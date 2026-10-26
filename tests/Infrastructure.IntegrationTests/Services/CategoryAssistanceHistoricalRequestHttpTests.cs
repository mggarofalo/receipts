using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using API.Controllers;
using Application.Classification;
using Application.Taxonomy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class CategoryAssistanceHistoricalRequestStoreTests
{
	private sealed class HistoricalTestIdentity(
		IOptionsMonitor<AuthenticationSchemeOptions> options,
		ILoggerFactory logger, UrlEncoder encoder)
		: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
	{
		protected override Task<AuthenticateResult> HandleAuthenticateAsync()
		{
			var actor = Request.Headers["X-Test-Actor"].ToString();
			if (actor.Length == 0)
			{
				return Task.FromResult(AuthenticateResult.NoResult());
			}

			var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor)], Scheme.Name);
			return Task.FromResult(AuthenticateResult.Success(
				new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
		}
	}

	[Fact]
	public async Task HistoricalHttpEnrollmentEnforcesWriterReplayVisibilityAndBounds()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Services.AddAuthentication("HistoricalTest")
			.AddScheme<AuthenticationSchemeOptions, HistoricalTestIdentity>("HistoricalTest", _ => { });
		builder.Services.AddAuthorization();
		builder.Services.AddControllers().AddApplicationPart(typeof(CategoryAssistanceHistoricalRequestsController).Assembly);
		builder.Services.AddSingleton<ICategoryAssistanceHistoricalRequestStore>(Store(fixture));
		await using var app = builder.Build();
		app.UseAuthentication();
		app.UseAuthorization();
		app.MapControllers();
		await app.StartAsync();
		using var client = app.GetTestClient();
		const string route = "/api/category-assistance/historical-requests";
		var request = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", " milk ", null)]);

		(await client.PostAsJsonAsync(route, request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		(await client.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		client.DefaultRequestHeaders.Add("X-Test-Actor", "inactive-writer");
		var forbidden = await client.PostAsJsonAsync(route, request);
		forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		forbidden.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

		client.DefaultRequestHeaders.Remove("X-Test-Actor");
		client.DefaultRequestHeaders.Add("X-Test-Actor", seed.Actor);
		var first = await client.PostAsJsonAsync(route, request);
		first.StatusCode.Should().Be(HttpStatusCode.OK);
		var progress = await first.Content.ReadFromJsonAsync<CategoryAssistanceHistoricalProgress>();
		progress!.OperationId.Should().Be(request.OperationId);
		progress.ItemCount.Should().Be(1);
		(await client.PostAsJsonAsync(route, request)).StatusCode.Should().Be(HttpStatusCode.OK);
		var conflict = await client.PostAsJsonAsync(route,
			request with { Groups = [new HistoricalGroupKey("Shop", "milk", null)] });
		conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
		conflict.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

		var own = await client.GetFromJsonAsync<CategoryAssistanceHistoricalPage>(route);
		own!.Items.Should().ContainSingle().Which.OperationId.Should().Be(request.OperationId);
		(await client.GetFromJsonAsync<CategoryAssistanceHistoricalProgress>(
			$"{route}/{request.OperationId}"))!.OperationId.Should().Be(request.OperationId);
		foreach (var suffix in new[] { "?offset=-1", "?limit=26" })
		{
			var invalid = await client.GetAsync(route + suffix);
			invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
			invalid.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		}
		var overLimit = await client.PostAsJsonAsync(route,
			new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
				Enumerable.Range(0, 26).Select(i => new HistoricalGroupKey("Shop", $"item-{i}", null)).ToArray()));
		overLimit.StatusCode.Should().Be(HttpStatusCode.BadRequest);

		client.DefaultRequestHeaders.Remove("X-Test-Actor");
		client.DefaultRequestHeaders.Add("X-Test-Actor", "another-actor");
		(await client.GetFromJsonAsync<CategoryAssistanceHistoricalPage>(route))!.Items.Should().BeEmpty();
		(await client.GetAsync($"{route}/{request.OperationId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
	}
}
