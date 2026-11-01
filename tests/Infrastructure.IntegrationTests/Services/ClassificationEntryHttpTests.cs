using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using API.Configuration;
using API.Controllers.Core;
using API.Middleware;
using API.Services;
using Application.Interfaces.Services;
using Application.Services;
using Application.Taxonomy;
using Common;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;


namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class ClassificationEntryHttpTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	private Guid seedRoot;
	private const string Actor = "classification-http-admin";
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }
	private sealed class ActorAccessor : Application.Interfaces.Services.ICurrentUserAccessor
	{
		public string? UserId => Actor;
		public Guid? ApiKeyId => null;
		public string? IpAddress => null;
		public string? UserAgent => null;
	}
	private sealed class ActorFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => new(fixture.CreateOptions(), new ActorAccessor()); }

	// Supplies identities at the authentication boundary; production authorization
	// policies, MVC serialization/validation, controller and database service remain real.
	private sealed class TestIdentity(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
			: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
	{
		protected override Task<AuthenticateResult> HandleAuthenticateAsync()
		{
			string role = Request.Headers["X-Test-Role"].ToString();
			if (role.Length == 0) { return Task.FromResult(AuthenticateResult.NoResult()); }
			ClaimsIdentity identity = new([new(ClaimTypes.NameIdentifier, Actor), new(ClaimTypes.Role, role)], Scheme.Name);
			return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
		}
	}

	private async Task<WebApplication> CreateHostAsync(IEntityChangeNotifier notifier, bool enabled = true)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration).RegisterProgramServices().RegisterApplicationServices(builder.Configuration);
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 40) });
		builder.Services.AddAuthServices(builder.Configuration);
		builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestIdentity>("TestIdentity", _ => { });
		builder.Services.PostConfigure<PolicySchemeOptions>(AuthConfiguration.PolicySchemeName, options => options.ForwardDefaultSelector = _ => "TestIdentity");
		builder.Services.Configure<ClassificationOptions>(options => options.WritesEnabled = enabled);
		builder.Services.AddControllers().AddApplicationPart(typeof(ReceiptsController).Assembly);
		builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new ActorFactory(fixture));
		builder.Services.AddSingleton<ReceiptMapper>().AddSingleton<ReceiptItemMapper>().AddSingleton<TransactionMapper>().AddSingleton<AdjustmentMapper>().AddSingleton<AccountMapper>();
		builder.Services.AddScoped<IReceiptRepository, ReceiptRepository>().AddScoped<IReceiptItemRepository, ReceiptItemRepository>().AddScoped<ITransactionRepository, TransactionRepository>().AddScoped<IAdjustmentRepository, AdjustmentRepository>();
		builder.Services.AddScoped<IReceiptService, ReceiptService>().AddScoped<IReceiptItemService, ReceiptItemService>().AddScoped<ITransactionService, TransactionService>().AddScoped<IAdjustmentService, AdjustmentService>().AddScoped<ICompleteReceiptWriter, CompleteReceiptWriter>();
		builder.Services.AddSingleton(notifier);
		WebApplication app = builder.Build();
		app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
		app.UseMiddleware<ValidationExceptionMiddleware>();
		app.UseAuthentication();
		app.UseAuthorization();
		app.MapControllers();
		await app.StartAsync();
		return app;
	}


	private async Task<TaxonomyRevision> SeedAsync()
	{
		await using var db = fixture.CreateDbContext();
		if (!await db.Users.AnyAsync(x => x.Id == Actor))
		{
			db.Users.Add(new ApplicationUser { Id = Actor, UserName = Actor });
			var role = new IdentityRole { Name = AppRoles.Admin };
			db.Roles.Add(role);
			db.UserRoles.Add(new() { UserId = Actor, RoleId = role.Id });
			await db.SaveChangesAsync();
		}
		var migration = new LegacyTaxonomyMigrationService(new Factory(fixture));
		if (!await db.LegacyMigrations.AnyAsync())
		{
			var plan = await migration.PreflightAsync(new("entry-http", Guid.NewGuid(), Actor, DateTimeOffset.UtcNow));
			await migration.ApplyAsync(LegacyMigrationFreeze.Source(plan));
		}
		var tree = new TaxonomyService(new Factory(fixture), TimeProvider.System);
		var head = (await tree.ReadAsync(null))!;
		var root = seedRoot = Guid.NewGuid(); var parent = Guid.NewGuid(); var leaf = Guid.NewGuid();
		return await tree.ChangeAsync(Actor, new(head.Id, Guid.NewGuid(), "HTTP entry fixture",
				[new TaxonomyEdit("create", root, Name: "Root " + root), new TaxonomyEdit("create", parent, root, "Parent"), new TaxonomyEdit("create", leaf, parent, "Leaf")]));
	}

	[Theory]
	[InlineData("no-choice")]
	[InlineData("clear")]
	[InlineData("root")]
	[InlineData("parent")]
	[InlineData("leaf")]
	public async Task CompleteAndItemRoutesAcceptExactEntryChoicesAndReplayAndClear(string choice)
	{
		var revision = await SeedAsync();
		var root = revision.Nodes.Single(x => x.Id == seedRoot);
		var parent = revision.Nodes.Single(x => x.ParentId == root.Id);
		var leaf = revision.Nodes.Single(x => x.ParentId == parent.Id);
		Guid? category = choice switch { "root" => root.Id, "parent" => parent.Id, "leaf" => leaf.Id, _ => null };
		var line = new Dictionary<string, object?>
		{
			["description"] = "HTTP " + choice,
			["quantity"] = 2,
			["unitPrice"] = 3.5,
			["category"] = category.HasValue ? root.Name : "",
			["subcategory"] = choice is "parent" or "leaf" ? parent.Name : null
		};
		if (choice != "no-choice")
		{
			line["classification"] = new { categoryId = category, taxonomyRevisionId = revision.Id, expectedAssignmentId = (Guid?)null, operationId = Guid.NewGuid(), reason = "Category selected during receipt entry" };
		}

		await using var app = await CreateHostAsync(Mock.Of<IEntityChangeNotifier>());
		using var client = app.GetTestClient();
		var complete = new { clientRequestId = Guid.NewGuid(), receipt = new { location = "HTTP entry", date = "2024-01-01", taxAmount = 0 }, items = new[] { line }, transactions = Array.Empty<object>(), adjustments = Array.Empty<object>() };
		(await client.PostAsJsonAsync("/api/receipts/complete", complete)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		(await client.PostAsJsonAsync($"/api/receipts/{Guid.NewGuid()}/receipt-items", line)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		(await client.PutAsJsonAsync($"/api/receipt-items/{Guid.NewGuid()}", line)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.User);
		var completeRoute = "/api/receipts/complete";
		using var response = await client.PostAsJsonAsync(completeRoute, complete);
		response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
		using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		var receipt = created.RootElement.GetProperty("receipt").GetProperty("id").GetGuid();
		var completeItem = created.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();
		using var replay = await client.PostAsJsonAsync(completeRoute, complete);
		replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());
		using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
		replayJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(completeItem);
		var itemRoute = $"/api/receipts/{receipt}/receipt-items?clientRequestId={Guid.NewGuid()}";
		// Separate operation identity for the standalone logical creation.
		if (choice != "no-choice")
		{
			line["classification"] = new { categoryId = category, taxonomyRevisionId = revision.Id, expectedAssignmentId = (Guid?)null, operationId = Guid.NewGuid(), reason = "Category selected during receipt entry" };
		}

		using var itemResponse = await client.PostAsJsonAsync(itemRoute, line);
		itemResponse.StatusCode.Should().Be(HttpStatusCode.OK, await itemResponse.Content.ReadAsStringAsync());
		using var itemJson = JsonDocument.Parse(await itemResponse.Content.ReadAsStringAsync());
		var itemId = itemJson.RootElement.GetProperty("id").GetGuid();
		using var itemReplay = await client.PostAsJsonAsync(itemRoute, line);
		itemReplay.StatusCode.Should().Be(HttpStatusCode.OK, await itemReplay.Content.ReadAsStringAsync());
		using var itemReplayJson = JsonDocument.Parse(await itemReplay.Content.ReadAsStringAsync());
		itemReplayJson.RootElement.GetProperty("id").GetGuid().Should().Be(itemId);
		var service = new ClassificationService(new Factory(fixture), TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = true }));
		foreach (var id in new[] { completeItem, itemId })
		{
			var history = await service.HistoryAsync(id, null, 10);
			history.Items.Should().ContainSingle().Which.CategoryId.Should().Be(category);
			history.Items.Single().Path.Select(x => x.Id).Should().Equal(choice switch { "root" => new[] { root.Id }, "parent" => new[] { root.Id, parent.Id }, "leaf" => new[] { root.Id, parent.Id, leaf.Id }, _ => Array.Empty<Guid>() });
			await using var readback = fixture.CreateDbContext();
			var persisted = await readback.ReceiptItems.SingleAsync(x => x.Id == id);
			// Request labels are retired and ignored: a new item stores none, whatever the request sent.
			persisted.Category.Should().BeEmpty(); persisted.Subcategory.Should().BeNull();
		}
		var assignment = (await service.HistoryAsync(itemId, null, 10)).Items.Single();
		line["id"] = itemId; line["description"] = "Facts only"; line.Remove("classification");
		using var facts = await client.PutAsJsonAsync($"/api/receipt-items/{itemId}?clientRequestId={Guid.NewGuid()}", line);
		facts.StatusCode.Should().Be(HttpStatusCode.NoContent, await facts.Content.ReadAsStringAsync());
		(await service.HistoryAsync(itemId, null, 10)).Items.Should().ContainSingle().Which.Id.Should().Be(assignment.Id);
		line["category"] = ""; line["subcategory"] = null;
		line["classification"] = new { categoryId = (Guid?)null, taxonomyRevisionId = revision.Id, expectedAssignmentId = assignment.Id, operationId = Guid.NewGuid(), reason = "Category selected during receipt entry" };
		var editRoute = $"/api/receipt-items/{itemId}?clientRequestId={Guid.NewGuid()}";
		foreach (var attempt in Enumerable.Range(0, 2))
		{
			using var clear = await client.PutAsJsonAsync(editRoute, line);
			clear.StatusCode.Should().Be(HttpStatusCode.NoContent, await clear.Content.ReadAsStringAsync());
		}
		(await service.HistoryAsync(itemId, null, 10)).Items.Should().HaveCount(2);
		line["description"] = "Conflicting facts";
		line["classification"] = new { categoryId = root.Id, taxonomyRevisionId = revision.Id, expectedAssignmentId = assignment.Id, operationId = Guid.NewGuid(), reason = "Stale opening baseline" };
		line["category"] = root.Name;
		using var conflict = await client.PutAsJsonAsync($"/api/receipt-items/{itemId}?clientRequestId={Guid.NewGuid()}", line);
		conflict.StatusCode.Should().Be(HttpStatusCode.Conflict, await conflict.Content.ReadAsStringAsync());
		conflict.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		await using var stored = fixture.CreateDbContext();
		var row = await stored.ReceiptItems.SingleAsync(x => x.Id == itemId);
		row.Description.Should().Be("Facts only"); row.Category.Should().BeEmpty(); row.Subcategory.Should().BeNull();
	}

	[Fact]
	public async Task RetiredRequestLabelsAreIgnoredOnCreateRetryEditAndExplicitChoice()
	{
		var revision = await SeedAsync();
		var root = revision.Nodes.Single(x => x.Id == seedRoot);
		var parent = revision.Nodes.Single(x => x.ParentId == root.Id);
		var leaf = revision.Nodes.Single(x => x.ParentId == parent.Id);
		await using var app = await CreateHostAsync(Mock.Of<IEntityChangeNotifier>());
		using var client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.User);
		var service = new ClassificationService(new Factory(fixture), TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = true }));
		async Task<ReceiptItemEntity> ReadItemAsync(Guid id)
		{
			await using var read = fixture.CreateDbContext();
			return await read.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == id);
		}
		Dictionary<string, object?> Line(string category, string? subcategory) => new()
		{
			["description"] = "Labelled " + Guid.NewGuid(),
			["quantity"] = 1,
			["unitPrice"] = 2,
			["category"] = category,
			["subcategory"] = subcategory,
		};

		// A complete create that names a resolvable path in its labels, and a retry relabelled differently.
		Guid completeRequest = Guid.NewGuid();
		var completeLine = Line(root.Name, parent.Name);
		object Complete(Dictionary<string, object?> line) => new { clientRequestId = completeRequest, receipt = new { location = "Labels ignored", date = "2024-01-01", taxAmount = 0 }, items = new[] { line }, transactions = Array.Empty<object>(), adjustments = Array.Empty<object>() };
		using var completed = await client.PostAsJsonAsync("/api/receipts/complete", Complete(completeLine));
		completed.StatusCode.Should().Be(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync());
		using var completedJson = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
		Guid receiptId = completedJson.RootElement.GetProperty("receipt").GetProperty("id").GetGuid();
		Guid completeItem = completedJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();
		using var completeRetry = await client.PostAsJsonAsync("/api/receipts/complete", Complete(new(completeLine) { ["category"] = "Relabelled", ["subcategory"] = "Retry" }));
		completeRetry.StatusCode.Should().Be(HttpStatusCode.OK, await completeRetry.Content.ReadAsStringAsync());
		using var completeRetryJson = JsonDocument.Parse(await completeRetry.Content.ReadAsStringAsync());
		completeRetryJson.RootElement.GetProperty("receipt").GetProperty("id").GetGuid().Should().Be(receiptId, "labels are not part of the request identity");

		// A standalone create with labels and no choice, retried with different labels under the same request ID.
		var line = Line(root.Name, parent.Name);
		var createRoute = $"/api/receipts/{receiptId}/receipt-items?clientRequestId={Guid.NewGuid()}";
		using var created = await client.PostAsJsonAsync(createRoute, line);
		created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
		using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
		Guid itemId = createdJson.RootElement.GetProperty("id").GetGuid();
		using var createRetry = await client.PostAsJsonAsync(createRoute, new Dictionary<string, object?>(line) { ["category"] = "Relabelled", ["subcategory"] = null });
		createRetry.StatusCode.Should().Be(HttpStatusCode.OK, await createRetry.Content.ReadAsStringAsync());
		using var createRetryJson = JsonDocument.Parse(await createRetry.Content.ReadAsStringAsync());
		createRetryJson.RootElement.GetProperty("id").GetGuid().Should().Be(itemId, "labels are not part of the request identity");

		foreach (Guid id in new[] { completeItem, itemId })
		{
			ReceiptItemEntity stored = await ReadItemAsync(id);
			stored.Category.Should().BeEmpty(); stored.Subcategory.Should().BeNull();
			var observed = (await service.HistoryAsync(id, null, 10)).Items.Should().ContainSingle().Subject;
			observed.Source.Should().Be("legacy_write");
			observed.CategoryId.Should().BeNull("labels no longer resolve to a path");
			observed.LegacyResolution.Should().Be("unresolved_path");
			observed.Reason.Should().Be("Saved without a category choice.");
			observed.LegacyCategory.Should().BeEmpty(); observed.LegacySubcategory.Should().BeNull();
		}
		var first = (await service.HistoryAsync(itemId, null, 10)).Items.Single();

		// An edit that relabels without a choice changes neither the stored labels nor the item type.
		line["id"] = itemId; line["category"] = leaf.Name; line["subcategory"] = "Other";
		using var relabelled = await client.PutAsJsonAsync($"/api/receipt-items/{itemId}?clientRequestId={Guid.NewGuid()}", line);
		relabelled.StatusCode.Should().Be(HttpStatusCode.NoContent, await relabelled.Content.ReadAsStringAsync());
		(await service.HistoryAsync(itemId, null, 10)).Items.Should().ContainSingle().Which.Id.Should().Be(first.Id);
		(await ReadItemAsync(itemId)).Category.Should().BeEmpty();

		// An explicit choice whose path disagrees with the sent labels is accepted; the labels stay as stored.
		line["category"] = "Contradiction"; line["subcategory"] = "Nope";
		line["classification"] = new { categoryId = leaf.Id, taxonomyRevisionId = revision.Id, expectedAssignmentId = first.Id, operationId = Guid.NewGuid(), reason = "Chosen against the labels" };
		using var chosen = await client.PutAsJsonAsync($"/api/receipt-items/{itemId}?clientRequestId={Guid.NewGuid()}", line);
		chosen.StatusCode.Should().Be(HttpStatusCode.NoContent, await chosen.Content.ReadAsStringAsync());
		var history = (await service.HistoryAsync(itemId, null, 10)).Items;
		history.Should().HaveCount(2);
		history[0].CategoryId.Should().Be(leaf.Id);
		history[0].Source.Should().Be("manual");
		ReceiptItemEntity afterChoice = await ReadItemAsync(itemId);
		afterChoice.Category.Should().BeEmpty(); afterChoice.Subcategory.Should().BeNull();
	}

	// Raw JSON, not DTOs, so the label keys are really absent (or really null) on the wire.
	private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

	[Theory]
	[InlineData("omitted", "")]
	[InlineData("null", "\"category\": null, \"subcategory\": null,")]
	public async Task RequestsWithoutLabelsSucceedOnCompleteCreateAndUpdate(string shape, string labels)
	{
		await SeedAsync();
		await using var app = await CreateHostAsync(Mock.Of<IEntityChangeNotifier>());
		using var client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.User);
		async Task<ReceiptItemEntity> ReadItemAsync(Guid id)
		{
			await using var read = fixture.CreateDbContext();
			return await read.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == id);
		}

		// (a) A complete receipt whose item carries no labels.
		string completeBody = $$"""
			{ "clientRequestId": "{{Guid.NewGuid()}}",
			  "receipt": { "location": "No labels {{shape}}", "date": "2024-01-01", "taxAmount": 0 },
			  "items": [ { {{labels}} "description": "Complete {{shape}}", "quantity": 1, "unitPrice": 2 } ],
			  "transactions": [], "adjustments": [] }
			""";
		using var completed = await client.PostAsync("/api/receipts/complete", Json(completeBody));
		completed.StatusCode.Should().Be(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync());
		using var completedJson = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
		Guid receiptId = completedJson.RootElement.GetProperty("receipt").GetProperty("id").GetGuid();
		Guid completeItem = completedJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();

		// (b) A standalone item create with no labels.
		string createBody = $$"""{ {{labels}} "description": "Item {{shape}}", "quantity": 2, "unitPrice": 1.5 }""";
		using var created = await client.PostAsync($"/api/receipts/{receiptId}/receipt-items?clientRequestId={Guid.NewGuid()}", Json(createBody));
		created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
		using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
		Guid itemId = createdJson.RootElement.GetProperty("id").GetGuid();

		foreach (Guid id in new[] { completeItem, itemId })
		{
			ReceiptItemEntity stored = await ReadItemAsync(id);
			stored.Category.Should().BeEmpty(); stored.Subcategory.Should().BeNull();
		}

		// (c) An update with no labels keeps the labels already stored (historical rows carry real ones).
		await using (var db = fixture.CreateDbContext())
		{
			await db.Database.ExecuteSqlInterpolatedAsync(
				$"UPDATE receipts.\"ReceiptItems\" SET \"Category\" = {"Historical"}, \"Subcategory\" = {"Kept"} WHERE \"Id\" = {itemId}");
		}
		var before = (await new ClassificationService(new Factory(fixture), TimeProvider.System,
			Options.Create(new ClassificationOptions { WritesEnabled = true })).HistoryAsync(itemId, null, 10)).Items;
		string updateBody = $$"""{ "id": "{{itemId}}", {{labels}} "description": "Updated {{shape}}", "quantity": 3, "unitPrice": 1.5 }""";
		using var updated = await client.PutAsync($"/api/receipt-items/{itemId}?clientRequestId={Guid.NewGuid()}", Json(updateBody));
		updated.StatusCode.Should().Be(HttpStatusCode.NoContent, await updated.Content.ReadAsStringAsync());
		ReceiptItemEntity afterUpdate = await ReadItemAsync(itemId);
		afterUpdate.Description.Should().Be("Updated " + shape);
		afterUpdate.Quantity.Should().Be(3);
		afterUpdate.Category.Should().Be("Historical"); afterUpdate.Subcategory.Should().Be("Kept");
		(await new ClassificationService(new Factory(fixture), TimeProvider.System,
			Options.Create(new ClassificationOptions { WritesEnabled = true })).HistoryAsync(itemId, null, 10)).Items
			.Should().BeEquivalentTo(before, "an edit without a choice appends no assignment");
	}

	[Fact]
	public async Task ClassificationGateStillAllowsManualUnclassifiedCompleteSave()
	{
		await SeedAsync();
		await using var app = await CreateHostAsync(Mock.Of<IEntityChangeNotifier>(), false);
		using var client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.User);
		using var response = await client.PostAsJsonAsync("/api/receipts/complete", new
		{
			clientRequestId = Guid.NewGuid(),
			receipt = new { location = "Gated", date = "2024-01-01", taxAmount = 0 },
			items = new[] { new { description = "Manual", category = "", subcategory = (string?)null, quantity = 1, unitPrice = 1 } },
			transactions = Array.Empty<object>(),
			adjustments = Array.Empty<object>()
		});
		response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}
}




