using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using API.Configuration;
using API.Controllers;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	private sealed class HistoricalHttpIdentity(IOptionsMonitor<AuthenticationSchemeOptions> options,
			ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
	{
		protected override Task<AuthenticateResult> HandleAuthenticateAsync()
		{
			string actor = Request.Headers["X-Test-Actor"].ToString();
			if (actor.Length == 0) { return Task.FromResult(AuthenticateResult.NoResult()); }
			ClaimsIdentity identity = new([new(ClaimTypes.NameIdentifier, actor), new(ClaimTypes.Role, AppRoles.User)], Scheme.Name);
			return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
		}
	}

	private async Task<WebApplication> HistoricalHttpHostAsync()
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Configuration.Sources.Clear();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 40) });
		builder.Services.AddAuthServices(builder.Configuration);
		builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HistoricalHttpIdentity>("HistoricalHttpIdentity", _ => { });
		builder.Services.PostConfigure<PolicySchemeOptions>(AuthConfiguration.PolicySchemeName,
				options => options.ForwardDefaultSelector = _ => "HistoricalHttpIdentity");
		builder.Services.AddApplicationServices(builder.Configuration);
		builder.Services.AddControllers().AddApplicationPart(typeof(HistoricalReclassificationsController).Assembly);
		builder.Services.AddSingleton<Application.Taxonomy.IHistoricalOperationService>(Historical());
		builder.Services.AddSingleton<Application.Taxonomy.IHistoricalGroupReader>(
			new Infrastructure.Services.HistoricalGroupReader(new Factory(fixture)));
		WebApplication app = builder.Build();
		app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
		await app.StartAsync();
		return app;
	}

	private static StringContent HistoricalBody(object value) =>
			new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

	private static async Task<JsonElement> HistoricalJson(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
	{
		string body = await response.Content.ReadAsStringAsync();
		response.StatusCode.Should().Be(status, body);
		if (status != HttpStatusCode.OK)
		{
			response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
			JsonDocument.Parse(body).RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
		}
		return JsonDocument.Parse(body).RootElement.Clone();
	}

	[Fact]
	public async Task HistoricalHttpOwnsIntentPagesEvidenceAndReplaysBoundedWork()
	{
		var seed = await HistoricalSeedAsync();
		var intent = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await using WebApplication app = await HistoricalHttpHostAsync();
		using HttpClient client = app.GetTestClient();
		const string route = "/api/historical-reclassifications";
		object body = new
		{
			intent.OperationId,
			intent.TaxonomyRevisionId,
			intent.Reason,
			groups = intent.Groups.Select(x => new { group = x.Group, x.TargetCategoryId }).ToArray()
		};
		(await client.PostAsync(route, HistoricalBody(body))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		client.DefaultRequestHeaders.Add("X-Test-Actor", Actor);
		await HistoricalJson(await client.PostAsync(route, HistoricalBody(new
		{
			intent.OperationId,
			intent.TaxonomyRevisionId,
			intent.Reason,
			groups = Array.Empty<object>()
		})), HttpStatusCode.BadRequest);
		await HistoricalJson(await client.PostAsync(route, HistoricalBody(new
		{
			operationId = Guid.NewGuid(),
			intent.TaxonomyRevisionId,
			intent.Reason,
			groups = new[] { new { group = intent.Groups[0].Group } }
		})), HttpStatusCode.BadRequest);
		await HistoricalJson(await client.PostAsync(route, HistoricalBody(new
		{
			intent.OperationId,
			intent.TaxonomyRevisionId,
			intent.Reason,
			groups = Enumerable.Range(0, 26).Select(_ => intent.Groups[0]).ToArray()
		})), HttpStatusCode.BadRequest);
		Guid emptyOperationId = Guid.NewGuid();
		JsonElement emptyPreview = await HistoricalJson(await client.PostAsync(route, HistoricalBody(new
		{
			operationId = emptyOperationId,
			intent.TaxonomyRevisionId,
			intent.Reason,
			groups = new[] { new
						{ group = new { store = "Absent store", rawDescription = "Absent item", itemCode = (string?)null },
								targetCategoryId = (Guid?)seed.Root } }
		})));
		emptyPreview.GetProperty("total").GetInt32().Should().Be(0);
		(await HistoricalJson(await client.PostAsync($"{route}/{emptyOperationId}/apply",
						HistoricalBody(new { limit = 1 })))).GetProperty("state").GetString().Should().Be("completed");
		JsonElement preview = await HistoricalJson(await client.PostAsync(route, HistoricalBody(body)));
		preview.GetProperty("total").GetInt32().Should().Be(1);
		preview.GetProperty("pending").GetInt32().Should().Be(1);
		(await HistoricalJson(await client.PostAsync(route, HistoricalBody(body)))).GetProperty("operationId").GetGuid()
				.Should().Be(intent.OperationId);
		await HistoricalJson(await client.PostAsync(route, HistoricalBody(new
		{ intent.OperationId, intent.TaxonomyRevisionId, reason = "changed", groups = intent.Groups })), HttpStatusCode.Conflict);
		string operation = $"{route}/{intent.OperationId}";
		await HistoricalJson(await client.GetAsync(operation + "/items?limit=101"), HttpStatusCode.BadRequest);
		JsonElement page = await HistoricalJson(await client.GetAsync(operation + "/items?offset=0&limit=1"));
		JsonElement item = page.GetProperty("items")[0].GetProperty("item");
		item.GetProperty("itemId").GetGuid().Should().Be(seed.Item);
		item.GetProperty("expectedAssignmentId").GetGuid().Should().Be(seed.Assignment);
		page.GetProperty("nextOffset").ValueKind.Should().Be(JsonValueKind.Null);
		JsonElement applied = await HistoricalJson(await client.PostAsync(operation + "/apply", HistoricalBody(new { limit = 1 })));
		applied.GetProperty("applied").GetInt32().Should().Be(1);
		(await HistoricalJson(await client.PostAsync(operation + "/apply", HistoricalBody(new { limit = 1 }))))
				.GetProperty("applied").GetInt32().Should().Be(1);
		await HistoricalJson(await client.PostAsync(operation + "/cancel", null), HttpStatusCode.Conflict);
		JsonElement undone = await HistoricalJson(await client.PostAsync(operation + "/undo",
				HistoricalBody(new { taxonomyRevisionId = seed.Revision, reason = "Restore", limit = 1 })));
		undone.GetProperty("undone").GetInt32().Should().Be(1);
		await HistoricalJson(await client.PostAsync(operation + "/undo",
				HistoricalBody(new { taxonomyRevisionId = seed.Revision, reason = "Changed", limit = 1 })), HttpStatusCode.Conflict);
		Guid cancelledId = Guid.NewGuid();
		await HistoricalJson(await client.PostAsync(route, HistoricalBody(new
		{
			operationId = cancelledId,
			intent.TaxonomyRevisionId,
			intent.Reason,
			groups = intent.Groups.Select(x => new { group = x.Group, x.TargetCategoryId })
		})));
		JsonElement cancelled = await HistoricalJson(await client.PostAsync($"{route}/{cancelledId}/cancel", null));
		cancelled.GetProperty("state").GetString().Should().Be("cancelled");
		cancelled.GetProperty("pending").GetInt32().Should().Be(1);
		JsonElement remaining = await HistoricalJson(await client.GetAsync($"{route}/{cancelledId}/items?offset=1&limit=1"));
		remaining.GetProperty("items").GetArrayLength().Should().Be(0);
		await HistoricalJson(await client.PostAsync($"{route}/{cancelledId}/apply", HistoricalBody(new { limit = 1 })));
		client.DefaultRequestHeaders.Remove("X-Test-Actor");
		client.DefaultRequestHeaders.Add("X-Test-Actor", "stranger");
		await HistoricalJson(await client.GetAsync(operation), HttpStatusCode.NotFound);
		await HistoricalJson(await client.GetAsync(operation + "/items"), HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task HistoricalHttpRequiresNullableFieldsButAcceptsExplicitNullIntent()
	{
		var seed = await HistoricalSeedAsync();
		await using (var db = fixture.CreateDbContext())
		{
			ReceiptItemEntity item = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
			item.ReceiptItemCode = null;
			await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture))
					.UpdateAsync([item], default);
		}
		var classified = await Service().AssignAsync(Actor, seed.Item,
				new(seed.Root, seed.Revision, seed.Assignment, Guid.NewGuid(), "Classify before explicit clear"));
		var intent = await HistoricalRequest(seed.Item, seed.Revision, null);
		await using WebApplication app = await HistoricalHttpHostAsync();
		using HttpClient client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("X-Test-Actor", Actor);
		const string route = "/api/historical-reclassifications";
		var group = intent.Groups[0].Group;
		object withoutCode = new
		{
			operationId = Guid.NewGuid(),
			intent.TaxonomyRevisionId,
			intent.Reason,
			groups = new[] { new { group = new { group.Store, group.RawDescription }, targetCategoryId = (Guid?)null } }
		};
		await HistoricalJson(await client.PostAsync(route, HistoricalBody(withoutCode)), HttpStatusCode.BadRequest);
		object explicitNulls = new
		{
			intent.OperationId,
			intent.TaxonomyRevisionId,
			intent.Reason,
			groups = new[] { new { group = new { group.Store, group.RawDescription, itemCode = (string?)null },
								targetCategoryId = (Guid?)null } }
		};
		JsonElement preview = await HistoricalJson(await client.PostAsync(route, HistoricalBody(explicitNulls)));
		preview.GetProperty("total").GetInt32().Should().Be(1);
		JsonElement frozen = await HistoricalJson(await client.GetAsync($"{route}/{intent.OperationId}/items"));
		frozen.GetProperty("items")[0].GetProperty("item").GetProperty("targetCategoryId").ValueKind
				.Should().Be(JsonValueKind.Null);
		frozen.GetProperty("items")[0].GetProperty("item").GetProperty("expectedAssignmentId").GetGuid()
				.Should().Be(classified.Id);
		JsonElement applied = await HistoricalJson(await client.PostAsync($"{route}/{intent.OperationId}/apply",
				HistoricalBody(new { limit = 1 })));
		applied.GetProperty("applied").GetInt32().Should().Be(1);
		(await Service().HistoryAsync(seed.Item, null, 1)).Items.Single().CategoryId.Should().BeNull();
		(await HistoricalJson(await client.PostAsync(route, HistoricalBody(explicitNulls))))
				.GetProperty("applied").GetInt32().Should().Be(1);
	}

	[Fact]
	public async Task HistoricalHttpPagesEveryFrozenItemInASelectedExactGroup()
	{
		var seed = await HistoricalSeedAsync();
		await using (var db = fixture.CreateDbContext())
		{
			ReceiptItemEntity original = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
			ReceiptItemEntity sibling = new()
			{
				Id = Guid.NewGuid(),
				ReceiptId = original.ReceiptId,
				Description = original.Description,
				ReceiptItemCode = original.ReceiptItemCode,
				Category = original.Category,
				Subcategory = original.Subcategory,
				Quantity = 1,
				UnitPrice = 1,
				TotalAmount = 1,
				UnitPriceCurrency = original.UnitPriceCurrency,
				TotalAmountCurrency = original.TotalAmountCurrency
			};
			await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture))
					.CreateAsync([sibling], default);
		}
		var intent = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await using WebApplication app = await HistoricalHttpHostAsync();
		using HttpClient client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("X-Test-Actor", Actor);
		JsonElement preview = await HistoricalJson(await client.PostAsync("/api/historical-reclassifications",
				HistoricalBody(new
				{
					intent.OperationId,
					intent.TaxonomyRevisionId,
					intent.Reason,
					groups = intent.Groups.Select(x => new { group = x.Group, x.TargetCategoryId })
				})));
		preview.GetProperty("total").GetInt32().Should().Be(2);
		string itemsRoute = $"/api/historical-reclassifications/{intent.OperationId}/items";
		JsonElement first = await HistoricalJson(await client.GetAsync(itemsRoute + "?offset=0&limit=1"));
		JsonElement second = await HistoricalJson(await client.GetAsync(itemsRoute + "?offset=1&limit=1"));
		first.GetProperty("nextOffset").GetInt32().Should().Be(1);
		second.GetProperty("nextOffset").ValueKind.Should().Be(JsonValueKind.Null);
		first.GetProperty("items")[0].GetProperty("item").GetProperty("itemId").GetGuid()
				.Should().NotBe(second.GetProperty("items")[0].GetProperty("item").GetProperty("itemId").GetGuid());
	}
}
