using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using API.Configuration;
using API.Controllers;
using API.Controllers.Core;
using API.Generated.Dtos;
using API.Http;
using API.Mapping.Core;
using API.Services;
using Application.Commands.Reports;
using Application.Interfaces.Services;
using Application.Models;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Presentation.API.Tests.Configuration;

public class ProblemMediaHttpTests
{
	[Theory]
	[InlineData(403)]
	[InlineData(503)]
	public async Task ConcreteProblemResults_DeclareAndExecuteTheirProblemContract(int status)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddProblemDetails();
		builder.Services.AddOpenApiServices();
		await using WebApplication app = builder.Build();
		app.MapGet("/forbidden", () => new ForbiddenProblem("Permission denied", "denied"));
		app.MapGet("/unavailable", () => new ServiceUnavailableProblem("Service unavailable", "unavailable"));
		app.MapOpenApi();
		await app.StartAsync();
		using HttpClient client = app.GetTestClient();
		string path = status == 403 ? "/forbidden" : "/unavailable";
		using HttpResponseMessage response = await client.GetAsync(path);
		using JsonDocument problem = await AssertProblemAsync(response, (HttpStatusCode)status,
			status == 403 ? "Permission denied" : "Service unavailable");
		problem.RootElement.GetProperty("code").GetString().Should().Be(status == 403 ? "denied" : "unavailable");
		using JsonDocument document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
		JsonElement content = document.RootElement.GetProperty("paths").GetProperty(path)
			.GetProperty("get").GetProperty("responses").GetProperty(status.ToString()).GetProperty("content");
		content.EnumerateObject().Select(property => property.Name).Should().Equal("application/problem+json");
		content.GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref")
			.GetString().Should().Be("#/components/schemas/ProblemDetails");
	}

	private static async Task<WebApplication> StartAsync(Mock<IBackupImportService>? imports = null)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration).AddOpenApiServices();
		builder.Services.AddAuthorization(options => options.AddPolicy("RequireAdmin", policy => policy.RequireAssertion(_ => true)));
		builder.Services.AddControllers().AddApplicationPart(typeof(BackupController).Assembly);
		Mock<IMediator> mediator = new();
		mediator.Setup(service => service.Send(It.IsAny<AcceptDuplicateGroupCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException("Receipt 123 not found"));
		Mock<ICardService> cards = new();
		cards.Setup(service => service.GetTransactionCountByCardIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(3);
		builder.Services.AddSingleton(mediator.Object);
		builder.Services.AddSingleton(cards.Object);
		builder.Services.AddSingleton(Mock.Of<IAccountService>());
		builder.Services.AddSingleton<CardMapper>();
		builder.Services.AddSingleton(Mock.Of<IBackupService>());
		builder.Services.AddSingleton((imports ?? new()).Object);
		builder.Services.AddSingleton(Mock.Of<IEntityChangeNotifier>());
		WebApplication app = builder.Build();
		app.UseAuthorization();
		app.MapControllers().AllowAnonymous();
		app.MapOpenApi();
		await app.StartAsync();
		return app;
	}

	[Theory]
	[InlineData("missing", "No file was uploaded.")]
	[InlineData("extension", "Invalid file extension")]
	[InlineData("service", "Malformed backup")]
	public async Task BackupImport_RejectionUsesProblemMediaAndDetail(string rejection, string detail)
	{
		Mock<IBackupImportService> imports = new();
		imports.Setup(service => service.ImportFromSqliteAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("Malformed backup"));
		await using WebApplication app = await StartAsync(imports);
		using HttpClient client = app.GetTestClient();
		using MultipartFormDataContent upload = new();
		if (rejection == "missing")
		{
			upload.Add(new StringContent("present"), "unrelated");
		}

		if (rejection != "missing")
		{
			upload.Add(new ByteArrayContent([1, 2, 3]), "file", rejection == "extension" ? "backup.txt" : "backup.db");
		}

		using HttpResponseMessage response = await client.PostAsync("/api/backup/import", upload);

		using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, detail);
		if (rejection != "service")
		{
			imports.VerifyNoOtherCalls();
		}
	}

	[Fact]
	public async Task ProducesJsonControllers_KeepProblemMediaFor400404409AndTopLevelExtensions()
	{
		await using WebApplication app = await StartAsync();
		using HttpClient client = app.GetTestClient();
		using HttpResponseMessage badRequest = await client.GetAsync("/api/cards?offset=-1");
		using JsonDocument badProblem = await AssertProblemAsync(badRequest, HttpStatusCode.BadRequest, "offset must be >= 0");
		using HttpResponseMessage notFound = await client.PostAsJsonAsync("/api/reports/duplicates/accepted",
			new AcceptDuplicateGroupRequest { ReceiptIds = [Guid.NewGuid(), Guid.NewGuid()] });
		using JsonDocument missingProblem = await AssertProblemAsync(notFound, HttpStatusCode.NotFound, "Receipt 123 not found");
		using HttpResponseMessage conflict = await client.DeleteAsync($"/api/cards/{Guid.NewGuid()}");
		using JsonDocument conflictProblem = await AssertProblemAsync(conflict, HttpStatusCode.Conflict, "Cannot delete");
		conflictProblem.RootElement.GetProperty("transactionCount").GetInt32().Should().Be(3);
		conflictProblem.RootElement.TryGetProperty("extensions", out _).Should().BeFalse();
	}

	[Fact]
	public async Task OrdinarySuccessAndBodilessNotFound_KeepExistingContracts()
	{
		Mock<IBackupImportService> imports = new();
		imports.Setup(service => service.ImportFromSqliteAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BackupImportResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
		await using WebApplication app = await StartAsync(imports);
		using HttpClient client = app.GetTestClient();
		using MultipartFormDataContent upload = new();
		upload.Add(new ByteArrayContent([1, 2, 3]), "file", "backup.db");
		using HttpResponseMessage success = await client.PostAsync("/api/backup/import", upload);
		success.StatusCode.Should().Be(HttpStatusCode.OK);
		success.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
		(await success.Content.ReadFromJsonAsync<BackupImportResponse>())!.TotalCreated.Should().Be(0);
		using HttpResponseMessage missing = await client.GetAsync($"/api/cards/{Guid.NewGuid()}");
		missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
		(await missing.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
	}

	[Fact]
	public async Task GeneratedOpenApi_SeparatesProblemStatusesAndMediaFromSuccess()
	{
		await using WebApplication app = await StartAsync();
		using HttpClient client = app.GetTestClient();
		using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement paths = document.RootElement.GetProperty("paths");
		foreach ((string path, string method, string status) in new[] {
			("/api/normalization-ai/connection", "get", "403"), ("/api/normalization-ai/connection", "get", "503"),
						("/api/backup/import", "post", "400"), ("/api/backup/import", "post", "409"),
			("/api/cards", "get", "400"), ("/api/cards/{id}", "delete", "409"),
			("/api/reports/duplicates/accepted", "post", "404") })
		{
			paths.TryGetProperty(path, out JsonElement operationPath).Should().BeTrue($"path {path}; paths: {string.Join(", ", paths.EnumerateObject().Select(property => property.Name))}");
			JsonElement responses = operationPath.GetProperty(method).GetProperty("responses");
			responses.TryGetProperty(status, out JsonElement errorResponse).Should().BeTrue($"{method} {path} {status}; responses: {responses}");
			errorResponse.TryGetProperty("content", out JsonElement content).Should().BeTrue($"{method} {path} {status}: {errorResponse}");
			content.EnumerateObject().Select(property => property.Name).Should().Equal(["application/problem+json"], $"{method} {path} {status}");
			content.GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString().Should().Be("#/components/schemas/ProblemDetails");
		}
		JsonElement successContent = paths.GetProperty("/api/backup/import").GetProperty("post").GetProperty("responses").GetProperty("200").GetProperty("content");
		successContent.TryGetProperty("application/json", out _).Should().BeTrue();
		successContent.TryGetProperty("application/problem+json", out _).Should().BeFalse();
	}

	private static async Task<JsonDocument> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string detail)
	{
		response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		document.RootElement.GetProperty("status").GetInt32().Should().Be((int)status);
		document.RootElement.GetProperty("detail").GetString().Should().Contain(detail);
		return document;
	}
}
