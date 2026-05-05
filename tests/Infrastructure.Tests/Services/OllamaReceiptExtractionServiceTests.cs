using System.Net;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.Ocr;
using Common;
using FluentAssertions;
using FluentAssertions.Specialized;
using Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Polly;

namespace Infrastructure.Tests.Services;

public class OllamaReceiptExtractionServiceTests
{
	private static readonly byte[] FakeImage = [0x89, 0x50, 0x4E, 0x47];

	private static OllamaReceiptExtractionService CreateService(
		HttpMessageHandler handler,
		VlmOcrOptions? options = null)
	{
		HttpClient httpClient = new(handler) { BaseAddress = new Uri("http://test-ollama/") };
		options ??= new VlmOcrOptions
		{
			OllamaUrl = "http://test-ollama",
			Model = "glm-ocr:q8_0",
			TimeoutSeconds = 30,
		};
		return new OllamaReceiptExtractionService(
			httpClient,
			Options.Create(options),
			NullLogger<OllamaReceiptExtractionService>.Instance);
	}

	private static HttpMessageHandler CreateHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
	{
		Mock<HttpMessageHandler> handlerMock = new();
		handlerMock.Protected()
			.Setup<Task<HttpResponseMessage>>("SendAsync",
				ItExpr.IsAny<HttpRequestMessage>(),
				ItExpr.IsAny<CancellationToken>())
			.ReturnsAsync(new HttpResponseMessage
			{
				StatusCode = statusCode,
				Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
			});
		return handlerMock.Object;
	}

	private static string WrapInOllamaEnvelope(string innerJson)
	{
		return JsonSerializer.Serialize(new
		{
			model = "glm-ocr:q8_0",
			response = innerJson,
			done = true,
		});
	}

	/// <summary>
	/// Runs <paramref name="action"/> against an <see cref="IReceiptExtractionService"/>
	/// resolved from a DI container configured exactly as the production code does
	/// (<see cref="InfrastructureService.RegisterReceiptExtractionService"/>), including
	/// the per-attempt Polly Timeout strategy. The container is disposed after the action
	/// completes so the underlying <see cref="IHttpClientFactory"/> and any other
	/// <see cref="IDisposable"/> singletons do not leak across tests.
	/// </summary>
	private static async Task RunWithPipelineServiceAsync(
		HttpMessageHandler primaryHandler,
		VlmOcrOptions options,
		Func<IReceiptExtractionService, Task> action)
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton<IOptions<VlmOcrOptions>>(Options.Create(options));
#pragma warning disable EXTEXP0001
		services.AddHttpClient<IReceiptExtractionService, OllamaReceiptExtractionService>(client =>
		{
			client.BaseAddress = new Uri(options.OllamaUrl!.TrimEnd('/') + "/");
			client.Timeout = Timeout.InfiniteTimeSpan;
		})
		.ConfigurePrimaryHttpMessageHandler(() => primaryHandler)
		.RemoveAllResilienceHandlers()
		.AddResilienceHandler("vlm-ocr-test", builder =>
			InfrastructureService.ConfigureVlmOcrResilience(builder, options));
#pragma warning restore EXTEXP0001

		await using ServiceProvider sp = services.BuildServiceProvider();
		IReceiptExtractionService service = sp.GetRequiredService<IReceiptExtractionService>();
		await action(service);
	}

	[Fact]
	public async Task ExtractAsync_HappyPath_ReturnsAllFieldsWithHighConfidence()
	{
		// Arrange — exercises the full V2 shape: nested store, datetime, lineTotal,
		// nullable quantity/unitPrice (GRANULATED has neither printed), taxCode,
		// payments array, receipt/store/terminal identifiers.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": {
			    "name": "Walmart Supercenter",
			    "address": "9 BENTON RD, TRAVELERS REST SC 29690",
			    "phone": "864-834-7179"
			  },
			  "datetime": "2026-01-14T17:57:20",
			  "items": [
			    {
			      "description": "GRANULATED",
			      "code": "078742228030",
			      "lineTotal": 3.07,
			      "quantity": null,
			      "unitPrice": null,
			      "taxCode": "F"
			    },
			    {
			      "description": "BANANAS",
			      "code": "000000004011",
			      "lineTotal": 1.23,
			      "quantity": 2.46,
			      "unitPrice": 0.50,
			      "taxCode": "N"
			    }
			  ],
			  "subtotal": 69.68,
			  "taxLines": [{ "label": "TAX1 6.0000%", "amount": 0.75 }],
			  "total": 70.43,
			  "payments": [
			    { "method": "MASTERCARD", "amount": 70.43, "lastFour": "3409" }
			  ],
			  "receiptId": "7QKKG1XDWPD",
			  "storeNumber": "05487",
			  "terminalId": "54731105"
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		receipt.StoreName.Value.Should().Be("Walmart Supercenter");
		receipt.StoreName.Confidence.Should().Be(ConfidenceLevel.High);
		receipt.Date.Value.Should().Be(new DateOnly(2026, 1, 14));
		receipt.Date.Confidence.Should().Be(ConfidenceLevel.High);

		receipt.StoreAddress.Value.Should().Be("9 BENTON RD, TRAVELERS REST SC 29690");
		receipt.StoreAddress.Confidence.Should().Be(ConfidenceLevel.High);
		receipt.StorePhone.Value.Should().Be("864-834-7179");
		receipt.StorePhone.Confidence.Should().Be(ConfidenceLevel.High);

		receipt.Items.Should().HaveCount(2);
		receipt.Items[0].Code.Value.Should().Be("078742228030");
		receipt.Items[0].Description.Value.Should().Be("GRANULATED");
		receipt.Items[0].Quantity.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.Items[0].UnitPrice.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.Items[0].TotalPrice.Value.Should().Be(3.07m);
		receipt.Items[0].TaxCode.Value.Should().Be("F");
		receipt.Items[0].TaxCode.Confidence.Should().Be(ConfidenceLevel.High);

		receipt.Items[1].Quantity.Value.Should().Be(2.46m);
		receipt.Items[1].UnitPrice.Value.Should().Be(0.50m);
		receipt.Items[1].TotalPrice.Value.Should().Be(1.23m);
		receipt.Items[1].TaxCode.Value.Should().Be("N");

		receipt.Subtotal.Value.Should().Be(69.68m);
		receipt.TaxLines.Should().HaveCount(1);
		receipt.TaxLines[0].Label.Value.Should().Be("TAX1 6.0000%");
		receipt.TaxLines[0].Amount.Value.Should().Be(0.75m);
		receipt.Total.Value.Should().Be(70.43m);
		receipt.PaymentMethod.Value.Should().Be("MASTERCARD");
		receipt.PaymentMethod.Confidence.Should().Be(ConfidenceLevel.High);

		receipt.Payments.Should().HaveCount(1);
		receipt.Payments[0].Method.Value.Should().Be("MASTERCARD");
		receipt.Payments[0].Amount.Value.Should().Be(70.43m);
		receipt.Payments[0].LastFour.Value.Should().Be("3409");
		receipt.Payments[0].LastFour.Confidence.Should().Be(ConfidenceLevel.High);

		receipt.ReceiptId.Value.Should().Be("7QKKG1XDWPD");
		receipt.ReceiptId.Confidence.Should().Be(ConfidenceLevel.High);
		receipt.StoreNumber.Value.Should().Be("05487");
		receipt.StoreNumber.Confidence.Should().Be(ConfidenceLevel.High);
		receipt.TerminalId.Value.Should().Be("54731105");
		receipt.TerminalId.Confidence.Should().Be(ConfidenceLevel.High);
	}

	[Theory]
	[InlineData("2026-04-01", 2026, 4, 1)]
	[InlineData("2026-01-14T17:57:20", 2026, 1, 14)]
	[InlineData("01/14/26 17:57:20", 2026, 1, 14)]
	[InlineData("01/14/26", 2026, 1, 14)]
	[InlineData("1/14/2026", 2026, 1, 14)]
	[InlineData("01/14/2026", 2026, 1, 14)]
	[InlineData("2026/04/01", 2026, 4, 1)]
	public async Task ExtractAsync_NonIsoDateFormats_ParsedWithHighConfidence(
		string dateString, int expectedYear, int expectedMonth, int expectedDay)
	{
		// Arrange — the VLM often returns dates in the format printed on the receipt
		// (e.g. "01/14/26") despite being asked for ISO-8601. We parse leniently.
		string innerJson = $$"""
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "datetime": "{{dateString}}",
			  "total": 10.00
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		receipt.Date.Value.Should().Be(new DateOnly(expectedYear, expectedMonth, expectedDay));
		receipt.Date.Confidence.Should().Be(ConfidenceLevel.High);
	}

	[Fact]
	public async Task ExtractAsync_UnparseableDate_YieldsNoneConfidenceWithoutThrowing()
	{
		// Arrange — a bad date string should not tank the entire extraction
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "datetime": "sometime last week",
			  "total": 10.00
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		receipt.Date.Value.Should().Be(default(DateOnly));
		receipt.Date.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.StoreName.Value.Should().Be("Walmart");
		receipt.Total.Value.Should().Be(10.00m);
	}

	[Fact]
	public async Task ExtractAsync_WeightSubline_MergesIntoParent()
	{
		// Arrange — reproduces the VLM's output shape for a weighted item: parent row with
		// null quantity + null unitPrice, followed by a separate row whose description holds
		// the "X lb. @ $Y" pattern and carries the actual quantity/unitPrice.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "items": [
			    { "description": "BANANAS", "code": "000000004011", "lineTotal": 1.23,
			      "quantity": null, "unitPrice": null, "taxCode": "N" },
			    { "description": "2.460 lb. @ 1 lb. /0.50", "code": null, "lineTotal": 1.23,
			      "quantity": 2.460, "unitPrice": 0.50, "taxCode": "N" }
			  ],
			  "total": 1.23
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — sub-line is absorbed; only the parent remains with the weight data
		receipt.Items.Should().HaveCount(1);
		receipt.Items[0].Description.Value.Should().Be("BANANAS");
		receipt.Items[0].Code.Value.Should().Be("000000004011");
		receipt.Items[0].TotalPrice.Value.Should().Be(1.23m);
		receipt.Items[0].Quantity.Value.Should().Be(2.460m);
		receipt.Items[0].UnitPrice.Value.Should().Be(0.50m);
	}

	[Fact]
	public async Task ExtractAsync_WeightSublineWithoutMatchingParent_PreservedAsItem()
	{
		// Arrange — defensive: if the parent's lineTotal doesn't match, we don't merge.
		// This keeps us from accidentally corrupting an unrelated prior item.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "items": [
			    { "description": "BREAD", "code": "072250049190", "lineTotal": 3.76,
			      "quantity": null, "unitPrice": null, "taxCode": "N" },
			    { "description": "2.460 lb. @ 1 lb. /0.50", "code": null, "lineTotal": 1.23,
			      "quantity": 2.460, "unitPrice": 0.50, "taxCode": "N" }
			  ]
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — both rows kept; BREAD is untouched
		receipt.Items.Should().HaveCount(2);
		receipt.Items[0].Description.Value.Should().Be("BREAD");
		receipt.Items[0].Quantity.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.Items[1].Description.Value.Should().Be("2.460 lb. @ 1 lb. /0.50");
		receipt.Items[1].Quantity.Value.Should().Be(2.460m);
	}

	[Fact]
	public async Task ExtractAsync_ParentAlreadyHasQuantity_DoesNotOverride()
	{
		// Arrange — if the VLM (or a future model) already populated the parent correctly,
		// the "sub-line" shouldn't clobber it. Treat it as a distinct item instead.
		string innerJson = """
			{
			  "schema_version": 1,
			  "items": [
			    { "description": "BANANAS", "code": "000000004011", "lineTotal": 1.23,
			      "quantity": 2.460, "unitPrice": 0.50, "taxCode": "N" },
			    { "description": "2.460 lb. @ 1 lb. /0.50", "code": null, "lineTotal": 1.23,
			      "quantity": 2.460, "unitPrice": 0.50, "taxCode": "N" }
			  ]
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — parent is preserved; second "item" is kept as a distinct row
		receipt.Items.Should().HaveCount(2);
		receipt.Items[0].Quantity.Value.Should().Be(2.460m);
	}

	[Fact]
	public void MergeWeightSublines_MultipleWeightedItems_MergesEach()
	{
		// Arrange — real Walmart case: two bananas weighed at different prices
		List<VlmReceiptItem> items =
		[
			new() { Description = "BANANAS", Code = "000000004011", LineTotal = 1.23m, Quantity = null, UnitPrice = null, TaxCode = "N" },
			new() { Description = "2.460 lb. @ 1 lb. /0.50", Code = null, LineTotal = 1.23m, Quantity = 2.460m, UnitPrice = 0.50m, TaxCode = "N" },
			new() { Description = "BANANAS", Code = "000000004011", LineTotal = 1.36m, Quantity = null, UnitPrice = null, TaxCode = "N" },
			new() { Description = "2.720 lb. @ 1 lb. /0.50", Code = null, LineTotal = 1.36m, Quantity = 2.720m, UnitPrice = 0.50m, TaxCode = "N" },
		];

		// Act
		List<VlmReceiptItem> merged = OllamaReceiptExtractionService.MergeWeightSublines(items);

		// Assert
		merged.Should().HaveCount(2);
		merged[0].Description.Should().Be("BANANAS");
		merged[0].Quantity.Should().Be(2.460m);
		merged[0].UnitPrice.Should().Be(0.50m);
		merged[0].TaxCode.Should().Be("N");
		merged[1].Description.Should().Be("BANANAS");
		merged[1].Quantity.Should().Be(2.720m);
		merged[1].UnitPrice.Should().Be(0.50m);
		merged[1].TaxCode.Should().Be("N");
	}

	[Fact]
	public void MergeWeightSublines_ParentMissingTaxCode_AbsorbsFromSubline()
	{
		// Arrange — defensive: if the VLM echoes taxCode on the sub-line but not on the
		// parent, the merge must preserve it. Without this, the merged item drops the code
		// entirely now that MapItem reads ParsedReceiptItem.TaxCode.
		List<VlmReceiptItem> items =
		[
			new() { Description = "BANANAS", Code = "000000004011", LineTotal = 1.23m, Quantity = null, UnitPrice = null, TaxCode = null },
			new() { Description = "2.460 lb. @ 1 lb. /0.50", Code = null, LineTotal = 1.23m, Quantity = 2.460m, UnitPrice = 0.50m, TaxCode = "N" },
		];

		// Act
		List<VlmReceiptItem> merged = OllamaReceiptExtractionService.MergeWeightSublines(items);

		// Assert — sub-line is absorbed; parent inherits the tax code
		merged.Should().HaveCount(1);
		merged[0].TaxCode.Should().Be("N");
	}

	[Fact]
	public void MergeWeightSublines_ParentHasTaxCode_WinsOverSubline()
	{
		// Arrange — parent populated, sub-line disagrees. Parent wins because the tax-code
		// marker sits next to the parent line on the physical receipt.
		List<VlmReceiptItem> items =
		[
			new() { Description = "BANANAS", Code = "000000004011", LineTotal = 1.23m, Quantity = null, UnitPrice = null, TaxCode = "N" },
			new() { Description = "2.460 lb. @ 1 lb. /0.50", Code = null, LineTotal = 1.23m, Quantity = 2.460m, UnitPrice = 0.50m, TaxCode = "T" },
		];

		// Act
		List<VlmReceiptItem> merged = OllamaReceiptExtractionService.MergeWeightSublines(items);

		// Assert — parent's taxCode is preserved
		merged.Should().HaveCount(1);
		merged[0].TaxCode.Should().Be("N");
	}

	[Fact]
	public void MergeWeightSublines_PhantomParent_AbsorbsSubline()
	{
		// Arrange — Walmart 2026-01-14 shape (RECEIPTS-662). Walmart prints the price on the
		// weight line, so the VLM emits a phantom header (lineTotal/qty/unitPrice all 0) with
		// taxCode "F" plus a sub-line carrying the actual values and taxCode "N".
		List<VlmReceiptItem> items =
		[
			new() { Description = "TOMATO", Code = "000000004664", LineTotal = 0m, Quantity = 0m, UnitPrice = 0m, TaxCode = "F" },
			new() { Description = "2.300 lb. @ 1 lb. /0.92", Code = null, LineTotal = 2.12m, Quantity = 2.300m, UnitPrice = 0.92m, TaxCode = "N" },
		];

		// Act
		List<VlmReceiptItem> merged = OllamaReceiptExtractionService.MergeWeightSublines(items);

		// Assert — single merged item with the sub-line's values; taxCode "N" wins
		merged.Should().HaveCount(1);
		merged[0].Description.Should().Be("TOMATO");
		merged[0].Code.Should().Be("000000004664");
		merged[0].LineTotal.Should().Be(2.12m);
		merged[0].Quantity.Should().Be(2.300m);
		merged[0].UnitPrice.Should().Be(0.92m);
		merged[0].TaxCode.Should().Be("N");
	}

	[Fact]
	public void MergeWeightSublines_PhantomParentNullFields_AbsorbsSubline()
	{
		// Arrange — defensive: phantom parent emits null instead of zero. Same outcome.
		List<VlmReceiptItem> items =
		[
			new() { Description = "TOMATO", Code = "000000004664", LineTotal = null, Quantity = null, UnitPrice = null, TaxCode = "F" },
			new() { Description = "2.300 lb. @ 1 lb. /0.92", Code = null, LineTotal = 2.12m, Quantity = 2.300m, UnitPrice = 0.92m, TaxCode = "N" },
		];

		// Act
		List<VlmReceiptItem> merged = OllamaReceiptExtractionService.MergeWeightSublines(items);

		// Assert
		merged.Should().HaveCount(1);
		merged[0].LineTotal.Should().Be(2.12m);
		merged[0].Quantity.Should().Be(2.300m);
		merged[0].TaxCode.Should().Be("N");
	}

	[Fact]
	public void MergeWeightSublines_PhantomParentNullLineTotal_DoesNotMatchExistingPredicate()
	{
		// Arrange — defensive: when both parent (phantom) and sub-line emit lineTotal=null,
		// the existing "matching lineTotal" predicate fires on null==null, which would route
		// the merge through the wrong case and leave the phantom's taxCode untouched. The
		// phantom-parent guard on case (a) keeps phantoms exclusively in case (b).
		List<VlmReceiptItem> items =
		[
			new() { Description = "TOMATO", Code = "000000004664", LineTotal = null, Quantity = null, UnitPrice = null, TaxCode = "F" },
			new() { Description = "2.300 lb. @ 1 lb. /0.92", Code = null, LineTotal = null, Quantity = 2.300m, UnitPrice = 0.92m, TaxCode = "N" },
		];

		// Act
		List<VlmReceiptItem> merged = OllamaReceiptExtractionService.MergeWeightSublines(items);

		// Assert — case (b) requires sub-line lineTotal > 0; this case is preserved as two rows
		// rather than silently absorbed with the wrong taxCode. The user can correct manually.
		merged.Should().HaveCount(2);
		merged[0].TaxCode.Should().Be("F");
	}

	[Fact]
	public void MergeWeightSublines_PhantomParentSublineMissingTaxCode_LeavesParentTaxCodeUntouched()
	{
		// Arrange — phantom parent has taxCode, sub-line doesn't. The parent's taxCode is
		// preserved (no overwrite) because sub-line carries no replacement signal.
		List<VlmReceiptItem> items =
		[
			new() { Description = "TOMATO", Code = "000000004664", LineTotal = 0m, Quantity = 0m, UnitPrice = 0m, TaxCode = "F" },
			new() { Description = "2.300 lb. @ 1 lb. /0.92", Code = null, LineTotal = 2.12m, Quantity = 2.300m, UnitPrice = 0.92m, TaxCode = null },
		];

		// Act
		List<VlmReceiptItem> merged = OllamaReceiptExtractionService.MergeWeightSublines(items);

		// Assert — values absorbed; pre-existing taxCode kept
		merged.Should().HaveCount(1);
		merged[0].LineTotal.Should().Be(2.12m);
		merged[0].TaxCode.Should().Be("F");
	}

	[Fact]
	public void ReconcileSubtotal_NoExtractedValue_ReturnsNoneConfidence()
	{
		FieldConfidence<decimal> result = OllamaReceiptExtractionService.ReconcileSubtotal(
			extracted: null,
			items: [MakeReceiptItem(1.00m)]);

		result.IsPresent.Should().BeFalse();
		result.Confidence.Should().Be(ConfidenceLevel.None);
	}

	[Fact]
	public void ReconcileSubtotal_SubtotalMatchesItemSumExactly_ReturnsHighConfidence()
	{
		FieldConfidence<decimal> result = OllamaReceiptExtractionService.ReconcileSubtotal(
			extracted: 6.00m,
			items: [MakeReceiptItem(1.00m), MakeReceiptItem(2.00m), MakeReceiptItem(3.00m)]);

		result.Confidence.Should().Be(ConfidenceLevel.High);
		result.Value.Should().Be(6.00m);
	}

	[Fact]
	public void ReconcileSubtotal_DeltaAtToleranceBoundary_ReturnsHighConfidence()
	{
		// Inclusive bound: delta exactly equal to tolerance ($0.05) is High confidence.
		FieldConfidence<decimal> result = OllamaReceiptExtractionService.ReconcileSubtotal(
			extracted: 10.05m,
			items: [MakeReceiptItem(10.00m)]);

		result.Confidence.Should().Be(ConfidenceLevel.High);
	}

	[Fact]
	public void ReconcileSubtotal_DeltaExceedsTolerance_ReturnsLowConfidencePreservingValue()
	{
		// Walmart 2026-01-14 (RECEIPTS-663): subtotal=$69.68, items sum to $69.57, delta=$0.11.
		FieldConfidence<decimal> result = OllamaReceiptExtractionService.ReconcileSubtotal(
			extracted: 69.68m,
			items:
			[
				MakeReceiptItem(30.00m),
				MakeReceiptItem(25.57m),
				MakeReceiptItem(14.00m),
			]);

		result.Confidence.Should().Be(ConfidenceLevel.Low);
		result.Value.Should().Be(69.68m);
	}

	[Fact]
	public void ReconcileSubtotal_NoItems_TreatsSumAsZero()
	{
		// Defensive: an empty items list shouldn't crash. Sum is 0, so non-zero subtotal
		// disagrees and ends up Low confidence — exactly the "we missed every item" signal
		// that motivates the cross-check (Option 1 in RECEIPTS-663).
		FieldConfidence<decimal> result = OllamaReceiptExtractionService.ReconcileSubtotal(
			extracted: 5.00m,
			items: []);

		result.Confidence.Should().Be(ConfidenceLevel.Low);
		result.Value.Should().Be(5.00m);
	}

	[Fact]
	public async Task ExtractAsync_SubtotalDisagreesWithItemSum_DowngradesConfidence()
	{
		// End-to-end: Walmart 2026-01-14 shape. Subtotal=$69.68, items sum to $69.57.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "items": [
			    { "description": "ITEM A", "code": "A", "lineTotal": 30.00 },
			    { "description": "ITEM B", "code": "B", "lineTotal": 25.57 },
			    { "description": "ITEM C", "code": "C", "lineTotal": 14.00 }
			  ],
			  "subtotal": 69.68,
			  "total": 70.43
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		receipt.Subtotal.Confidence.Should().Be(ConfidenceLevel.Low);
		receipt.Subtotal.Value.Should().Be(69.68m);
	}

	private static ParsedReceiptItem MakeReceiptItem(decimal totalPrice) =>
		new(FieldConfidence<string?>.None(),
			FieldConfidence<string>.High("X"),
			FieldConfidence<decimal>.None(),
			FieldConfidence<decimal>.None(),
			FieldConfidence<decimal>.High(totalPrice));

	[Fact]
	public async Task ExtractAsync_PhantomHeaderWithWeightSubline_ProducesSingleItem()
	{
		// Arrange — full pipeline reproduction of the Walmart 2026-01-14 shape (RECEIPTS-662).
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "items": [
			    { "description": "TOMATO", "code": "000000004664", "lineTotal": 0,
			      "quantity": 0, "unitPrice": 0, "taxCode": "F" },
			    { "description": "2.300 lb. @ 1 lb. /0.92", "code": null, "lineTotal": 2.12,
			      "quantity": 2.300, "unitPrice": 0.92, "taxCode": "N" }
			  ],
			  "total": 2.12
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — phantom is absorbed; merged item carries the printed values
		receipt.Items.Should().HaveCount(1);
		receipt.Items[0].Description.Value.Should().Be("TOMATO");
		receipt.Items[0].Code.Value.Should().Be("000000004664");
		receipt.Items[0].TotalPrice.Value.Should().Be(2.12m);
		receipt.Items[0].Quantity.Value.Should().Be(2.300m);
		receipt.Items[0].UnitPrice.Value.Should().Be(0.92m);
		receipt.Items[0].TaxCode.Value.Should().Be("N");
	}

	[Fact]
	public async Task ExtractAsync_MissingOptionalFields_ReturnsNoneConfidence()
	{
		// Arrange — no payments, no taxLines, items with unitPrice/quantity omitted (preferred per V2 prompt)
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "datetime": "2026-04-01",
			  "items": [],
			  "subtotal": 3.99,
			  "total": 4.29
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		receipt.PaymentMethod.Value.Should().BeNull();
		receipt.PaymentMethod.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.TaxLines.Should().BeEmpty();
		receipt.Items.Should().BeEmpty();
		receipt.StoreName.Confidence.Should().Be(ConfidenceLevel.High);
	}

	[Fact]
	public async Task ExtractAsync_MultiPayment_PreservesAllPayments()
	{
		// Arrange — split tender (gift card + card). The legacy PaymentMethod field picks
		// the first non-empty method for backward compatibility, but the Payments list must
		// carry every tender with its amount and last-four for downstream reconciliation.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "total": 40.00,
			  "payments": [
			    { "method": "GIFT CARD", "amount": 10.00, "lastFour": null },
			    { "method": "VISA", "amount": 30.00, "lastFour": "1234" }
			  ]
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — legacy field keeps the first method
		receipt.PaymentMethod.Value.Should().Be("GIFT CARD");
		receipt.PaymentMethod.Confidence.Should().Be(ConfidenceLevel.High);

		// Assert — full Payments list is preserved in order
		receipt.Payments.Should().HaveCount(2);
		receipt.Payments[0].Method.Value.Should().Be("GIFT CARD");
		receipt.Payments[0].Amount.Value.Should().Be(10.00m);
		receipt.Payments[0].Amount.Confidence.Should().Be(ConfidenceLevel.High);
		receipt.Payments[0].LastFour.Value.Should().BeNull();
		receipt.Payments[0].LastFour.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.Payments[1].Method.Value.Should().Be("VISA");
		receipt.Payments[1].Amount.Value.Should().Be(30.00m);
		receipt.Payments[1].LastFour.Value.Should().Be("1234");
		receipt.Payments[1].LastFour.Confidence.Should().Be(ConfidenceLevel.High);
	}

	[Fact]
	public async Task ExtractAsync_MissingStoreObject_YieldsNoneConfidenceStoreName()
	{
		// Arrange — the entire store object may be omitted on a hard-to-read receipt
		string innerJson = """
			{
			  "schema_version": 1,
			  "datetime": "2026-04-01",
			  "total": 10.00
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		receipt.StoreName.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.StoreAddress.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.StoreAddress.Value.Should().BeNull();
		receipt.StorePhone.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.StorePhone.Value.Should().BeNull();
		receipt.Date.Value.Should().Be(new DateOnly(2026, 4, 1));
		receipt.Total.Value.Should().Be(10.00m);
	}

	[Fact]
	public async Task ExtractAsync_MissingIdentifiersAndPayments_YieldNoneConfidenceAndEmptyList()
	{
		// Arrange — receiptId/storeNumber/terminalId are all commonly missing on smaller
		// independent-store receipts, and a receipt with no payments block should yield an
		// empty Payments list rather than throwing.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Corner Market" },
			  "datetime": "2026-04-01",
			  "total": 3.99
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		receipt.ReceiptId.Value.Should().BeNull();
		receipt.ReceiptId.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.StoreNumber.Value.Should().BeNull();
		receipt.StoreNumber.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.TerminalId.Value.Should().BeNull();
		receipt.TerminalId.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.Payments.Should().BeEmpty();
	}

	[Fact]
	public async Task ExtractAsync_HallucinatedLastFour_RejectedWithLowConfidence()
	{
		// Arrange — RECEIPTS-627: qwen2.5vl:3b sometimes lifts the APPR# (a 6+ digit reference
		// number printed elsewhere on the receipt) into lastFour instead of the true 4-digit
		// card tail. Post-processing rejects anything that does not match ^\d{4}$ so the UI
		// never surfaces a hallucinated value with high confidence.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "total": 70.43,
			  "payments": [
			    { "method": "MCARD", "amount": 70.43, "lastFour": "014042" }
			  ]
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — value is nulled, confidence drops to Low. Other payment fields untouched.
		receipt.Payments.Should().HaveCount(1);
		receipt.Payments[0].LastFour.Value.Should().BeNull();
		receipt.Payments[0].LastFour.Confidence.Should().Be(ConfidenceLevel.Low);
		receipt.Payments[0].Method.Value.Should().Be("MCARD");
		receipt.Payments[0].Method.Confidence.Should().Be(ConfidenceLevel.High);
		receipt.Payments[0].Amount.Value.Should().Be(70.43m);
	}

	[Theory]
	// Hallucinated 6-digit value (the original RECEIPTS-627 repro)
	[InlineData("014042")]
	// Five digits — partial trim of an APPR#
	[InlineData("12345")]
	// Three digits — too short, not a valid full card tail
	[InlineData("340")]
	// Letters mixed in — OCR substituted alphanumerics
	[InlineData("34O9")]
	// Masked formats — strict pattern rejects, even though the trailing 4 digits are present
	[InlineData("****3409")]
	[InlineData("XX3409")]
	// Whitespace and separators
	[InlineData("3 409")]
	[InlineData("3-409")]
	// Non-ASCII Unicode digit sequences. .NET's default \d regex expands to the full
	// Unicode Decimal_Number category, so the pattern must use [0-9] explicitly to enforce
	// the documented "ASCII digits" contract. Without that, Arabic-Indic (٣٤٠٩) and
	// Devanagari (३४०९) digits would slip through with High confidence.
	[InlineData("\u0663\u0664\u0660\u0669")]
	[InlineData("\u096B\u096C\u0966\u0966")]
	public void ValidateLastFour_InvalidNonEmptyPatterns_YieldNullWithLowConfidence(string raw)
	{
		// Act — non-empty/non-whitespace input that fails the regex represents a hallucinated
		// or malformed value the VLM emitted. We retain Low confidence to signal "the model
		// said something but we rejected it" — distinct from None ("the model said nothing").
		FieldConfidence<string?> result = OllamaReceiptExtractionService.ValidateLastFour(raw);

		// Assert
		result.Value.Should().BeNull();
		result.Confidence.Should().Be(ConfidenceLevel.Low);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("\t")]
	public void ValidateLastFour_EmptyOrWhitespace_YieldsNullWithNoneConfidence(string raw)
	{
		// Act — an empty/whitespace lastFour is functionally equivalent to "the field was
		// absent", which we represent with ConfidenceLevel.None.
		FieldConfidence<string?> result = OllamaReceiptExtractionService.ValidateLastFour(raw);

		// Assert
		result.Value.Should().BeNull();
		result.Confidence.Should().Be(ConfidenceLevel.None);
	}

	[Fact]
	public void ValidateLastFour_NullInput_YieldsNullWithNoneConfidence()
	{
		// Act — null is treated identically to an empty/absent value.
		FieldConfidence<string?> result = OllamaReceiptExtractionService.ValidateLastFour(null);

		// Assert
		result.Value.Should().BeNull();
		result.Confidence.Should().Be(ConfidenceLevel.None);
	}

	[Theory]
	[InlineData("3409")]
	[InlineData("0000")]
	[InlineData("1234")]
	public void ValidateLastFour_ExactlyFourDigits_PreservedWithHighConfidence(string raw)
	{
		// Act
		FieldConfidence<string?> result = OllamaReceiptExtractionService.ValidateLastFour(raw);

		// Assert
		result.Value.Should().Be(raw);
		result.Confidence.Should().Be(ConfidenceLevel.High);
	}

	[Fact]
	public void ValidateLastFour_FourDigitsWithSurroundingWhitespace_TrimmedAndAccepted()
	{
		// Act — defensive: VLMs sometimes emit "3409 " with a trailing space
		FieldConfidence<string?> result = OllamaReceiptExtractionService.ValidateLastFour(" 3409 ");

		// Assert
		result.Value.Should().Be("3409");
		result.Confidence.Should().Be(ConfidenceLevel.High);
	}

	[Fact]
	public async Task ExtractAsync_ValidLastFour_PreservedWithHighConfidence()
	{
		// Arrange — sanity check that the post-processing does not regress the happy path.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "total": 70.43,
			  "payments": [
			    { "method": "MCARD", "amount": 70.43, "lastFour": "3409" }
			  ]
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		receipt.Payments[0].LastFour.Value.Should().Be("3409");
		receipt.Payments[0].LastFour.Confidence.Should().Be(ConfidenceLevel.High);
	}

	[Fact]
	public async Task ExtractAsync_PaymentWithMissingMethod_YieldsNoneConfidenceOnThatPaymentMethod()
	{
		// Arrange — defensive: if a payment record omits the method but has an amount, we
		// still include the payment in the list but mark the method as None-confidence so
		// downstream code can distinguish "truly unknown tender" from legacy-mapper behavior.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "total": 15.00,
			  "payments": [
			    { "method": null, "amount": 15.00, "lastFour": null }
			  ]
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — the payment is preserved with correct confidence
		receipt.Payments.Should().HaveCount(1);
		receipt.Payments[0].Method.Value.Should().BeNull();
		receipt.Payments[0].Method.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.Payments[0].Amount.Value.Should().Be(15.00m);
		receipt.Payments[0].Amount.Confidence.Should().Be(ConfidenceLevel.High);

		// Assert — legacy PaymentMethod falls back to None since no method string was found
		receipt.PaymentMethod.Value.Should().BeNull();
		receipt.PaymentMethod.Confidence.Should().Be(ConfidenceLevel.None);
	}

	[Fact]
	public async Task ExtractAsync_MalformedInnerJson_Throws()
	{
		// Arrange — the response field is not valid JSON
		string envelope = WrapInOllamaEnvelope("{ this is not: valid JSON");
		OllamaReceiptExtractionService service = CreateService(CreateHandler(envelope));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		ExceptionAssertions<InvalidOperationException> thrown = await act.Should().ThrowAsync<InvalidOperationException>();
		thrown.Which.InnerException.Should().BeOfType<JsonException>();
	}

	[Fact]
	public async Task ExtractAsync_EmptyResponse_Throws()
	{
		// Arrange — envelope present but response field is empty string
		string envelope = WrapInOllamaEnvelope(string.Empty);
		OllamaReceiptExtractionService service = CreateService(CreateHandler(envelope));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*empty response*");
	}

	[Fact]
	public async Task ExtractAsync_OperationCanceled_Propagates()
	{
		// Arrange — handler blocks until canceled
		Mock<HttpMessageHandler> handlerMock = new();
		handlerMock.Protected()
			.Setup<Task<HttpResponseMessage>>("SendAsync",
				ItExpr.IsAny<HttpRequestMessage>(),
				ItExpr.IsAny<CancellationToken>())
			.Returns(async (HttpRequestMessage _, CancellationToken ct) =>
			{
				await Task.Delay(Timeout.Infinite, ct);
				return new HttpResponseMessage(HttpStatusCode.OK);
			});
		OllamaReceiptExtractionService service = CreateService(handlerMock.Object);
		using CancellationTokenSource cts = new();
		cts.CancelAfter(TimeSpan.FromMilliseconds(50));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, cts.Token);

		// Assert — caller-initiated cancellation surfaces as OperationCanceledException
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task ExtractAsync_Timeout_ThrowsTimeoutException()
	{
		// Arrange — handler delays longer than the per-attempt timeout. The Polly Timeout
		// strategy registered by RegisterReceiptExtractionService aborts the attempt and
		// surfaces TimeoutRejectedException, which the service translates to TimeoutException.
		Mock<HttpMessageHandler> handlerMock = new();
		handlerMock.Protected()
			.Setup<Task<HttpResponseMessage>>("SendAsync",
				ItExpr.IsAny<HttpRequestMessage>(),
				ItExpr.IsAny<CancellationToken>())
			.Returns(async (HttpRequestMessage _, CancellationToken ct) =>
			{
				await Task.Delay(TimeSpan.FromSeconds(5), ct);
				return new HttpResponseMessage(HttpStatusCode.OK);
			});
		VlmOcrOptions options = new()
		{
			OllamaUrl = "http://test-ollama",
			Model = "glm-ocr:q8_0",
			TimeoutSeconds = 1,
		};

		await RunWithPipelineServiceAsync(handlerMock.Object, options, async service =>
		{
			// Act
			Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

			// Assert
			await act.Should().ThrowAsync<TimeoutException>()
				.WithMessage("*timed out after 1s*");
		});
	}

	[Fact]
	public async Task ExtractAsync_Request_IncludesModelAndBase64AndJsonFormat()
	{
		// Arrange
		HttpRequestMessage? capturedRequest = null;
		string? capturedBody = null;
		Mock<HttpMessageHandler> handlerMock = new();
		handlerMock.Protected()
			.Setup<Task<HttpResponseMessage>>("SendAsync",
				ItExpr.IsAny<HttpRequestMessage>(),
				ItExpr.IsAny<CancellationToken>())
			.Returns(async (HttpRequestMessage req, CancellationToken _) =>
			{
				capturedRequest = req;
				capturedBody = req.Content is not null ? await req.Content.ReadAsStringAsync() : null;
				return new HttpResponseMessage
				{
					StatusCode = HttpStatusCode.OK,
					Content = new StringContent(
						WrapInOllamaEnvelope("""{ "schema_version": 1 }"""),
						System.Text.Encoding.UTF8,
						"application/json"),
				};
			});
		OllamaReceiptExtractionService service = CreateService(handlerMock.Object);

		// Act
		await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		capturedRequest.Should().NotBeNull();
		capturedRequest!.Method.Should().Be(HttpMethod.Post);
		capturedRequest.RequestUri!.AbsolutePath.Should().EndWith("/api/generate");
		capturedBody.Should().NotBeNullOrEmpty();

		using JsonDocument doc = JsonDocument.Parse(capturedBody!);
		doc.RootElement.GetProperty("model").GetString().Should().Be("glm-ocr:q8_0");
		doc.RootElement.GetProperty("format").GetString().Should().Be("json");
		doc.RootElement.GetProperty("stream").GetBoolean().Should().BeFalse();
		doc.RootElement.GetProperty("images")[0].GetString().Should().Be(Convert.ToBase64String(FakeImage));
		doc.RootElement.GetProperty("prompt").GetString().Should().Contain("receipt");
	}

	[Fact]
	public async Task RegisterReceiptExtractionService_SlowResponse_NotCancelledByServiceDefaultsStandardHandler()
	{
		// Regression for RECEIPTS-630. The Receipts.ServiceDefaults registration applies
		// AddStandardResilienceHandler globally via ConfigureHttpClientDefaults (30s per
		// attempt / 90s total). Real glm-ocr inferences routinely exceed 30s, so the
		// vlm-ocr typed client MUST opt out of that handler — otherwise every slow VLM
		// call surfaces as a Polly TimeoutRejectedException long before the documented
		// VlmOcrOptions.TimeoutSeconds (120s) budget applies.
		//
		// This test simulates the production composition: ServiceDefaults registers the
		// standard handler with an aggressive 200ms attempt timeout, THEN the application
		// calls RegisterReceiptExtractionService. A handler that delays 1s — well past the
		// 200ms standard-handler ceiling but well under the test's per-attempt VLM timeout
		// (5s) — must complete successfully. If the standard handler were not removed by
		// our registration, the call would die at ~200ms with a TimeoutRejectedException.
		Mock<HttpMessageHandler> handlerMock = new();
		string successBody = WrapInOllamaEnvelope("""{ "schema_version": 1, "store": { "name": "Walmart" }, "total": 10.00 }""");
		handlerMock.Protected()
			.Setup<Task<HttpResponseMessage>>("SendAsync",
				ItExpr.IsAny<HttpRequestMessage>(),
				ItExpr.IsAny<CancellationToken>())
			.Returns(async (HttpRequestMessage _, CancellationToken ct) =>
			{
				await Task.Delay(TimeSpan.FromSeconds(1), ct);
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(successBody, System.Text.Encoding.UTF8, "application/json"),
				};
			});

		ServiceCollection services = new();
		services.AddLogging();

		// Replicate Receipts.ServiceDefaults: register an aggressive standard handler
		// for every HttpClient via ConfigureHttpClientDefaults. If RegisterReceiptExtractionService
		// fails to remove this handler, the 200ms attempt timeout will cancel any call
		// that takes longer than 200ms.
		services.ConfigureHttpClientDefaults(http =>
		{
			http.AddStandardResilienceHandler(opts =>
			{
				opts.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(200);
				opts.TotalRequestTimeout.Timeout = TimeSpan.FromMilliseconds(600);
				opts.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(2);
				opts.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
			});
		});

		// Configuration mirrors the production VLM section: a real OllamaUrl plus a
		// TimeoutSeconds well above the 1s simulated work — so success depends solely
		// on whether the global standard handler was successfully removed.
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[$"{ConfigurationVariables.OcrVlmSection}:{nameof(VlmOcrOptions.OllamaUrl)}"] = "http://test-ollama",
				[$"{ConfigurationVariables.OcrVlmSection}:{nameof(VlmOcrOptions.Model)}"] = "glm-ocr:q8_0",
				[$"{ConfigurationVariables.OcrVlmSection}:{nameof(VlmOcrOptions.TimeoutSeconds)}"] = "5",
			})
			.Build();

		InfrastructureService.RegisterReceiptExtractionService(services, configuration);

		// Override the primary handler so we don't actually hit Ollama. ConfigurePrimaryHttpMessageHandler
		// is a separate registration that targets the same named client.
		services.AddHttpClient<IReceiptExtractionService, OllamaReceiptExtractionService>()
			.ConfigurePrimaryHttpMessageHandler(() => handlerMock.Object);

		using ServiceProvider sp = services.BuildServiceProvider();
		IReceiptExtractionService service = sp.GetRequiredService<IReceiptExtractionService>();

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — call completed without being cancelled by ServiceDefaults' 200ms standard
		// handler. With the bug present, this throws TimeoutRejectedException at ~200ms.
		receipt.StoreName.Value.Should().Be("Walmart");
		receipt.Total.Value.Should().Be(10.00m);
	}

	// ----------------------------------------------------------------------
	// Payload fuzz tests (RECEIPTS-647)
	// ----------------------------------------------------------------------
	// Direct invocations of MapToParsedReceipt for shapes that are awkward to
	// build through the JSON deserializer (e.g. NaN — JSON doesn't represent
	// it natively) plus end-to-end ExtractAsync tests for shapes that the
	// JSON layer must accept (numbers-as-strings) or reject (items as a
	// non-array). These exercise schema mismatches the production VLM has
	// been observed to emit.
	// ----------------------------------------------------------------------

	[Fact]
	public async Task ExtractAsync_NumbersAsStrings_ParsedAsDecimals()
	{
		// Arrange — qwen2.5vl emits numeric receipt fields as strings under some
		// prompts (e.g. "total": "9.71"). The service registers
		// JsonNumberHandling.AllowReadingFromString so this round-trips into
		// decimal cleanly, with high confidence on the parsed values.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "datetime": "2026-04-01",
			  "subtotal": "9.10",
			  "total": "9.71",
			  "items": [
			    { "description": "MILK", "lineTotal": "3.49", "quantity": "1", "unitPrice": "3.49" },
			    { "description": "BREAD", "lineTotal": "5.61", "quantity": "1", "unitPrice": "5.61" }
			  ],
			  "taxLines": [{ "label": "TAX1", "amount": "0.61" }]
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — every numeric-as-string value parsed without loss. Two items sum to the
		// declared subtotal so RECEIPTS-663 reconciliation keeps confidence High.
		receipt.Subtotal.Value.Should().Be(9.10m);
		receipt.Subtotal.Confidence.Should().Be(ConfidenceLevel.High);
		receipt.Total.Value.Should().Be(9.71m);
		receipt.Total.Confidence.Should().Be(ConfidenceLevel.High);
		receipt.TaxLines.Should().HaveCount(1);
		receipt.TaxLines[0].Amount.Value.Should().Be(0.61m);
		receipt.Items.Should().HaveCount(2);
		receipt.Items[0].TotalPrice.Value.Should().Be(3.49m);
		receipt.Items[0].Quantity.Value.Should().Be(1m);
		receipt.Items[0].UnitPrice.Value.Should().Be(3.49m);
		receipt.Items[1].TotalPrice.Value.Should().Be(5.61m);
	}

	[Theory]
	// Bare year — not a parseable DateOnly format under any DateFormats entry.
	[InlineData("2026")]
	// Time-only string with no date component (the splitter slices off everything before the
	// first space/T, leaving the empty string for `2026-not-a-date`-style garbage too).
	[InlineData("17:57:20")]
	// Pure non-numeric garbage.
	[InlineData("not a date at all")]
	// Punctuation-only — exercises the "non-empty input that fully fails parse" path.
	[InlineData("???")]
	// Common locale-tagged time spelling without any date marker.
	[InlineData("yesterday afternoon")]
	public async Task ExtractAsync_MalformedDate_YieldsNoneConfidenceWithoutThrowing(string raw)
	{
		// Arrange — RECEIPTS-647: malformed dates must not abort the whole
		// extraction. The other receipt fields should round-trip normally.
		// Note: dates like "4/26/2026 at lunchtime" or "Apr-26-26" are NOT
		// included here because the service deliberately parses leniently —
		// the splitter strips the trailing junk and the invariant parser
		// accepts month-name forms. The cases below exercise inputs that
		// genuinely fail every parse path.
		string innerJson = $$"""
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "datetime": "{{raw}}",
			  "total": 9.71
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		receipt.Date.Value.Should().Be(default(DateOnly));
		receipt.Date.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.StoreName.Value.Should().Be("Walmart");
		receipt.Total.Value.Should().Be(9.71m);
	}

	[Fact]
	public async Task ExtractAsync_ItemsAsObject_FailsWithDescriptiveError()
	{
		// Arrange — RECEIPTS-647: if the VLM emits `items` as an object instead
		// of an array, the JSON deserializer raises a JsonException which the
		// service translates into an InvalidOperationException whose
		// InnerException is the original JsonException. The raw response is
		// included so operators can see the offending payload in the logs.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "items": { "description": "MILK", "lineTotal": 3.49 },
			  "total": 3.49
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		ExceptionAssertions<InvalidOperationException> thrown =
			await act.Should().ThrowAsync<InvalidOperationException>()
				.WithMessage("*Failed to parse Ollama VLM response*");
		thrown.Which.InnerException.Should().BeOfType<JsonException>();
	}

	[Fact]
	public async Task ExtractAsync_ItemsAsString_FailsWithDescriptiveError()
	{
		// Arrange — defensive: scalar in `items` is also a JSON shape the
		// deserializer rejects with JsonException. Same surface contract.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "items": "see attached",
			  "total": 3.49
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		ExceptionAssertions<InvalidOperationException> thrown =
			await act.Should().ThrowAsync<InvalidOperationException>()
				.WithMessage("*Failed to parse Ollama VLM response*");
		thrown.Which.InnerException.Should().BeOfType<JsonException>();
	}

	[Fact]
	public async Task ExtractAsync_UnknownTopLevelFields_IgnoredForwardCompatibility()
	{
		// Arrange — RECEIPTS-647: a future prompt revision may add fields the
		// service doesn't yet know about (e.g. "loyaltyId", "couponsApplied").
		// JsonSerializerDefaults.Web ignores unknown properties, so the service
		// must round-trip the rest of the payload without throwing. Forward
		// compatibility is a hard requirement: rolling out a richer prompt must
		// not require redeploying the API simultaneously.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart", "regionCode": "SE", "internalId": 4711 },
			  "datetime": "2026-04-01",
			  "items": [
			    { "description": "MILK", "lineTotal": 3.49, "promoCode": "SPRING25" }
			  ],
			  "total": 3.49,
			  "loyaltyId": "987654321",
			  "couponsApplied": ["SPRING25"],
			  "metadata": { "extractor": "qwen2.5vl-future" }
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — known fields parsed; unknown fields silently dropped
		receipt.StoreName.Value.Should().Be("Walmart");
		receipt.Date.Value.Should().Be(new DateOnly(2026, 4, 1));
		receipt.Total.Value.Should().Be(3.49m);
		receipt.Items.Should().HaveCount(1);
		receipt.Items[0].Description.Value.Should().Be("MILK");
		receipt.Items[0].TotalPrice.Value.Should().Be(3.49m);
	}

	[Fact]
	public async Task ExtractAsync_NegativeDecimals_PreservedAsExtractedValues()
	{
		// Arrange — refund/return receipts legitimately carry negative totals
		// (e.g. a return refunded to the same card). The service must not
		// silently drop these — the user reviews and confirms the sign on the
		// scan-result UI. Field confidence stays High because the extractor
		// emitted a real value; signs are a domain concern, not an extraction
		// quality concern.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "datetime": "2026-04-01",
			  "items": [
			    { "description": "MILK RETURN", "lineTotal": -3.49 }
			  ],
			  "subtotal": -3.49,
			  "total": -3.74,
			  "taxLines": [{ "label": "TAX1", "amount": -0.25 }]
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — negatives preserved at High confidence
		receipt.Subtotal.Value.Should().Be(-3.49m);
		receipt.Subtotal.Confidence.Should().Be(ConfidenceLevel.High);
		receipt.Total.Value.Should().Be(-3.74m);
		receipt.Items[0].TotalPrice.Value.Should().Be(-3.49m);
		receipt.TaxLines[0].Amount.Value.Should().Be(-0.25m);
	}

	[Fact]
	public void MapToParsedReceipt_NaNAndInfinityDecimals_PreservedAsHighConfidence()
	{
		// Arrange — JSON cannot represent NaN/Infinity natively (they round-trip
		// only through extension-string formats), so this case is exercised by
		// hand-constructing a VlmReceiptPayload with the special values. The
		// mapper's contract is "if a decimal is present, surface it at High
		// confidence". Downstream domain validation flags impossible totals;
		// the OCR layer's job is faithful capture, not arithmetic policy.
		VlmReceiptPayload payload = new()
		{
			Store = new VlmStore { Name = "Walmart" },
			Total = decimal.MaxValue,
			Subtotal = decimal.MinValue,
			Items =
			[
				new VlmReceiptItem
				{
					Description = "MILK",
					LineTotal = decimal.Zero,
					Quantity = decimal.MaxValue,
					UnitPrice = decimal.MinValue,
				},
			],
		};

		// Act
		ParsedReceipt receipt = OllamaReceiptExtractionService.MapToParsedReceipt(payload);

		// Assert
		receipt.Total.Value.Should().Be(decimal.MaxValue);
		receipt.Total.Confidence.Should().Be(ConfidenceLevel.High);
		receipt.Subtotal.Value.Should().Be(decimal.MinValue);
		receipt.Items[0].Quantity.Value.Should().Be(decimal.MaxValue);
		receipt.Items[0].UnitPrice.Value.Should().Be(decimal.MinValue);
	}

	[Fact]
	public async Task ExtractAsync_TotalNull_YieldsNoneConfidence()
	{
		// Arrange — RECEIPTS-647: an explicit JSON null for `total` is
		// indistinguishable from "field absent" because the deserialized model
		// uses nullable decimals. Both must produce ConfidenceLevel.None
		// (RECEIPTS-631: None is now distinct from Low — Low(0m) carries a
		// real-but-uncertain reading, whereas the absent / explicit-null cases
		// have no value to surface).
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "datetime": "2026-04-01",
			  "total": null,
			  "subtotal": null
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		receipt.Total.Value.Should().Be(default(decimal));
		receipt.Total.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.Subtotal.Confidence.Should().Be(ConfidenceLevel.None);
	}

	[Fact]
	public async Task ExtractAsync_TotalMissing_YieldsNoneConfidenceMatchingExplicitNull()
	{
		// Arrange — companion to ExtractAsync_TotalNull_YieldsNoneConfidence:
		// confirms that an entirely absent `total` key produces the same
		// surface result. The service must not differentiate between "the VLM
		// decided not to emit the field" and "the VLM emitted null".
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "datetime": "2026-04-01"
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		receipt.Total.Value.Should().Be(default(decimal));
		receipt.Total.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.Subtotal.Confidence.Should().Be(ConfidenceLevel.None);
	}

	[Fact]
	public void MapToParsedReceipt_AllFieldsAbsent_ReturnsAllNoneConfidence()
	{
		// Arrange — direct mapper test: an empty payload (everything null /
		// missing) must produce a ParsedReceipt where every scalar is None
		// and every list is empty. This is the contract the scan handler's
		// IsEmpty check relies on (RECEIPTS-631: None is now distinct from
		// Low for value types).
		VlmReceiptPayload payload = new();

		// Act
		ParsedReceipt receipt = OllamaReceiptExtractionService.MapToParsedReceipt(payload);

		// Assert
		receipt.StoreName.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.StoreAddress.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.StorePhone.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.Date.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.Subtotal.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.Total.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.PaymentMethod.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.ReceiptId.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.StoreNumber.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.TerminalId.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.Items.Should().BeEmpty();
		receipt.TaxLines.Should().BeEmpty();
		receipt.Payments.Should().BeEmpty();
	}

	[Fact]
	public void MapToParsedReceipt_StorePresentButFieldsBlank_YieldsNoneConfidence()
	{
		// Arrange — direct mapper test: a Store object that exists but has
		// only whitespace fields must produce None confidence (the mapper
		// guards every string with !IsNullOrWhiteSpace, so the empty / blank
		// path is the contract).
		VlmReceiptPayload payload = new()
		{
			Store = new VlmStore { Name = "   ", Address = "", Phone = null },
		};

		// Act
		ParsedReceipt receipt = OllamaReceiptExtractionService.MapToParsedReceipt(payload);

		// Assert
		receipt.StoreName.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.StoreAddress.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.StoreAddress.Value.Should().BeNull();
		receipt.StorePhone.Confidence.Should().Be(ConfidenceLevel.None);
		receipt.StorePhone.Value.Should().BeNull();
	}

	[Fact]
	public async Task ExtractAsync_RetryThenSuccess_ReturnsReceipt()
	{
		// Arrange — build a DI pipeline with retry. Fail twice with 503, then succeed.
		int callCount = 0;
		string successBody = WrapInOllamaEnvelope("""{ "schema_version": 1, "store": { "name": "Walmart" }, "total": 10.00 }""");

		Mock<HttpMessageHandler> handlerMock = new();
		handlerMock.Protected()
			.Setup<Task<HttpResponseMessage>>("SendAsync",
				ItExpr.IsAny<HttpRequestMessage>(),
				ItExpr.IsAny<CancellationToken>())
			.Returns(() =>
			{
				int call = Interlocked.Increment(ref callCount);
				HttpResponseMessage message = call <= 2
					? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
					{
						Content = new StringContent("{}"),
					}
					: new HttpResponseMessage(HttpStatusCode.OK)
					{
						Content = new StringContent(successBody, System.Text.Encoding.UTF8, "application/json"),
					};
				return Task.FromResult(message);
			});

		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton<IOptions<VlmOcrOptions>>(Options.Create(new VlmOcrOptions
		{
			OllamaUrl = "http://test-ollama",
			Model = "glm-ocr:q8_0",
			TimeoutSeconds = 30,
		}));
		services.AddHttpClient<IReceiptExtractionService, OllamaReceiptExtractionService>(client =>
		{
			client.BaseAddress = new Uri("http://test-ollama/");
			client.Timeout = Timeout.InfiniteTimeSpan;
		})
		.ConfigurePrimaryHttpMessageHandler(() => handlerMock.Object)
		.AddResilienceHandler("vlm-ocr-test", builder =>
		{
			builder.AddRetry(new HttpRetryStrategyOptions
			{
				MaxRetryAttempts = 3,
				Delay = TimeSpan.FromMilliseconds(1),
				BackoffType = DelayBackoffType.Constant,
				ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
					.Handle<HttpRequestException>()
					.HandleResult(r => r.StatusCode is HttpStatusCode.ServiceUnavailable
						or HttpStatusCode.TooManyRequests
						or HttpStatusCode.GatewayTimeout),
			});
		});

		using ServiceProvider sp = services.BuildServiceProvider();
		IReceiptExtractionService service = sp.GetRequiredService<IReceiptExtractionService>();

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		receipt.StoreName.Value.Should().Be("Walmart");
		receipt.Total.Value.Should().Be(10.00m);
		callCount.Should().Be(3); // 2 transient failures + 1 success
	}

	// -------------------------------------------------------------------------
	// RECEIPTS-639: schema_version, prompt observability, raw-response gating,
	// exception-message truncation, and shared client registration helper.
	// -------------------------------------------------------------------------

	[Fact]
	public async Task ExtractAsync_SchemaVersionMissing_ThrowsInvalidOperationException()
	{
		// Arrange — a payload without schema_version represents either an old VLM model that
		// pre-dates the schema bump or a corrupted/wrong-shape response. Either way, we MUST
		// fail loudly rather than silently degrade fields.
		string innerJson = """
			{
			  "store": { "name": "Walmart" },
			  "total": 10.00
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — the exception message must NOT contain the raw payload (PII gate) but MUST
		// describe the version mismatch clearly enough to debug from telemetry.
		ExceptionAssertions<InvalidOperationException> thrown = await act.Should().ThrowAsync<InvalidOperationException>();
		thrown.Which.Message.Should().Contain("schema_version mismatch");
		thrown.Which.Message.Should().Contain("expected=1");
		thrown.Which.Message.Should().Contain("actual=null");
		thrown.Which.Message.Should().Contain("promptVersion=V4");
		thrown.Which.Message.Should().NotContain("Walmart");
	}

	[Fact]
	public async Task ExtractAsync_SchemaVersionMismatch_ThrowsInvalidOperationException()
	{
		// Arrange — a payload claiming schema_version: 2 from a model trained against a newer
		// schema. Same fail-fast contract — we don't try to interpret a future shape.
		string innerJson = """
			{
			  "schema_version": 2,
			  "store": { "name": "Walmart" },
			  "total": 10.00
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		ExceptionAssertions<InvalidOperationException> thrown = await act.Should().ThrowAsync<InvalidOperationException>();
		thrown.Which.Message.Should().Contain("schema_version mismatch");
		thrown.Which.Message.Should().Contain("expected=1");
		thrown.Which.Message.Should().Contain("actual=2");
		thrown.Which.Message.Should().NotContain("Walmart");
	}

	[Fact]
	public async Task ExtractAsync_LogRawResponses_DefaultFalse_DoesNotLogRawBody()
	{
		// Arrange — the raw response carries receipt PII. With LogRawResponses unset (default
		// false) the body MUST NOT appear in any log message.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart Supercenter" },
			  "total": 70.43,
			  "payments": [
			    { "method": "MCARD", "amount": 70.43, "lastFour": "3409" }
			  ]
			}
			""";
		CapturingLogger<OllamaReceiptExtractionService> logger = new();
		HttpClient httpClient = new(CreateHandler(WrapInOllamaEnvelope(innerJson)))
		{
			BaseAddress = new Uri("http://test-ollama/"),
		};
		VlmOcrOptions options = new()
		{
			OllamaUrl = "http://test-ollama",
			Model = "glm-ocr:q8_0",
			TimeoutSeconds = 30,
			// Default — LogRawResponses left false.
		};
		OllamaReceiptExtractionService service = new(httpClient, Options.Create(options), logger);

		// Act
		await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — every captured log message is free of PII fragments from the body.
		logger.Records.Should().NotBeEmpty();
		logger.Records.Should().NotContain(record => record.FormattedMessage.Contains("Walmart Supercenter"));
		logger.Records.Should().NotContain(record => record.FormattedMessage.Contains("3409"));
		logger.Records.Should().NotContain(record => record.FormattedMessage.Contains("MCARD"));
		logger.Records.Should().NotContain(record => record.FormattedMessage.Contains("raw response", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task ExtractAsync_LogRawResponses_True_DoesLogRawBody()
	{
		// Arrange — sanity check: when explicitly enabled (e.g. by VlmEval), the raw body
		// surfaces in the debug log. This is the only path that should ever expose PII to logs,
		// and is gated to local diagnostic flows.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart Supercenter" },
			  "total": 70.43
			}
			""";
		CapturingLogger<OllamaReceiptExtractionService> logger = new();
		HttpClient httpClient = new(CreateHandler(WrapInOllamaEnvelope(innerJson)))
		{
			BaseAddress = new Uri("http://test-ollama/"),
		};
		VlmOcrOptions options = new()
		{
			OllamaUrl = "http://test-ollama",
			Model = "glm-ocr:q8_0",
			TimeoutSeconds = 30,
			LogRawResponses = true,
		};
		OllamaReceiptExtractionService service = new(httpClient, Options.Create(options), logger);

		// Act
		await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		logger.Records.Should().Contain(record =>
			record.FormattedMessage.Contains("Walmart Supercenter")
			&& record.FormattedMessage.Contains("raw response", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task ExtractAsync_PromptVersion_FlowsIntoLogScope()
	{
		// Arrange — every log emitted during the extraction must inherit a scope containing
		// the prompt version so a regression can be traced back to the prompt that produced it.
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "total": 10.00
			}
			""";
		CapturingLogger<OllamaReceiptExtractionService> logger = new();
		HttpClient httpClient = new(CreateHandler(WrapInOllamaEnvelope(innerJson)))
		{
			BaseAddress = new Uri("http://test-ollama/"),
		};
		VlmOcrOptions options = new()
		{
			OllamaUrl = "http://test-ollama",
			Model = "glm-ocr:q8_0",
			TimeoutSeconds = 30,
		};
		OllamaReceiptExtractionService service = new(httpClient, Options.Create(options), logger);

		// Act
		await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — at least one record carries a scope with VlmPromptVersion=V4.
		logger.Records.Should().NotBeEmpty();
		bool anyRecordHasVersionScope = logger.Records.Any(record =>
			record.Scopes.Any(scope =>
				scope.TryGetValue("VlmPromptVersion", out object? v) && v as string == "V4"));
		anyRecordHasVersionScope.Should().BeTrue("the prompt version must flow into a structured log scope so logs can be filtered by it");
	}

	[Fact]
	public async Task ExtractAsync_MalformedJson_TruncatesRawResponseInExceptionMessage()
	{
		// Arrange — a malformed response longer than 500 chars must NOT be copied verbatim into
		// the exception message (telemetry leak risk). The truncation marker confirms the cap
		// was applied. The full body still flows through the gated raw-response debug log when
		// the operator opts in.
		string oversizedGarbage = "this is not JSON " + new string('x', 1000);
		string envelope = WrapInOllamaEnvelope(oversizedGarbage);
		OllamaReceiptExtractionService service = CreateService(CreateHandler(envelope));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		ExceptionAssertions<InvalidOperationException> thrown = await act.Should().ThrowAsync<InvalidOperationException>();
		thrown.Which.Message.Should().Contain("[truncated]");
		thrown.Which.Message.Length.Should().BeLessThan(oversizedGarbage.Length);
		thrown.Which.InnerException.Should().BeOfType<JsonException>();
	}

	[Theory]
	[InlineData("short value", 500, "short value")]
	[InlineData("", 100, "")]
	public void Truncate_InputShorterThanOrEqualToLimit_ReturnedVerbatim(string input, int max, string expected)
	{
		// Act
		string result = OllamaReceiptExtractionService.Truncate(input, max);

		// Assert
		result.Should().Be(expected);
	}

	[Fact]
	public void Truncate_InputLongerThanLimit_TruncatedAndAppendedWithMarker()
	{
		// Act
		string result = OllamaReceiptExtractionService.Truncate(new string('a', 1000), 500);

		// Assert — first 500 chars preserved, suffix appended so callers can tell.
		result.Should().StartWith(new string('a', 500));
		result.Should().EndWith("[truncated]");
	}

	[Fact]
	public void Truncate_CutBoundaryInsideSurrogatePair_DoesNotOrphanHighSurrogate()
	{
		// Arrange — RECEIPTS-639 bug-finder follow-up. A supplementary Unicode character (here
		// U+1F4A9 PILE OF POO, encoded as the surrogate pair D83D DCA9) costs two UTF-16 code
		// units. If the cut boundary lands precisely between them, a naive slice would produce
		// a string ending in a lone high surrogate D83D, which System.Text.Json (the default
		// formatter for many structured log sinks) rejects in strict mode — masking the
		// original exception in telemetry. Build a string whose code unit at index
		// `maxChars - 1` is a high surrogate to force the boundary case.
		const int maxChars = 10;
		string padding = new('a', maxChars - 1);              // 9 'a' chars
		string supplementary = "\uD83D\uDCA9";                // single emoji = 2 code units
		string input = padding + supplementary + new string('b', 100);
		// input[maxChars - 1] = input[9] = D83D (high surrogate).

		// Act
		string result = OllamaReceiptExtractionService.Truncate(input, maxChars);

		// Assert — every high surrogate in the result must be followed by a low surrogate.
		for (int i = 0; i < result.Length; i++)
		{
			if (char.IsHighSurrogate(result[i]))
			{
				bool hasPair = i + 1 < result.Length && char.IsLowSurrogate(result[i + 1]);
				hasPair.Should().BeTrue(
					$"position {i} of the truncated result must not be a lone high surrogate (result={result})");
			}
		}

		// Round-trip the result through System.Text.Json — this is what would happen when the
		// exception message is serialized into a log sink. With the bug present, this throws.
		Action serialize = () => System.Text.Json.JsonSerializer.Serialize(result);
		serialize.Should().NotThrow();
	}

	[Fact]
	public void AddVlmOcrClient_BlankOllamaUrl_Throws()
	{
		// Arrange — RECEIPTS-640: VlmOcrOptions.OllamaUrl is non-nullable with a localhost
		// default, so to exercise the helper's guard we explicitly clear the URL to whitespace.
		// RECEIPTS-638: VlmOcrOptions.OllamaUrl is decorated with [Required(AllowEmptyStrings =
		// false)] and the instance overload runs full DataAnnotations validation so the error
		// message surfaces the constraint that failed (consistent with the IConfiguration
		// overload's ValidateOnStart pipeline).
		ServiceCollection services = new();
		services.AddLogging();
		VlmOcrOptions options = new() { Model = "glm-ocr:q8_0", OllamaUrl = "   " };

		// Act
		Action act = () => services.AddVlmOcrClient(options);

		// Assert
		act.Should().Throw<ArgumentException>().WithMessage("*OllamaUrl*");
	}

	[Fact]
	public void AddVlmOcrClient_Instance_EmptyModel_Throws()
	{
		// Arrange — the instance overload must enforce the same DataAnnotations contract as
		// the IConfiguration overload. Without this, a VlmEval consumer that misconfigures
		// Ocr:Vlm:Model="" would surface the failure as an opaque Ollama 400 instead of a
		// clear startup error. RECEIPTS-638 follow-up bug fix.
		ServiceCollection services = new();
		services.AddLogging();
		VlmOcrOptions options = new()
		{
			OllamaUrl = "http://test-ollama",
			Model = "",
		};

		// Act
		Action act = () => services.AddVlmOcrClient(options);

		// Assert
		act.Should().Throw<ArgumentException>().WithMessage("*Model*");
	}

	[Fact]
	public void AddVlmOcrClient_Instance_OutOfRangeTimeout_Throws()
	{
		// Arrange — TimeoutSeconds=0 is outside [Range(1, 3600)]. The instance overload must
		// catch this at registration so VlmEval doesn't silently register a client whose every
		// retry expires immediately.
		ServiceCollection services = new();
		services.AddLogging();
		VlmOcrOptions options = new()
		{
			OllamaUrl = "http://test-ollama",
			Model = "glm-ocr:q8_0",
			TimeoutSeconds = 0,
		};

		// Act
		Action act = () => services.AddVlmOcrClient(options);

		// Assert
		act.Should().Throw<ArgumentException>().WithMessage("*TimeoutSeconds*");
	}

	[Fact]
	public void AddVlmOcrClient_Instance_RegistersServiceAndOptions()
	{
		// Arrange — verify the instance overload (used by VlmEval) wires the typed client
		// and exposes the same VlmOcrOptions instance via IOptions<VlmOcrOptions>. Since
		// RECEIPTS-638 the helper no longer registers the bare VlmOcrOptions instance —
		// consumers resolve it through IOptions<VlmOcrOptions>.
		ServiceCollection services = new();
		services.AddLogging();
		VlmOcrOptions options = new()
		{
			OllamaUrl = "http://test-ollama",
			Model = "glm-ocr:q8_0",
			TimeoutSeconds = 60,
		};

		// Act
		services.AddVlmOcrClient(options);

		// Assert — both the typed client and IOptions<VlmOcrOptions> are wired, with the
		// IOptions wrapper backed by the same instance the caller passed in.
		using ServiceProvider sp = services.BuildServiceProvider();
		sp.GetRequiredService<IOptions<VlmOcrOptions>>().Value.Should().BeSameAs(options);
		sp.GetRequiredService<IReceiptExtractionService>().Should().BeOfType<OllamaReceiptExtractionService>();
	}

	[Fact]
	public void RegisterReceiptExtractionService_AspireOnlyConfig_PrefersOllamaBaseUrl()
	{
		// Arrange — RECEIPTS-640 regression: when the production Aspire deployment injects
		// only Ollama:BaseUrl (no Ocr:Vlm:OllamaUrl override), the registration must pick that
		// URL up. Before the fix, the non-null DefaultOllamaUrl on VlmOcrOptions short-circuited
		// the IsNullOrWhiteSpace gate, ResolveOllamaUrl was never called, and every production
		// scan request ended up targeting localhost:11434 inside the API container — silent
		// connection-refused failures. Pin the corrected behavior.
		ServiceCollection services = new();
		services.AddLogging();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				// No Ocr:Vlm:OllamaUrl key — only the Aspire-injected Ollama:BaseUrl
				[ConfigurationVariables.OllamaBaseUrl] = "http://aspire-injected:11434",
				[$"{ConfigurationVariables.OcrVlmSection}:{nameof(VlmOcrOptions.Model)}"] = "glm-ocr:q8_0",
			})
			.Build();

		// Act
		InfrastructureService.RegisterReceiptExtractionService(services, configuration);

		// Assert — the registered VlmOcrOptions points at the Aspire-injected URL, not the
		// localhost default that would otherwise leak through.
		using ServiceProvider sp = services.BuildServiceProvider();
		VlmOcrOptions registered = sp.GetRequiredService<IOptions<VlmOcrOptions>>().Value;
		registered.OllamaUrl.Should().Be("http://aspire-injected:11434");
	}

	[Fact]
	public void RegisterReceiptExtractionService_OcrVlmOverrideTakesPrecedence()
	{
		// Arrange — when both Ocr:Vlm:OllamaUrl and Ollama:BaseUrl are set, the explicit
		// Ocr:Vlm:OllamaUrl override wins. ResolveOllamaUrl encodes this priority chain;
		// pin it here so a future refactor can't reverse the precedence silently.
		ServiceCollection services = new();
		services.AddLogging();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[ConfigurationVariables.OcrVlmOllamaUrl] = "http://override:11434",
				[ConfigurationVariables.OllamaBaseUrl] = "http://aspire-injected:11434",
			})
			.Build();

		// Act
		InfrastructureService.RegisterReceiptExtractionService(services, configuration);

		// Assert
		using ServiceProvider sp = services.BuildServiceProvider();
		VlmOcrOptions registered = sp.GetRequiredService<IOptions<VlmOcrOptions>>().Value;
		registered.OllamaUrl.Should().Be("http://override:11434");
	}

	[Fact]
	public void RegisterReceiptExtractionService_NoConfig_FallsBackToLocalhostDefault()
	{
		// Arrange — when neither key is set (a developer running `dotnet run` without Aspire
		// and without env vars), the localhost default kicks in so the service still builds.
		// AddVlmOcrClient enforces non-blank URL — this is the only place the default applies.
		ServiceCollection services = new();
		services.AddLogging();
		IConfiguration configuration = new ConfigurationBuilder().Build();

		// Act
		InfrastructureService.RegisterReceiptExtractionService(services, configuration);

		// Assert
		using ServiceProvider sp = services.BuildServiceProvider();
		VlmOcrOptions registered = sp.GetRequiredService<IOptions<VlmOcrOptions>>().Value;
		registered.OllamaUrl.Should().Be(VlmOcrOptions.DefaultOllamaUrl);
	}

	[Fact]
	public async Task ExtractAsync_DoneFalse_SingleObject_ThrowsTruncationError()
	{
		// Arrange — a single-line JSON response with done=false (e.g. an unhealthy daemon
		// returning the first chunk only, or a truncated response that arrived as a single
		// non-streaming object). The payload is mid-stream and the JSON is incomplete, so
		// any downstream JsonException would be a confusing red herring. Surface the real
		// cause early. See RECEIPTS-640 (original guard) and the NDJSON consolidation that
		// motivated rephrasing the error around truncation rather than streaming.
		string envelope = JsonSerializer.Serialize(new
		{
			model = "glm-ocr:q8_0",
			response = "{ \"schema_version\": 1, \"store\": { \"name\": \"Walmart\" }, \"total\": 10.0 }",
			done = false,
		});
		OllamaReceiptExtractionService service = CreateService(CreateHandler(envelope));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*truncated*done=true*");
	}

	[Fact]
	public async Task ExtractAsync_ImageExceedsMaxBytes_ThrowsArgumentException()
	{
		// Arrange — RECEIPTS-640: an image larger than VlmOcrOptions.MaxImageBytes must be
		// rejected before any base64 encoding (which inflates the byte count by ~33%) and before
		// any HTTP traffic is generated. Ollama's default request body limit is well below 50 MB+
		// camera dumps; this guard turns an opaque downstream HttpRequestException into a clear
		// ArgumentException at the boundary.
		VlmOcrOptions options = new()
		{
			OllamaUrl = "http://test-ollama",
			Model = "glm-ocr:q8_0",
			TimeoutSeconds = 30,
			MaxImageBytes = 100, // tiny limit so the test image trips it
		};
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope("{}")), options);
		byte[] oversized = new byte[101];

		// Act
		Func<Task> act = () => service.ExtractAsync(oversized, CancellationToken.None);

		// Assert
		ExceptionAssertions<ArgumentException> thrown = await act.Should().ThrowAsync<ArgumentException>();
		thrown.Which.ParamName.Should().Be("imageBytes");
		thrown.Which.Message.Should().Contain("101");
		thrown.Which.Message.Should().Contain("100");
	}

	[Fact]
	public async Task ExtractAsync_ImageAtMaxBytes_DoesNotThrow()
	{
		// Arrange — boundary check: an image exactly at MaxImageBytes is allowed (the guard
		// uses strict `>`, not `>=`). This pins behavior so a future tightening to `>=` would
		// fail loudly rather than silently rejecting at-size payloads.
		VlmOcrOptions options = new()
		{
			OllamaUrl = "http://test-ollama",
			Model = "glm-ocr:q8_0",
			TimeoutSeconds = 30,
			MaxImageBytes = 100,
		};
		string innerJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "total": 1.0
			}
			""";
		OllamaReceiptExtractionService service = CreateService(CreateHandler(WrapInOllamaEnvelope(innerJson)), options);
		byte[] atLimit = new byte[100];

		// Act / Assert
		await service.ExtractAsync(atLimit, CancellationToken.None);
	}

	[Fact]
	public void Constructor_NullHttpClient_Throws()
	{
		// RECEIPTS-640: tighten ctor null-check pattern so all three injected dependencies have
		// the same guard. Previously only imageBytes had a guard; ctor params were unchecked.
		Action act = () => new OllamaReceiptExtractionService(
			null!, Options.Create(new VlmOcrOptions { OllamaUrl = "http://test" }), NullLogger<OllamaReceiptExtractionService>.Instance);

		act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("httpClient");
	}

	[Fact]
	public void Constructor_NullOptions_Throws()
	{
		Action act = () => new OllamaReceiptExtractionService(
			new HttpClient { BaseAddress = new Uri("http://test/") },
			null!,
			NullLogger<OllamaReceiptExtractionService>.Instance);

		act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("options");
	}

	[Fact]
	public void Constructor_NullLogger_Throws()
	{
		Action act = () => new OllamaReceiptExtractionService(
			new HttpClient { BaseAddress = new Uri("http://test/") },
			Options.Create(new VlmOcrOptions { OllamaUrl = "http://test" }),
			null!);

		act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("logger");
	}

	[Fact]
	public void AddVlmOcrClient_Configuration_ValidatesOnStart()
	{
		// Arrange — bind from a configuration source missing TimeoutSeconds (out of [Range(1, 3600)]
		// only when explicitly 0; default is 120). Use an out-of-range TimeoutSeconds=0 to trigger
		// validation. ValidateOnStart causes the failure to surface when IOptions<VlmOcrOptions>
		// is first resolved (or when the host starts) rather than at the first request.
		ServiceCollection services = new();
		services.AddLogging();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[$"{ConfigurationVariables.OcrVlmSection}:{nameof(VlmOcrOptions.OllamaUrl)}"] = "http://test-ollama",
				[$"{ConfigurationVariables.OcrVlmSection}:{nameof(VlmOcrOptions.Model)}"] = "glm-ocr:q8_0",
				[$"{ConfigurationVariables.OcrVlmSection}:{nameof(VlmOcrOptions.TimeoutSeconds)}"] = "0",
			})
			.Build();

		services.AddVlmOcrClient(configuration);

		// Act
		using ServiceProvider sp = services.BuildServiceProvider();
		Func<VlmOcrOptions> act = () => sp.GetRequiredService<IOptions<VlmOcrOptions>>().Value;

		// Assert — DataAnnotations [Range(1, 3600)] rejects TimeoutSeconds=0 with a clear message.
		act.Should().Throw<OptionsValidationException>()
			.WithMessage("*TimeoutSeconds*");
	}

	[Fact]
	public void AddVlmOcrClient_Configuration_AppliesPostConfigureFallback()
	{
		// Arrange — when neither Ocr:Vlm:OllamaUrl nor Ollama:BaseUrl is set, PostConfigure
		// must fall back to localhost so the bound options pass [Required] validation. This
		// preserves the historical fallback chain RegisterReceiptExtractionService used.
		ServiceCollection services = new();
		services.AddLogging();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection([])
			.Build();

		services.AddVlmOcrClient(configuration);

		// Act
		using ServiceProvider sp = services.BuildServiceProvider();
		VlmOcrOptions value = sp.GetRequiredService<IOptions<VlmOcrOptions>>().Value;

		// Assert — fallback applied; defaults preserved for everything else.
		value.OllamaUrl.Should().Be("http://localhost:11434");
		value.Model.Should().Be(VlmOcrOptions.DefaultModel);
		value.TimeoutSeconds.Should().Be(120);
	}

	[Fact]
	public void AddVlmOcrClient_Configuration_PrefersExplicitOllamaUrlOverAspireBaseUrl()
	{
		// Arrange — when both Ocr:Vlm:OllamaUrl and Ollama:BaseUrl are present, the explicit
		// Ocr:Vlm:OllamaUrl wins. This guards against the canonical fallback chain regressing.
		ServiceCollection services = new();
		services.AddLogging();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[ConfigurationVariables.OcrVlmOllamaUrl] = "http://explicit-ollama:11434",
				[ConfigurationVariables.OllamaBaseUrl] = "http://aspire-ollama:11434",
			})
			.Build();

		services.AddVlmOcrClient(configuration);

		// Act
		using ServiceProvider sp = services.BuildServiceProvider();
		VlmOcrOptions value = sp.GetRequiredService<IOptions<VlmOcrOptions>>().Value;

		// Assert — explicit override beat the Aspire-injected base URL.
		value.OllamaUrl.Should().Be("http://explicit-ollama:11434");
	}

	[Fact]
	public void AddVlmOcrClient_Configuration_FallsBackToAspireBaseUrlWhenExplicitMissing()
	{
		// Arrange — Aspire-injected Ollama:BaseUrl wins when the explicit Ocr:Vlm:OllamaUrl
		// is absent. This is the production-Aspire case (no explicit override).
		ServiceCollection services = new();
		services.AddLogging();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[ConfigurationVariables.OllamaBaseUrl] = "http://aspire-ollama:11434",
			})
			.Build();

		services.AddVlmOcrClient(configuration);

		// Act
		using ServiceProvider sp = services.BuildServiceProvider();
		VlmOcrOptions value = sp.GetRequiredService<IOptions<VlmOcrOptions>>().Value;

		// Assert
		value.OllamaUrl.Should().Be("http://aspire-ollama:11434");
	}

	// ParseOllamaResponse: handles both single-JSON-object and NDJSON streaming responses
	// transparently. Some Ollama versions stream image-input responses despite stream=false on
	// the request; the parser folds the chunks back into one synthetic OllamaGenerateResponse
	// so callers see a single coherent object.

	[Fact]
	public async Task ExtractAsync_NdjsonStreamedResponse_ReassemblesToFullPayload()
	{
		// Arrange — Ollama streamed the response across 3 chunks despite our stream=false
		// (observed on glm-ocr with rasterized-PDF inputs). Each line is a valid
		// OllamaGenerateResponse; chunks 1-2 carry partial JSON in `response`, chunk 3 has
		// done=true and an empty `response`.
		string ndjsonBody = string.Join('\n',
			"""{"model":"glm-ocr:q8_0","response":"{ \"schema_version\": 1, \"store\": { \"name\": \"Walmart\" },","done":false}""",
			"""{"model":"glm-ocr:q8_0","response":" \"datetime\": \"2026-04-01\", \"total\": 9.71 }","done":false}""",
			"""{"model":"glm-ocr:q8_0","response":"","done":true}""");
		OllamaReceiptExtractionService service = CreateService(CreateHandler(ndjsonBody));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — the joined chunks reconstruct a valid VLM payload that maps cleanly.
		receipt.StoreName.Value.Should().Be("Walmart");
		receipt.Date.Value.Should().Be(new DateOnly(2026, 4, 1));
		receipt.Total.Value.Should().Be(9.71m);
	}

	[Fact]
	public async Task ExtractAsync_NdjsonTruncatedStream_NoDoneTrueChunk_ThrowsTruncationError()
	{
		// Arrange — connection dropped before Ollama emitted the final done=true chunk. The
		// reassembled response is partial JSON, but the truncation detector fires before any
		// downstream JsonException can mask the cause.
		string truncatedNdjson = string.Join('\n',
			"""{"model":"glm-ocr:q8_0","response":"{ \"schema_version\": 1, \"store\":","done":false}""",
			"""{"model":"glm-ocr:q8_0","response":" { \"name\": \"Walmart\"","done":false}""");
		OllamaReceiptExtractionService service = CreateService(CreateHandler(truncatedNdjson));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		ExceptionAssertions<InvalidOperationException> thrown =
			await act.Should().ThrowAsync<InvalidOperationException>()
				.WithMessage("*truncated*");
		thrown.Which.Message.Should().Contain("done=true");
	}

	[Fact]
	public void ParseOllamaResponse_EmptyBody_ReturnsNull()
	{
		// Arrange — defensive: a literally empty / whitespace-only body must not throw and
		// must not produce a synthetic OllamaGenerateResponse with stale defaults.
		OllamaGenerateResponse? r1 = OllamaReceiptExtractionService.ParseOllamaResponse(string.Empty);
		OllamaGenerateResponse? r2 = OllamaReceiptExtractionService.ParseOllamaResponse("   \n  \n");

		// Assert
		r1.Should().BeNull();
		r2.Should().BeNull();
	}

	[Fact]
	public void ParseOllamaResponse_SingleJsonObject_ParsesDirectly()
	{
		// Arrange — the happy path: stream=false honored by the daemon, body is a single
		// JSON object on one line.
		string singleObject =
			"""{"model":"glm-ocr:q8_0","response":"{ \"schema_version\": 1 }","done":true}""";

		// Act
		OllamaGenerateResponse? response = OllamaReceiptExtractionService.ParseOllamaResponse(singleObject);

		// Assert
		response.Should().NotBeNull();
		response!.Done.Should().BeTrue();
		response.Response.Should().Contain("schema_version");
		response.Model.Should().Be("glm-ocr:q8_0");
	}

	// ----------------------------------------------------------------------
	// RECEIPTS-654: shared retry predicate is wired into the Ollama VLM client
	// pipeline. The predicate is tested in detail in
	// AnthropicReceiptExtractionServiceTests; this test verifies the same
	// policy applies to the Ollama side, since AddRetryAndCircuitBreaker is
	// shared between the two clients.
	// ----------------------------------------------------------------------

	[Fact]
	public async Task Resilience_PermanentClientError_400_IsNotRetried_OllamaPipeline()
	{
		// Arrange — the same retry predicate applies to the Ollama client. A permanent
		// 4xx must not be retried even when emitted from the local Ollama daemon.
		int callCount = 0;
		Mock<HttpMessageHandler> handlerMock = new();
		handlerMock.Protected()
			.Setup<Task<HttpResponseMessage>>("SendAsync",
				ItExpr.IsAny<HttpRequestMessage>(),
				ItExpr.IsAny<CancellationToken>())
			.Returns(() =>
			{
				Interlocked.Increment(ref callCount);
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
				{
					Content = new StringContent("""{ "error": "bad request" }""",
						System.Text.Encoding.UTF8, "application/json"),
				});
			});
		VlmOcrOptions options = new()
		{
			OllamaUrl = "http://test-ollama",
			Model = "glm-ocr:q8_0",
			TimeoutSeconds = 30,
		};

		await RunWithPipelineServiceAsync(handlerMock.Object, options, async service =>
		{
			// Act
			Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

			// Assert
			await act.Should().ThrowAsync<Exception>();
			callCount.Should().Be(1, "HTTP 400 must not be retried by the shared predicate");
		});
	}

	[Fact]
	public async Task Resilience_TransientServerError_503_IsRetried_OllamaPipeline()
	{
		// Arrange — control case: a 503 must still trigger a retry on the Ollama client.
		int callCount = 0;
		Mock<HttpMessageHandler> handlerMock = new();
		handlerMock.Protected()
			.Setup<Task<HttpResponseMessage>>("SendAsync",
				ItExpr.IsAny<HttpRequestMessage>(),
				ItExpr.IsAny<CancellationToken>())
			.Returns(() =>
			{
				Interlocked.Increment(ref callCount);
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
				{
					Content = new StringContent("{}"),
				});
			});
		VlmOcrOptions options = new()
		{
			OllamaUrl = "http://test-ollama",
			Model = "glm-ocr:q8_0",
			TimeoutSeconds = 30,
		};

		await RunWithPipelineServiceAsync(handlerMock.Object, options, async service =>
		{
			// Act
			Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

			// Assert
			await act.Should().ThrowAsync<Exception>();
			callCount.Should().BeGreaterThan(1, "HTTP 503 must be retried");
		});
	}

	/// <summary>
	/// Captures formatted log messages and the active scope chain at log time, so tests can
	/// assert both message content (for raw-response PII gating) and ambient scope state
	/// (for prompt-version observability).
	/// </summary>
	private sealed class CapturingLogger<T> : ILogger<T>
	{
		private readonly List<IReadOnlyDictionary<string, object?>> _activeScopes = [];

		public List<LogRecord> Records { get; } = [];

		IDisposable? ILogger.BeginScope<TState>(TState state)
			where TState : default
		{
			Dictionary<string, object?> snapshot = state is IEnumerable<KeyValuePair<string, object>> pairs
				? pairs.ToDictionary(p => p.Key, p => (object?)p.Value)
				: new Dictionary<string, object?> { ["__state"] = state };
			_activeScopes.Add(snapshot);
			return new ScopeReleaser(_activeScopes);
		}

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			Records.Add(new LogRecord(
				logLevel,
				formatter(state, exception),
				_activeScopes.Select(s => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(s)).ToList()));
		}

		private sealed class ScopeReleaser(List<IReadOnlyDictionary<string, object?>> scopes) : IDisposable
		{
			public void Dispose()
			{
				if (scopes.Count > 0)
				{
					scopes.RemoveAt(scopes.Count - 1);
				}
			}
		}

		public sealed record LogRecord(
			LogLevel Level,
			string FormattedMessage,
			IReadOnlyList<IReadOnlyDictionary<string, object?>> Scopes);
	}
}
