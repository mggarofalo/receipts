using System.Net;
using System.Text.Json;
using API.Configuration;
using API.Controllers;
using API.Middleware;
using API.Services;
using Application.Interfaces.Services;
using Application.Models.Search;
using Application.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Presentation.API.Tests.Configuration;

public class PurchaseSearchHttpTests
{
	[Theory]
	[InlineData("")]
	[InlineData("?q=")]
	[InlineData("?q=%20a%20")]
	[InlineData("?q=milk&offset=-1")]
	[InlineData("?q=milk&limit=0")]
	[InlineData("?q=milk&limit=101")]
	[InlineData("?q=milk&offset=not-a-number")]
	public async Task InvalidQuery_ReturnsReasonedProblemWithoutSearching(string query)
	{
		Mock<IPurchaseSearchReader> reader = new(MockBehavior.Strict);
		await using WebApplication app = await StartAsync(reader);
		using HttpClient client = app.GetTestClient();
		using HttpResponseMessage response = await client.GetAsync("/api/search/purchases" + query);
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		document.RootElement.GetProperty("status").GetInt32().Should().Be(400);
		document.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
		reader.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task ValidQuery_ReturnsHistoricalFactsAndDefaultPagination()
	{
		Guid id = Guid.NewGuid(), receipt = Guid.NewGuid();
		Mock<IPurchaseSearchReader> reader = new(MockBehavior.Strict);
		reader.Setup(x => x.SearchAsync("whole milk", 0, 25, It.IsAny<CancellationToken>())).ReturnsAsync(new PurchaseSearchResult(
			[new(id, receipt, new DateOnly(2024, 1, 2), "Shop", "Whole Milk", "SKU", null, null, 1.2345m, 2.3456m, "USD", 2.90m, "USD")], 204, 0, 25));
		await using WebApplication app = await StartAsync(reader);
		using HttpClient client = app.GetTestClient();
		using HttpResponseMessage response = await client.GetAsync("/api/search/purchases?q=%20whole%20milk%20");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement, item = root.GetProperty("items")[0];
		root.GetProperty("total").GetInt32().Should().Be(204);
		root.GetProperty("offset").GetInt32().Should().Be(0);
		root.GetProperty("limit").GetInt32().Should().Be(25);
		item.GetProperty("id").GetGuid().Should().Be(id);
		item.GetProperty("receiptId").GetGuid().Should().Be(receipt);
		item.GetProperty("date").GetString().Should().Be("2024-01-02");
		item.GetProperty("description").GetString().Should().Be("Whole Milk");
		item.GetProperty("location").GetString().Should().Be("Shop");
		item.GetProperty("receiptItemCode").GetString().Should().Be("SKU");
		item.GetProperty("quantity").GetDecimal().Should().Be(1.2345m);
		item.GetProperty("unitPrice").GetDecimal().Should().Be(2.3456m);
		item.GetProperty("totalAmount").GetDecimal().Should().Be(2.90m);
		item.GetProperty("unitPriceCurrency").GetString().Should().Be("USD");
		item.GetProperty("totalAmountCurrency").GetString().Should().Be("USD");
		reader.VerifyAll();
	}

	private static async Task<WebApplication> StartAsync(Mock<IPurchaseSearchReader> reader)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration)
			.RegisterProgramServices().RegisterApplicationServices(builder.Configuration);
		builder.Services.AddAuthorization();
		builder.Services.AddControllers().AddApplicationPart(typeof(SearchController).Assembly);
		builder.Services.AddSingleton(reader.Object);
		WebApplication app = builder.Build();
		app.UseMiddleware<ValidationExceptionMiddleware>();
		app.UseAuthorization();
		app.MapControllers().AllowAnonymous();
		await app.StartAsync();
		return app;
	}
}
