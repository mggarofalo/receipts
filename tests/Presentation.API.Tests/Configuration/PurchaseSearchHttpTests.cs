using System.Net;
using System.Text.Json;
using API.Configuration;
using API.Controllers;
using API.Middleware;
using API.Services;
using Application.Interfaces.Services;
using Application.Models.Reports;
using Application.Models.Search;
using Application.Services;
using Domain.Taxonomy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Presentation.API.Tests.Configuration;

public class PurchaseSearchHttpTests
{
	[Fact]
	public async Task FilteredQuery_ForwardsTypedNormalizedOptionsThroughTheRealPipeline()
	{
		var expected = new PurchaseSearchOptions("unitPrice", "asc", "50% Market", new(2025, 1, 1), new(2025, 1, 2), 0m, 1.2345m);
		var reader = new Mock<IPurchaseSearchReader>(MockBehavior.Strict);
		reader.Setup(x => x.SearchAsync("milk", 10, 10, expected, It.IsAny<CancellationToken>())).ReturnsAsync(new PurchaseSearchResult([], 0, 10, 10));
		await using var app = await StartAsync(reader);
		using var client = app.GetTestClient();
		using var response = await client.GetAsync("/api/search/purchases?q=milk&offset=10&limit=10&sortBy=unitPrice&sortDirection=asc&location=%2050%25%20Market%20&startDate=2025-01-01&endDate=2025-01-02&minUnitPrice=0&maxUnitPrice=1.2345");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		reader.VerifyAll();
	}
	[Theory]
	[InlineData("")]
	[InlineData("?q=")]
	[InlineData("?q=%20a%20")]
	[InlineData("?q=milk&offset=-1")]
	[InlineData("?q=milk&limit=0")]
	[InlineData("?q=milk&limit=101")]
	[InlineData("?q=milk&offset=not-a-number")]
	[InlineData("?q=milk&sortBy=receiptId")]
	[InlineData("?q=milk&sortDirection=descending")]
	[InlineData("?q=milk&startDate=2025-02-30")]
	[InlineData("?q=milk&startDate=2025-02-02&endDate=2025-02-01")]
	[InlineData("?q=milk&minUnitPrice=-0.0001")]
	[InlineData("?q=milk&minUnitPrice=1.0001&maxUnitPrice=1")]
	[InlineData("?q=milk&maxUnitPrice=NaN")]
	[InlineData("?categoryId=00000000-0000-0000-0000-000000000000")]
	[InlineData("?q=milk&categoryView=unknown")]
	[InlineData("?q=milk&capturedCurrentRevisionId=00000000-0000-0000-0000-000000000001")]
	[InlineData("?q=milk&knowledgeCutoff=2026-01-01T00:00:00%2B00:00")]
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
	public async Task CategoryFilter_ForwardsTemporalOptionsAndSerializesFullArchivedPath()
	{
		Guid category = Guid.NewGuid(), revision = Guid.NewGuid(), root = Guid.NewGuid();
		DateTimeOffset cutoff = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		var options = new PurchaseSearchOptions(CategoryView: CategoryReportView.CurrentHierarchy,
				CategoryId: category, KnowledgeCutoff: cutoff, CapturedCurrentRevisionId: revision);
		var item = new PurchaseSearchItem(Guid.NewGuid(), Guid.NewGuid(), new(2025, 1, 1), "Shop", "Milk",
				null, null, null, 1, 2, "USD", 2, "USD")
		{
			CategoryId = category,
			PathRevisionId = revision,
			CategoryState = CategoryReportState.Classified,
			CategoryPath = [new TaxonomyNode(root, null, "Food", null, false),
								new TaxonomyNode(category, root, "Milk", null, true)]
		};
		Mock<IPurchaseSearchReader> reader = new(MockBehavior.Strict);
		reader.Setup(x => x.SearchAsync("", 0, 25, options, It.IsAny<CancellationToken>()))
				.ReturnsAsync(new PurchaseSearchResult([item], 1, 0, 25)
				{
					CategoryView = CategoryReportView.CurrentHierarchy,
					CurrentRevisionId = revision,
					KnowledgeCutoff = cutoff,
					AssignmentSelection = CategoryReportAssignmentSelection.LatestRecordedAtOrBeforeCutoff
				});
		await using var app = await StartAsync(reader);
		using var client = app.GetTestClient();
		using var response = await client.GetAsync($"/api/search/purchases?categoryView=currentHierarchy&categoryId={category}&knowledgeCutoff=2026-01-01T00:00:00Z&capturedCurrentRevisionId={revision}");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement result = document.RootElement;
		result.GetProperty("categoryView").GetString().Should().Be("currentHierarchy");
		result.GetProperty("assignmentSelection").GetString().Should().Be("latestRecordedAtOrBeforeCutoff");
		result.GetProperty("currentRevisionId").GetGuid().Should().Be(revision);
		JsonElement row = result.GetProperty("items")[0];
		row.GetProperty("categoryState").GetString().Should().Be("classified");
		row.GetProperty("pathRevisionId").GetGuid().Should().Be(revision);
		row.GetProperty("categoryPath")[0].GetProperty("name").GetString().Should().Be("Food");
		row.GetProperty("categoryPath")[1].GetProperty("archived").GetBoolean().Should().BeTrue();
		reader.VerifyAll();
	}

	[Fact]
	public async Task ValidQuery_ReturnsHistoricalFactsAndDefaultPagination()
	{
		Guid id = Guid.NewGuid(), receipt = Guid.NewGuid();
		Mock<IPurchaseSearchReader> reader = new(MockBehavior.Strict);
		reader.Setup(x => x.SearchAsync("whole milk", 0, 25, new PurchaseSearchOptions(), It.IsAny<CancellationToken>())).ReturnsAsync(new PurchaseSearchResult(
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

	[Theory]
	[InlineData("")]
	[InlineData("&q=")]
	[InlineData("&q=%20%20")]
	public async Task GuidHistory_AcceptsAbsentOrBlankPhraseAndReturnsReviewState(string phrase)
	{
		Guid target = Guid.NewGuid();
		var options = new PurchaseSearchOptions(NormalizedDescriptionId: target);
		Mock<IPurchaseSearchReader> reader = new(MockBehavior.Strict);
		reader.Setup(x => x.SearchAsync("", 0, 25,
			options, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PurchaseSearchResult([
				new(Guid.NewGuid(), Guid.NewGuid(), new(2025, 1, 15), "Shop", "RAW", null,
						target, "Reviewed name", 1m, 2m, "USD", -2m, "USD", "verified")], 1, 0, 25));
		await using WebApplication app = await StartAsync(reader);
		using HttpClient client = app.GetTestClient();
		using var response = await client.GetAsync($"/api/search/purchases?normalizedDescriptionId={target}{phrase}");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		var item = document.RootElement.GetProperty("items")[0];
		item.GetProperty("normalizedDescriptionId").GetGuid().Should().Be(target);
		item.GetProperty("normalizationReviewState").GetString().Should().Be("verified");
		item.GetProperty("totalAmount").GetDecimal().Should().Be(-2m);
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
