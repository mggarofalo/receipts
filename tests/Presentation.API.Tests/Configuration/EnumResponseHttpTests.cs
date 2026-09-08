using System.Net;
using System.Text.Json;
using API.Configuration;
using API.Controllers.Core;
using API.Services;
using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Reports;
using Application.Models.Ynab;
using Application.Queries.Aggregates.Reports;
using Application.Queries.Core.ItemTemplate.GetSimilarItems;
using Application.Queries.Core.Receipt;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using DomainStatus = Domain.NormalizedDescriptions.NormalizedDescriptionStatus;

namespace Presentation.API.Tests.Configuration;

public class EnumResponseHttpTests
{
	private static async Task<WebApplication> StartAsync(Mock<IMediator> mediator)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration).RegisterProgramServices();
		builder.Services.AddAuthorization();
		builder.Services.AddControllers().AddApplicationPart(typeof(ReceiptsController).Assembly);
		builder.Services.AddSingleton(mediator.Object);
		builder.Services.AddSingleton(Mock.Of<IYnabApiClient>());
		builder.Services.AddSingleton(Mock.Of<IYnabBudgetSelectionService>());
		WebApplication app = builder.Build();
		app.UseAuthorization();
		// Only application query results are stubbed. Actual controllers, mappings, typed
		// results and configured JSON serialization run; authentication has separate tests.
		app.MapControllers().AllowAnonymous();
		await app.StartAsync();
		return app;
	}

	[Fact]
	public async Task SimilarItems_RawResponseDistinguishesHistoryAndTemplateUsingContractLiterals()
	{
		Mock<IMediator> mediator = new(MockBehavior.Strict);
		mediator.Setup(m => m.Send(It.Is<GetSimilarItemsQuery>(q => q.SearchText == "Milk"), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IEnumerable<SimilarItemResult>)[
				new() { Name = "History milk", Source = "history", Similarity = 0.9 },
				new() { Name = "Template milk", Source = "template", Similarity = 0.8 },
			]);
		await using WebApplication app = await StartAsync(mediator);
		using HttpClient client = app.GetTestClient();
		using JsonDocument body = await GetBodyAsync(client, "/api/item-templates/similar?q=Milk&semantic=false");
		body.RootElement.EnumerateArray().Select(row => row.GetProperty("source").GetString())
			.Should().Equal("history", "template");
		mediator.VerifyAll();
	}

	[Fact]
	public async Task ReceiptList_RawNestedBalanceCannotMasqueradeAsBalanced()
	{
		Mock<IMediator> mediator = new(MockBehavior.Strict);
		List<ReceiptListItem> rows = [
			new(Guid.NewGuid(), "Store", new DateOnly(2025, 1, 1), 0, 10, 0, 10, 9, "out-of-balance", 1, "Food", "Card"),
			new(Guid.NewGuid(), "No payment", new DateOnly(2025, 1, 1), 0, 10, 0, 10, 0, "no-transactions", 1, "Food", ""),
		];
		mediator.Setup(m => m.Send(It.IsAny<GetAllReceiptsQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ReceiptListItem>(rows, rows.Count, 0, 50));
		await using WebApplication app = await StartAsync(mediator);
		using HttpClient client = app.GetTestClient();
		using JsonDocument body = await GetBodyAsync(client, "/api/receipts");
		body.RootElement.GetProperty("data").EnumerateArray().Select(row => row.GetProperty("balanceState").GetString())
			.Should().Equal("outOfBalance", "noTransactions");
		mediator.VerifyAll();
	}

	[Fact]
	public async Task ReceiptSync_RawListStatusMatchesGeneratedClientContract()
	{
		Guid receiptId = Guid.NewGuid();
		Mock<IMediator> mediator = new(MockBehavior.Strict);
		mediator.Setup(m => m.Send(It.Is<GetReceiptYnabSyncStatusesQuery>(q => q.ReceiptIds.SequenceEqual(new[] { receiptId })), It.IsAny<CancellationToken>()))
			.ReturnsAsync([new(receiptId, ReceiptSyncStatusValue.Synced)]);
		await using WebApplication app = await StartAsync(mediator);
		using HttpClient client = app.GetTestClient();
		using JsonDocument body = await GetBodyAsync(client, $"/api/ynab/receipt-sync-statuses?receiptIds={receiptId}");
		JsonElement row = body.RootElement.GetProperty("data")[0];
		row.GetProperty("receiptId").GetGuid().Should().Be(receiptId);
		row.GetProperty("syncStatus").GetString().Should().Be("synced");
		mediator.VerifyAll();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task NormalizedReport_RawNullableStatusPreservesPendingAndSyntheticBuckets(bool syntheticOnly)
	{
		Mock<IMediator> mediator = new(MockBehavior.Strict);
		List<SpendingByNormalizedDescriptionItem> rows = [new("(Not Normalized)", 3, "USD", 1, null, null, null)];
		if (!syntheticOnly)
		{
			rows.Add(new("Milk", 7, "USD", 1, null, null, DomainStatus.PendingReview));
		}
		mediator.Setup(m => m.Send(It.IsAny<GetSpendingByNormalizedDescriptionQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new SpendingByNormalizedDescriptionResult(rows, rows.Count, rows.Sum(row => row.TotalAmount), null, null));
		await using WebApplication app = await StartAsync(mediator);
		using HttpClient client = app.GetTestClient();
		using JsonDocument body = await GetBodyAsync(client, "/api/reports/spending-by-normalized-description");
		JsonElement items = body.RootElement.GetProperty("items");
		items.GetArrayLength().Should().Be(rows.Count);
		items[0].GetProperty("status").ValueKind.Should().Be(JsonValueKind.Null);
		if (!syntheticOnly)
		{
			items[1].GetProperty("status").GetString().Should().Be("pendingReview");
		}
		mediator.VerifyAll();
	}

	private static async Task<JsonDocument> GetBodyAsync(HttpClient client, string path)
	{
		using HttpResponseMessage response = await client.GetAsync(path);
		string raw = await response.Content.ReadAsStringAsync();
		response.StatusCode.Should().Be(HttpStatusCode.OK, raw);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
		return JsonDocument.Parse(raw);
	}
}
