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

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class CategoryMappingHttpTests(PostgresFixture fixture)
{
	private const string Actor = "mapping-http-admin";
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }

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
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Configuration.Sources.Clear();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 40) });
		builder.Services.AddAuthServices(builder.Configuration);
		builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestIdentity>("TestIdentity", _ => { });
		builder.Services.PostConfigure<PolicySchemeOptions>(AuthConfiguration.PolicySchemeName, options => options.ForwardDefaultSelector = _ => "TestIdentity");
		builder.Services.AddApplicationServices(builder.Configuration);
		builder.Services.AddControllers().AddApplicationPart(typeof(TaxonomyController).Assembly);
		builder.Services.AddSingleton<ICategoryMappingService>(new CategoryMappingService(new Factory(fixture), TimeProvider.System));
		builder.Services.AddSingleton<ITaxonomyService>(new TaxonomyService(new Factory(fixture), TimeProvider.System));
		WebApplication app = builder.Build();
		app.UseMiddleware<API.Middleware.ValidationExceptionMiddleware>();
		app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
		await app.StartAsync();
		return app;
	}

	private static StringContent Body(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
	private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
	{
		string body = await response.Content.ReadAsStringAsync();
		response.StatusCode.Should().Be(status, body);
		if (status != HttpStatusCode.OK) { response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json"); }
		return JsonDocument.Parse(body).RootElement.Clone();
	}

	[Fact]
	public async Task SourceTransportRejectsMalformedValuesAndPreservesExactNormalization()
	{
		await using WebApplication app = await HostAsync();
		using HttpClient client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.Admin);
		object[] invalidSources = [
			null!, new { description = "Item" }, new { location = "Shop" },
			new { location = "Shop", description = "Item", itemCode = "bad\0code" },
			new { location = "bad\0shop", description = "Item" },
			new { location = new string('L', 1001), description = "Item" },
			new { location = "Shop", description = new string('D', 2001) },
			new { location = "Shop", description = "Item", itemCode = new string('C', 201) }
		];
		foreach (object source in invalidSources)
		{
			foreach (string route in new[] { "inspect", "history" })
			{
				await Json(await client.PostAsync($"/api/category-mappings/{route}",
					Body(new { source, limit = 1 })), HttpStatusCode.BadRequest);
			}
		}
		// Inclusive boundaries are accepted; rejecting overlong inputs must not reject valid ones.
		await Json(await client.PostAsync("/api/category-mappings/inspect", Body(new
		{ source = new { location = new string('L', 1000), description = new string('D', 2000), itemCode = new string('C', 200) } })));
		await Json(await client.PostAsync("/api/category-mappings/history", Body(new { source = new { location = "Shop", description = "Item" } })), HttpStatusCode.BadRequest);

		async Task<string> Fingerprint(object source)
		{
			JsonElement inspected = await Json(await client.PostAsync("/api/category-mappings/inspect", Body(new { source })));
			return inspected.GetProperty("sourceFingerprint").GetString()!;
		}
		string fingerprint = await Fingerprint(new { location = "Shop", description = "Item", itemCode = (string?)null });
		(await Fingerprint(new { location = " Shop ", description = " Item ", itemCode = "  " })).Should().Be(fingerprint);
		foreach (object distinct in new object[] {
			new { location = "\tShop", description = "Item" },
			new { location = "\u00a0Shop", description = "Item" },
			new { location = "Shop", description = "\tItem" },
			new { location = "Shop", description = "Item", itemCode = "\t" } })
		{ (await Fingerprint(distinct)).Should().NotBe(fingerprint); }

		JsonElement initial = await Json(await client.PostAsync("/api/category-mappings/inspect", Body(new { source = new { location = "Shop", description = "Item" } })));
		await Json(await client.PostAsync("/api/category-mappings/changes", Body(new
		{
			source = new { location = "Shop", description = "Item" },
			sourceFingerprint = fingerprint,
			expectedMappingRevisionId = (Guid?)null,
			expectedTaxonomyRevisionId = initial.GetProperty("taxonomyRevisionId").GetGuid(),
			category = new { id = Guid.NewGuid() },
			operationId = Guid.NewGuid(),
			reason = new string('R', 4001)
		})), HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task AuthorizationValidationExactApprovalReplayRevocationAndHistory()
	{
		await using WebApplication app = await HostAsync();
		using HttpClient client = app.GetTestClient();
		var source = new { location = "Mapping HTTP", description = "Mixed " + Guid.NewGuid(), itemCode = "CODE" };
		foreach (string route in new[] { "inspect", "history", "changes" })
		{ (await client.PostAsync($"/api/category-mappings/{route}", Body(new { source }))).StatusCode.Should().Be(HttpStatusCode.Unauthorized); }
		client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.User);
		JsonElement initial = await Json(await client.PostAsync("/api/category-mappings/inspect", Body(new { source })));
		initial.GetProperty("disposition").GetString().Should().Be("unmapped");
		await Json(await client.PostAsync("/api/category-mappings/history", Body(new { source, limit = 1 })));
		(await client.PostAsync("/api/category-mappings/changes", Body(new { source }))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
		client.DefaultRequestHeaders.Remove("X-Test-Role"); client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.Admin);
		Guid taxonomyId = initial.GetProperty("taxonomyRevisionId").GetGuid();
		Guid categoryId = Guid.NewGuid();
		JsonElement taxonomy = await Json(await client.PostAsync("/api/taxonomy/changes", Body(new
		{ expectedRevisionId = taxonomyId, operationId = Guid.NewGuid(), reason = "HTTP mapping setup", changes = new[] { new { kind = "create", categoryId, name = "Mapping " + categoryId.ToString("N") } } })));
		taxonomyId = taxonomy.GetProperty("id").GetGuid();
		string fingerprint = initial.GetProperty("sourceFingerprint").GetString()!;
		Dictionary<string, object?> command = new()
		{
			["source"] = source,
			["sourceFingerprint"] = fingerprint,
			["expectedMappingRevisionId"] = null,
			["expectedTaxonomyRevisionId"] = taxonomyId,
			["category"] = new { id = categoryId },
			["operationId"] = Guid.NewGuid(),
			["reason"] = "Explicit future reuse",
			["actor"] = "forged"
		};
		foreach (string missing in new[] { "source", "category", "expectedMappingRevisionId" })
		{
			var invalid = new Dictionary<string, object?>(command); invalid.Remove(missing);
			await Json(await client.PostAsync("/api/category-mappings/changes", Body(invalid)), HttpStatusCode.BadRequest);
		}
		foreach ((string key, object? value) in new (string, object?)[]
		{ ("operationId", Guid.Empty), ("expectedMappingRevisionId", Guid.Empty), ("expectedTaxonomyRevisionId", Guid.Empty), ("category", new { id = Guid.Empty }), ("reason", "   "), ("reason", "bad\0reason"),
		  ("source", new { location = "shop", description = "bad\0source" }), ("source", new { location = "shop", description = "   " }) })
		{
			var invalid = new Dictionary<string, object?>(command) { [key] = value };
			await Json(await client.PostAsync("/api/category-mappings/changes", Body(invalid)), HttpStatusCode.BadRequest);
		}
		JsonElement approval = await Json(await client.PostAsync("/api/category-mappings/changes", Body(command)));
		approval.GetProperty("actor").GetString().Should().Be(Actor);
		Guid approvedId = approval.GetProperty("id").GetGuid();
		JsonElement state = await Json(await client.PostAsync("/api/category-mappings/inspect", Body(new { source })));
		state.GetProperty("disposition").GetString().Should().Be("approved");
		foreach (object distinct in new object[] {
			new { location = source.location + "x", source.description, source.itemCode },
			new { source.location, description = source.description.ToUpperInvariant(), source.itemCode },
			new { source.location, source.description, itemCode = "OTHER" } })
		{
			JsonElement unmapped = await Json(await client.PostAsync("/api/category-mappings/inspect", Body(new { source = distinct })));
			unmapped.GetProperty("disposition").GetString().Should().Be("unmapped");
		}
		var stale = new Dictionary<string, object?>(command) { ["operationId"] = Guid.NewGuid() };
		JsonElement conflict = await Json(await client.PostAsync("/api/category-mappings/changes", Body(stale)), HttpStatusCode.Conflict);
		conflict.GetProperty("currentMappingRevisionId").GetGuid().Should().Be(approvedId);
		stale["expectedMappingRevisionId"] = approvedId; stale["expectedTaxonomyRevisionId"] = Guid.NewGuid();
		conflict = await Json(await client.PostAsync("/api/category-mappings/changes", Body(stale)), HttpStatusCode.Conflict);
		conflict.GetProperty("currentTaxonomyRevisionId").GetGuid().Should().Be(taxonomyId);
		stale["expectedTaxonomyRevisionId"] = taxonomyId; stale["sourceFingerprint"] = new string('A', 64);
		conflict = await Json(await client.PostAsync("/api/category-mappings/changes", Body(stale)), HttpStatusCode.Conflict);
		conflict.GetProperty("sourceFingerprint").GetString().Should().Be(fingerprint);
		var revoke = new Dictionary<string, object?>(command) { ["operationId"] = Guid.NewGuid(), ["category"] = null, ["expectedMappingRevisionId"] = approvedId };
		var missingCategory = new Dictionary<string, object?>(revoke); missingCategory.Remove("category");
		await Json(await client.PostAsync("/api/category-mappings/changes", Body(missingCategory)), HttpStatusCode.BadRequest);
		JsonElement revoked = await Json(await client.PostAsync("/api/category-mappings/changes", Body(revoke)));
		Guid revokedId = revoked.GetProperty("id").GetGuid();
		JsonElement replay = await Json(await client.PostAsync("/api/category-mappings/changes", Body(command)));
		replay.GetProperty("id").GetGuid().Should().Be(approvedId);
		var altered = new Dictionary<string, object?>(command) { ["reason"] = "Altered replay" };
		await Json(await client.PostAsync("/api/category-mappings/changes", Body(altered)), HttpStatusCode.Conflict);
		state = await Json(await client.PostAsync("/api/category-mappings/inspect", Body(new { source })));
		state.GetProperty("disposition").GetString().Should().Be("revoked");
		JsonElement page = await Json(await client.PostAsync("/api/category-mappings/history", Body(new { source, limit = 1 })));
		page.GetProperty("items").GetArrayLength().Should().Be(1);
		page.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(revokedId);
		page.GetProperty("nextBeforeRevisionId").GetGuid().Should().Be(revokedId);
		page = await Json(await client.PostAsync("/api/category-mappings/history", Body(new { source, limit = 1, beforeRevisionId = revokedId })));
		page.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(approvedId);
		page.GetProperty("nextBeforeRevisionId").ValueKind.Should().Be(JsonValueKind.Null);
		await Json(await client.PostAsync("/api/category-mappings/history", Body(new { source = new { location = "other", source.description }, limit = 1, beforeRevisionId = revokedId })), HttpStatusCode.BadRequest);
		foreach (int limit in new[] { 0, 101 })
		{ await Json(await client.PostAsync("/api/category-mappings/history", Body(new { source, limit })), HttpStatusCode.BadRequest); }
		await Json(await client.PostAsync("/api/category-mappings/history", Body(new { source, limit = 1, beforeRevisionId = Guid.Empty })), HttpStatusCode.BadRequest);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		ApplicationUser actor = await db.Users.SingleAsync(user => user.Id == Actor);
		actor.LockoutEnabled = true; actor.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1); await db.SaveChangesAsync();
		try { (await client.PostAsync("/api/category-mappings/changes", Body(command))).StatusCode.Should().Be(HttpStatusCode.Forbidden); }
		finally { actor.LockoutEnd = null; await db.SaveChangesAsync(); }
	}
}
