using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using API.Configuration;
using API.Controllers;
using Application.Taxonomy;
using Common;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
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

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class ClassificationHttpTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
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

	private async Task<WebApplication> HostAsync()
	{
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			if (!await db.Users.AnyAsync(user => user.Id == Actor))
			{
				db.Users.Add(new ApplicationUser { Id = Actor, UserName = Actor });
				IdentityRole? role = await db.Roles.FirstOrDefaultAsync(role => role.Name == AppRoles.Admin);
				if (role == null) { role = new() { Name = AppRoles.Admin }; db.Roles.Add(role); }
				db.UserRoles.Add(new() { UserId = Actor, RoleId = role.Id });
				await db.SaveChangesAsync();
			}
		}
		var migration = new LegacyTaxonomyMigrationService(new Factory(fixture));
		await using (var db = fixture.CreateDbContext())
		{
			if (!await db.LegacyMigrations.AnyAsync())
			{
				var plan = await migration.PreflightAsync(new("classification-http-fixture", Guid.NewGuid(), Actor, DateTimeOffset.UtcNow));
				await migration.ApplyAsync(LegacyMigrationFreeze.Source(plan));
			}
		}
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Configuration.Sources.Clear();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 40) });
		builder.Services.AddAuthServices(builder.Configuration);
		builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestIdentity>("TestIdentity", _ => { });
		builder.Services.PostConfigure<PolicySchemeOptions>(AuthConfiguration.PolicySchemeName, options => options.ForwardDefaultSelector = _ => "TestIdentity");
		builder.Services.AddApplicationServices(builder.Configuration);
		builder.Services.AddControllers().AddApplicationPart(typeof(TaxonomyController).Assembly);
		builder.Services.AddSingleton<IClassificationService>(new ClassificationService(new Factory(fixture), TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = true })));
		builder.Services.AddSingleton<ITaxonomyService>(new TaxonomyService(new Factory(fixture), TimeProvider.System));
		WebApplication app = builder.Build();
		app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
		await app.StartAsync();
		return app;
	}

	[Fact]
	public async Task AssignmentRequiresAuthenticationAndNullableFieldPresenceAndReturnsReasonedProblems()
	{
		await using WebApplication app = await HostAsync();
		using HttpClient client = app.GetTestClient();
		Guid item = Guid.NewGuid();
		string route = $"/api/receipt-items/{item}/classification";
		StringContent Body(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
		(await client.PutAsync(route, Body(new { }))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		(await client.GetAsync($"/api/receipt-items/{item}/classification-history")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.User);
		Guid revision = (await new TaxonomyService(new Factory(fixture), TimeProvider.System).ReadAsync(null))!.Id;
		object[] missing = [
			new { taxonomyRevisionId = revision, expectedAssignmentId = (Guid?)null, operationId = Guid.NewGuid(), reason = "Missing category" },
			new { categoryId = (Guid?)null, taxonomyRevisionId = revision, operationId = Guid.NewGuid(), reason = "Missing expected assignment" }
		];
		foreach (object value in missing)
		{
			using var response = await client.PutAsync(route, Body(value));
			response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
			response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		}
		using var unknown = await client.PutAsync(route, Body(new
		{
			categoryId = (Guid?)null,
			taxonomyRevisionId = revision,
			expectedAssignmentId = (Guid?)null,
			operationId = Guid.NewGuid(),
			reason = "Explicit null"
		}));
		unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
		unknown.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using var problem = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
		problem.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public async Task LegacyHistoryHttpMappingPreservesExactEvidenceAndManualClear(string? subcategory)
	{
		await using WebApplication app = await HostAsync();
		using HttpClient client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.User);
		await using var db = fixture.CreateDbContext();
		var receipt = new ReceiptEntity { Id = Guid.NewGuid(), Location = "HTTP evidence", Date = new(2020, 1, 1) };
		db.Receipts.Add(receipt); await db.SaveChangesAsync();
		var item = new ReceiptItemEntity
		{
			Id = Guid.NewGuid(),
			ReceiptId = receipt.Id,
			Category = "  unknown HTTP  ",
			Subcategory = subcategory,
			Description = "Preserved evidence",
			Quantity = 1,
			UnitPrice = 2,
			TotalAmount = 2
		};
		await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync([item], default);
		var service = new ClassificationService(new Factory(fixture), TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = true }));
		var observed = (await service.HistoryAsync(item.Id, null, 10)).Items.Single();
		using var response = await client.GetAsync($"/api/receipt-items/{item.Id}/classification-history");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		using var history = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		var row = history.RootElement.GetProperty("items").EnumerateArray().Single();
		row.GetProperty("id").GetGuid().Should().Be(observed.Id);
		row.GetProperty("receiptItemId").GetGuid().Should().Be(item.Id);
		row.GetProperty("sequence").GetInt64().Should().Be(1);
		row.GetProperty("previousAssignmentId").ValueKind.Should().Be(JsonValueKind.Null);
		row.GetProperty("categoryId").ValueKind.Should().Be(JsonValueKind.Null);
		row.GetProperty("taxonomyRevisionId").GetGuid().Should().Be(observed.TaxonomyRevisionId);
		row.GetProperty("recordedAt").GetDateTimeOffset().Should().Be(observed.RecordedAt);
		row.GetProperty("actor").GetString().Should().Be(Actor);
		row.GetProperty("source").GetString().Should().Be("legacy_write");
		row.GetProperty("operationId").GetGuid().Should().Be(observed.OperationId);
		row.GetProperty("reason").GetString().Should().Be(observed.Reason);
		row.GetProperty("legacyCategory").GetString().Should().Be(item.Category);
		row.GetProperty("legacySubcategory").GetString().Should().Be(subcategory);
		row.GetProperty("legacyResolution").GetString().Should().Be("unresolved_path");
		row.GetProperty("path").GetArrayLength().Should().Be(0);
		history.RootElement.GetProperty("nextBeforeSequence").ValueKind.Should().Be(JsonValueKind.Null);

		int identities = await db.TaxonomyIdentities.CountAsync();
		using var clear = await client.PutAsync($"/api/receipt-items/{item.Id}/classification", new StringContent(JsonSerializer.Serialize(new
		{
			categoryId = (Guid?)null,
			taxonomyRevisionId = observed.TaxonomyRevisionId,
			expectedAssignmentId = observed.Id,
			operationId = Guid.NewGuid(),
			reason = "Intentional clear"
		}), Encoding.UTF8, "application/json"));
		clear.StatusCode.Should().Be(HttpStatusCode.OK);
		using var cleared = JsonDocument.Parse(await clear.Content.ReadAsStringAsync());
		cleared.RootElement.GetProperty("source").GetString().Should().Be("manual");
		cleared.RootElement.GetProperty("categoryId").ValueKind.Should().Be(JsonValueKind.Null);
		cleared.RootElement.GetProperty("previousAssignmentId").GetGuid().Should().Be(observed.Id);
		cleared.RootElement.GetProperty("sequence").GetInt64().Should().Be(2);
		cleared.RootElement.GetProperty("path").GetArrayLength().Should().Be(0);
		(await db.TaxonomyIdentities.CountAsync()).Should().Be(identities);
		(await service.HistoryAsync(item.Id, null, 10)).Items.Last().Should().BeEquivalentTo(observed);
	}
}
