using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using API.Configuration;
using API.Controllers.Aggregates;
using API.Services;
using Application.Models.Dashboard;
using Application.Models.Reports;
using Application.Queries.Aggregates.Dashboard;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Presentation.API.Tests.Configuration;

public sealed class DashboardCategoryReportingHttpTests
{
	private const string Url = "/api/dashboard/spending-by-category";

	[Fact]
	public async Task AnonymousRequestCannotReachMediator()
	{
		var mediator = new Mock<IMediator>(MockBehavior.Strict);
		await using var app = await Start(mediator);
		using var client = app.GetTestClient();
		using var response = await client.GetAsync(Url);
		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		mediator.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData("categoryId=invalid")]
	[InlineData("capturedCurrentRevisionId=invalid")]
	[InlineData("categoryId=00000000-0000-0000-0000-000000000000")]
	[InlineData("categoryView=currentHierarchy&capturedCurrentRevisionId=00000000-0000-0000-0000-000000000000")]
	[InlineData("startDate=invalid")]
	[InlineData("startDate=2026-10-02&endDate=2026-10-01")]
	[InlineData("limit=invalid")]
	[InlineData("limit=0")]
	[InlineData("limit=101")]
	[InlineData("categoryView=unknown")]
	[InlineData("rollup=unknown")]
	[InlineData("knowledgeCutoff=2026-10-01T00%3A00%3A00%2B00%3A00")]
	public async Task InvalidQueryReturnsReasonedProblemWithoutCallingMediator(string query)
	{
		var mediator = new Mock<IMediator>(MockBehavior.Strict);
		await using var app = await Start(mediator);
		using var client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("Test-User", "reader");
		using var response = await client.GetAsync(Url + "?" + query);
		await AssertProblem(response);
		mediator.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task UnknownCapturedRevisionReturnsReasonedProblem()
	{
		var mediator = new Mock<IMediator>(MockBehavior.Strict);
		mediator.Setup(m => m.Send(It.IsAny<GetSpendingByCategoryQuery>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new ArgumentException("The revision does not exist.", "capturedCurrentRevisionId"));
		await using var app = await Start(mediator);
		using var client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("Test-User", "reader");
		using var response = await client.GetAsync(Url + $"?categoryView=currentHierarchy&capturedCurrentRevisionId={Guid.NewGuid()}");
		await AssertProblem(response);
		mediator.VerifyAll();
	}

	[Theory]
	[InlineData(null)]
	[InlineData("2026-10-01T12:34:56.1234567Z")]
	public async Task ValidQueryPreservesDefaultsAndUtcPrecision(string? cutoff)
	{
		var mediator = new Mock<IMediator>(MockBehavior.Strict);
		GetSpendingByCategoryQuery? captured = null;
		mediator.Setup(m => m.Send(It.IsAny<GetSpendingByCategoryQuery>(), It.IsAny<CancellationToken>()))
			.Callback<IRequest<SpendingByCategoryResult>, CancellationToken>((query, _) => captured = (GetSpendingByCategoryQuery)query)
			.ReturnsAsync(new SpendingByCategoryResult([]));
		await using var app = await Start(mediator);
		using var client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("Test-User", "reader");
		using var response = await client.GetAsync(Url + (cutoff is null ? "" : "?knowledgeCutoff=" + cutoff));
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		captured!.StartDate.Should().Be(DateOnly.MinValue);
		captured.EndDate.Should().Be(DateOnly.FromDateTime(DateTime.Today));
		captured.Limit.Should().Be(10);
		captured.CategoryOptions!.View.Should().Be(CategoryReportView.AsClassified);
		captured.CategoryOptions.Rollup.Should().Be(CategoryReportRollup.AssignedCategory);
		captured.CategoryOptions.KnowledgeCutoff.Should().Be(cutoff is null ? null : DateTimeOffset.Parse(cutoff));
		if (cutoff is not null) { captured.CategoryOptions.KnowledgeCutoff!.Value.Offset.Should().Be(TimeSpan.Zero); }
		mediator.VerifyAll();
	}

	private static async Task AssertProblem(HttpResponseMessage response)
	{
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		json.RootElement.GetProperty("status").GetInt32().Should().Be(400);
		json.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
	}

	private static async Task<WebApplication> Start(Mock<IMediator> mediator)
	{
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration).RegisterProgramServices();
		builder.Services.AddAuthorization();
		builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
		builder.Services.AddControllers().AddApplicationPart(typeof(DashboardController).Assembly);
		builder.Services.AddSingleton(mediator.Object);
		var app = builder.Build();
		app.UseAuthentication();
		app.UseAuthorization();
		app.MapControllers();
		await app.StartAsync();
		return app;
	}

	private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
		: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
	{
		protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
			Request.Headers.ContainsKey("Test-User")
				? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "reader")], Scheme.Name)), Scheme.Name))
				: AuthenticateResult.NoResult());
	}
}
