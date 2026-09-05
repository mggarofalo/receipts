using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using API.Configuration;
using API.Controllers.Core;
using API.Generated.Dtos;
using API.Middleware;
using API.Services;
using Application.Interfaces.Services;
using Application.Queries.Core.ItemTemplate.GetSimilarItems;
using Application.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Presentation.API.Tests.Configuration;

public class ValidationCompositionHttpTests
{
	private static async Task<WebApplication> StartAsync(Mock<IReceiptService> receipts, Mock<IItemTemplateSimilarityService>? similarity = null)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration)
			.RegisterProgramServices().RegisterApplicationServices(builder.Configuration);
		builder.Services.AddAuthorization();
		builder.Services.AddControllers().AddApplicationPart(typeof(ReceiptsController).Assembly);
		builder.Services.AddSingleton(receipts.Object);
		builder.Services.AddSingleton((similarity ?? new()).Object);
		builder.Services.AddSingleton(Mock.Of<IEntityChangeNotifier>());
		WebApplication app = builder.Build();
		app.UseMiddleware<ValidationExceptionMiddleware>();
		app.UseAuthorization();
		// Authentication has its own composed-host tests. These routes exercise the actual
		// MVC/filter/Mediator pipeline without requiring credentials unrelated to validation.
		app.MapControllers().AllowAnonymous();
		await app.StartAsync();
		return app;
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(0)]
	[InlineData(21)]
	public async Task Similarity_InvalidLimit_ReturnsValidationProblemBeforeService(int limit)
	{
		Mock<IItemTemplateSimilarityService> similarity = new();
		await using WebApplication app = await StartAsync(new(), similarity);
		using HttpClient client = app.GetTestClient();
		using HttpResponseMessage response = await client.GetAsync($"/api/item-templates/similar?q=aa&limit={limit}&semantic=false");
		await AssertValidationProblemAsync(response);
		similarity.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task MalformedJson_ReturnsProblemWithoutInvokingReceiptService()
	{
		Mock<IReceiptService> receipts = new();
		await using WebApplication app = await StartAsync(receipts);
		using HttpClient client = app.GetTestClient();
		using StringContent content = new("{", System.Text.Encoding.UTF8, "application/json");

		using HttpResponseMessage response = await client.PostAsync("/api/receipts", content);

		await AssertValidationProblemAsync(response);
		receipts.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task SingleAndBatch_CreateAndUpdate_RejectSameElementPolicy(bool update, bool futureDate)
	{
		Mock<IReceiptService> receipts = new();
		await using WebApplication app = await StartAsync(receipts);
		using HttpClient client = app.GetTestClient();
		UpdateReceiptRequest invalid = Request(futureDate ? "valid location" : new string('x', 201));
		if (futureDate)
		{
			invalid.Date = DateOnly.FromDateTime(DateTime.Today.AddDays(2));
		}

		using HttpResponseMessage single = update
			? await client.PutAsJsonAsync($"/api/receipts/{invalid.Id}", invalid)
			: await client.PostAsJsonAsync("/api/receipts", invalid);
		using HttpResponseMessage batch = update
			? await client.PutAsJsonAsync("/api/receipts/batch", new[] { Request("valid"), invalid })
			: await client.PostAsJsonAsync("/api/receipts/batch", new[] { Request("valid"), invalid });
		await AssertValidationProblemAsync(single);
		await AssertValidationProblemAsync(batch, futureDate ? "[1].Date" : "[1].Location");
		receipts.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task EmptyAndNullElementBatches_Return400WithoutDispatch(bool update)
	{
		Mock<IReceiptService> receipts = new();
		await using WebApplication app = await StartAsync(receipts);
		using HttpClient client = app.GetTestClient();
		foreach (object body in new object[] { Array.Empty<UpdateReceiptRequest>(), new UpdateReceiptRequest?[] { Request("valid"), null } })
		{
			using HttpResponseMessage response = update
				? await client.PutAsJsonAsync("/api/receipts/batch", body)
				: await client.PostAsJsonAsync("/api/receipts/batch", body);
			await AssertValidationProblemAsync(response);
		}
		receipts.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ValidSingleAndBatch_AllowBoundaryLocationAndPreserveResponseContract(bool update)
	{
		Mock<IReceiptService> receipts = new();
		receipts.Setup(s => s.CreateAsync(It.IsAny<List<Domain.Core.Receipt>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((List<Domain.Core.Receipt> values, CancellationToken _) => values);
		receipts.Setup(s => s.UpdateAsync(It.IsAny<List<Domain.Core.Receipt>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		receipts.Setup(s => s.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
		await using WebApplication app = await StartAsync(receipts);
		using HttpClient client = app.GetTestClient();
		UpdateReceiptRequest valid = Request(new string('x', 200));
		using HttpResponseMessage single = update
			? await client.PutAsJsonAsync($"/api/receipts/{valid.Id}", valid)
			: await client.PostAsJsonAsync("/api/receipts", valid);
		using HttpResponseMessage batch = update
			? await client.PutAsJsonAsync("/api/receipts/batch", new[] { valid, Request("second") })
			: await client.PostAsJsonAsync("/api/receipts/batch", new[] { valid, Request("second") });
		single.StatusCode.Should().Be(update ? HttpStatusCode.NoContent : HttpStatusCode.OK, await single.Content.ReadAsStringAsync());
		batch.StatusCode.Should().Be(update ? HttpStatusCode.NoContent : HttpStatusCode.OK, await batch.Content.ReadAsStringAsync());
		if (update)
		{
			receipts.Verify(s => s.UpdateAsync(It.Is<List<Domain.Core.Receipt>>(items => items.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
		}
		else
		{
			receipts.Verify(s => s.CreateAsync(It.Is<List<Domain.Core.Receipt>>(items => items.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
		}
	}

	private static UpdateReceiptRequest Request(string location) => new()
	{
		Id = Guid.NewGuid(),
		Location = location,
		Date = new DateOnly(2025, 1, 1),
		TaxAmount = 0,
	};

	private static async Task AssertValidationProblemAsync(HttpResponseMessage response, string? expectedField = null)
	{
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		body.RootElement.GetProperty("status").GetInt32().Should().Be(400);
		body.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
		body.RootElement.GetProperty("errors").EnumerateObject().Should().NotBeEmpty();
		if (expectedField is not null)
		{
			body.RootElement.GetProperty("errors").TryGetProperty(expectedField, out _).Should().BeTrue();
		}
	}
}
