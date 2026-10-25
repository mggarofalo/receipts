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
public sealed class TaxonomyHttpTests(PostgresFixture fixture)
{
	private const string Actor = "taxonomy-http-admin";
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
		builder.Services.AddSingleton<ITaxonomyService>(new TaxonomyService(new Factory(fixture), TimeProvider.System));
		WebApplication app = builder.Build();
		app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
		await app.StartAsync();
		return app;
	}

	private static StringContent Body(Guid revision, object?[] changes, string reason = "HTTP acceptance") => new(JsonSerializer.Serialize(new
	{ expectedRevisionId = revision, operationId = Guid.NewGuid(), reason, changes }), Encoding.UTF8, "application/json");

	[Fact]
	public async Task AuthorizationSerializationValidationAndConflictUseTheHttpContract()
	{
		await using WebApplication app = await HostAsync();
		using HttpClient client = app.GetTestClient();
		(await client.GetAsync("/api/taxonomy")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		(await client.PostAsync("/api/taxonomy/changes", Body(Guid.NewGuid(), []))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.User);
		(await client.GetAsync("/api/taxonomy")).StatusCode.Should().Be(HttpStatusCode.OK);
		(await client.PostAsync("/api/taxonomy/changes", Body(Guid.NewGuid(), []))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
		client.DefaultRequestHeaders.Remove("X-Test-Role"); client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.Admin);
		using JsonDocument current = JsonDocument.Parse(await client.GetStringAsync("/api/taxonomy"));
		Guid revision = current.RootElement.GetProperty("id").GetGuid();
		object?[][] invalidChanges = [
			[new { categoryId = Guid.NewGuid(), name = "Omitted kind" }],
			[null],
			[new { kind = "create", categoryId = Guid.NewGuid(), name = new string('x', 201) }],
			[new { kind = "create", categoryId = Guid.NewGuid(), name = "Definition", definition = new string('x', 4001) }],
			[new { kind = "create", categoryId = Guid.NewGuid(), name = "Food\0" }],
			[new { kind = "create", categoryId = Guid.NewGuid(), name = "Food", definition = "Contains\0NUL" }]
		];
		foreach (object?[] changes in invalidChanges)
		{
			using HttpResponseMessage response = await client.PostAsync("/api/taxonomy/changes", Body(revision, changes));
			response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
			response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
			using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
			problem.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
		}
		string unicodeName = string.Concat(Enumerable.Repeat("🍎", 180)) + Guid.NewGuid().ToString("N")[..20];
		using (HttpResponseMessage invalidReason = await client.PostAsync("/api/taxonomy/changes", Body(revision,
			[new { kind = "create", categoryId = Guid.NewGuid(), name = "Food" }], "Reason\0")))
		{
			invalidReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
			invalidReason.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
			using JsonDocument problem = JsonDocument.Parse(await invalidReason.Content.ReadAsStringAsync());
			problem.RootElement.GetProperty("detail").GetString().Should().Contain("NUL");
		}
		using HttpResponseMessage success = await client.PostAsync("/api/taxonomy/changes", Body(revision,
			[new { kind = "create", categoryId = Guid.NewGuid(), name = unicodeName }]));
		success.StatusCode.Should().Be(HttpStatusCode.OK);
		using JsonDocument published = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
		published.RootElement.GetProperty("nodes").EnumerateArray().Should().Contain(node => node.GetProperty("name").GetString() == unicodeName);
		using HttpResponseMessage stale = await client.PostAsync("/api/taxonomy/changes", Body(revision,
			[new { kind = "create", categoryId = Guid.NewGuid(), name = "Stale" }]));
		stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
		stale.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using JsonDocument conflict = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
		conflict.RootElement.GetProperty("detail").GetString().Should().Contain("Refresh");
		(await client.GetAsync($"/api/taxonomy/revisions/{revision}")).StatusCode.Should().Be(HttpStatusCode.OK);
	}
}
