using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using API.Controllers;
using Application.Classification;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class CategoryAssistanceAutoWriterTests
{
	private sealed class SettingsTestIdentity(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
		: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
	{
		protected override Task<AuthenticateResult> HandleAuthenticateAsync()
		{
			var actor = Request.Headers["X-Test-Actor"].ToString();
			if (actor.Length == 0) { return Task.FromResult(AuthenticateResult.NoResult()); }
			var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor)], Scheme.Name);
			return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
		}
	}

	[Fact]
	public async Task AutoApplyPersistsAndRoundTripsThroughTheSettingsStore()
	{
		string admin = await UserAsync();
		var store = new CategoryAssistanceSettingsStore(new Factory(fixture), TimeProvider.System);
		var defaults = await store.GetAsync();
		defaults.AutoApply.Should().BeTrue("new installations apply strong matches once consent exists");

		var off = await store.UpdateAsync(admin, defaults.Revision, defaults with
		{ Enabled = true, Provider = "jev", Model = CategorySuggestionPolicy.Model, AutoApply = false });

		off.AutoApply.Should().BeFalse();
		(await store.GetAsync()).Should().Be(off);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.CategoryAssistanceSettings.AsNoTracking().SingleAsync()).AutoApply.Should().BeFalse();
			var audit = await db.CategoryAssistanceSettingsAudits.AsNoTracking().SingleAsync(x => x.Revision == off.Revision);
			JsonSerializer.Deserialize<CategoryAssistanceSettings>(audit.Before)!.AutoApply.Should().BeTrue();
			JsonSerializer.Deserialize<CategoryAssistanceSettings>(audit.After)!.AutoApply.Should().BeFalse();
		}

		var limits = await store.UpdateAsync(admin, off.Revision, off with { PendingLimit = 5 });
		limits.AutoApply.Should().BeFalse();
		(await store.UpdateAsync(admin, limits.Revision, limits with { AutoApply = true })).AutoApply.Should().BeTrue();
		(await store.GetAsync()).AutoApply.Should().BeTrue();
	}

	[Fact]
	public async Task SettingsApiOmissionKeepsTheSavedAutoApply()
	{
		string admin = await UserAsync();
		var store = new CategoryAssistanceSettingsStore(new Factory(fixture), TimeProvider.System);
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Services.AddAuthentication("SettingsTest")
			.AddScheme<AuthenticationSchemeOptions, SettingsTestIdentity>("SettingsTest", _ => { });
		// The store enforces the active Admin itself; the policy only requires authentication here.
		builder.Services.AddAuthorization(o => o.AddPolicy("RequireAdmin", p => p.RequireAuthenticatedUser()));
		builder.Services.AddControllers().AddApplicationPart(typeof(CategoryAssistanceController).Assembly);
		builder.Services.AddSingleton<ICategoryAssistanceSettingsStore>(store);
		builder.Services.AddSingleton(Mock.Of<ICategoryAssistanceReadStore>());
		builder.Services.AddSingleton(Mock.Of<ICategoryAssistanceCapabilitySource>());
		builder.Services.AddSingleton(Mock.Of<ICategoryAssistanceDecisionStore>());
		builder.Services.AddSingleton(Mock.Of<ICategoryAssistanceReviewPreferenceStore>());
		await using var app = builder.Build();
		app.UseAuthentication();
		app.UseAuthorization();
		app.MapControllers();
		await app.StartAsync();
		using var client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("X-Test-Actor", admin);
		const string route = "/api/category-assistance/settings";

		async Task<CategoryAssistanceSettings> PutAsync(object body)
		{
			var response = await client.PutAsJsonAsync(route, body);
			response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
			return (await response.Content.ReadFromJsonAsync<CategoryAssistanceSettings>())!;
		}

		var current = (await client.GetFromJsonAsync<CategoryAssistanceSettings>(route))!;
		current.AutoApply.Should().BeTrue();
		var off = await PutAsync(new
		{
			enabled = true,
			provider = "jev",
			model = CategorySuggestionPolicy.Model,
			dailyLimit = current.DailyLimit,
			pendingLimit = current.PendingLimit,
			expectedRevision = current.Revision,
			autoApply = false
		});
		off.AutoApply.Should().BeFalse();

		// An older client that does not know the field must not switch automatic writes back on.
		var omitted = await PutAsync(new
		{
			enabled = true,
			provider = "jev",
			model = CategorySuggestionPolicy.Model,
			dailyLimit = off.DailyLimit,
			pendingLimit = off.PendingLimit + 1,
			expectedRevision = off.Revision
		});
		omitted.AutoApply.Should().BeFalse();
		omitted.PendingLimit.Should().Be(off.PendingLimit + 1);
		(await store.GetAsync()).AutoApply.Should().BeFalse();

		var on = await PutAsync(new
		{
			enabled = true,
			provider = "jev",
			model = CategorySuggestionPolicy.Model,
			dailyLimit = omitted.DailyLimit,
			pendingLimit = omitted.PendingLimit,
			expectedRevision = omitted.Revision,
			autoApply = true
		});
		on.AutoApply.Should().BeTrue();
		(await client.GetFromJsonAsync<CategoryAssistanceSettings>(route))!.AutoApply.Should().BeTrue();
	}
}
