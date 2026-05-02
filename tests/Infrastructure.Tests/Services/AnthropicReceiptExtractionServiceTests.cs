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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Polly;
using SkiaSharp;

namespace Infrastructure.Tests.Services;

public class AnthropicReceiptExtractionServiceTests
{
	private static readonly byte[] FakeImage = [0x89, 0x50, 0x4E, 0x47];

	private static AnthropicReceiptExtractionService CreateService(
		HttpMessageHandler handler,
		AnthropicOptions? options = null)
	{
		HttpClient httpClient = new(handler) { BaseAddress = new Uri("https://api.anthropic.com/") };
		options ??= new AnthropicOptions
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
			TimeoutSeconds = 30,
		};
		return new AnthropicReceiptExtractionService(
			httpClient,
			Options.Create(options),
			NullLogger<AnthropicReceiptExtractionService>.Instance);
	}

	/// <summary>
	/// Builds a synthetic Anthropic Messages API response carrying a single tool_use block
	/// whose <c>input</c> deserializes to <see cref="VlmReceiptPayload"/>. <paramref name="payloadJson"/>
	/// is interpolated as the raw JSON object the model "passed" to the submit_receipt tool.
	/// </summary>
	private static string WrapInToolUseEnvelope(string payloadJson)
	{
		using JsonDocument payloadDoc = JsonDocument.Parse(payloadJson);
		string canonicalPayload = payloadDoc.RootElement.GetRawText();

		return $$"""
			{
			  "id": "msg_test",
			  "type": "message",
			  "role": "assistant",
			  "model": "claude-haiku-4-5",
			  "stop_reason": "tool_use",
			  "content": [
			    {
			      "type": "tool_use",
			      "id": "toolu_test",
			      "name": "submit_receipt",
			      "input": {{canonicalPayload}}
			    }
			  ],
			  "usage": {
			    "input_tokens": 10,
			    "output_tokens": 50,
			    "cache_creation_input_tokens": 0,
			    "cache_read_input_tokens": 0
			  }
			}
			""";
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

	private static async Task RunWithPipelineServiceAsync(
		HttpMessageHandler primaryHandler,
		AnthropicOptions options,
		Func<IReceiptExtractionService, Task> action)
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton<IOptions<AnthropicOptions>>(Options.Create(options));
#pragma warning disable EXTEXP0001
		services.AddHttpClient<IReceiptExtractionService, AnthropicReceiptExtractionService>(client =>
		{
			client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
			client.DefaultRequestHeaders.Add("x-api-key", options.ApiKey);
			client.DefaultRequestHeaders.Add("anthropic-version", options.ApiVersion);
			client.Timeout = Timeout.InfiniteTimeSpan;
		})
		.ConfigurePrimaryHttpMessageHandler(() => primaryHandler)
		.RemoveAllResilienceHandlers()
		.AddResilienceHandler("anthropic-vlm-test", builder =>
			InfrastructureService.ConfigureAnthropicVlmResilience(builder, options));
#pragma warning restore EXTEXP0001

		await using ServiceProvider sp = services.BuildServiceProvider();
		IReceiptExtractionService service = sp.GetRequiredService<IReceiptExtractionService>();
		await action(service);
	}

	[Fact]
	public async Task ExtractAsync_HappyPath_ParsesToolUseInputCorrectly()
	{
		// Arrange — full V2/V3 shape: nested store, datetime, lineTotal,
		// nullable quantity/unitPrice, taxCode, payments, identifiers.
		string payloadJson = """
			{
			  "schema_version": 1,
			  "store": {
			    "name": "Walmart Supercenter",
			    "address": "9 BENTON RD, TRAVELERS REST SC 29690",
			    "phone": "864-834-7179"
			  },
			  "datetime": "2026-01-14T17:57:20",
			  "items": [
			    { "description": "GRANULATED", "code": "078742228030", "lineTotal": 3.07,
			      "quantity": null, "unitPrice": null, "taxCode": "F" },
			    { "description": "BANANAS", "code": "000000004011", "lineTotal": 1.23,
			      "quantity": 2.46, "unitPrice": 0.50, "taxCode": "N" }
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
		AnthropicReceiptExtractionService service = CreateService(CreateHandler(WrapInToolUseEnvelope(payloadJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		receipt.StoreName.Value.Should().Be("Walmart Supercenter");
		receipt.StoreName.Confidence.Should().Be(ConfidenceLevel.High);
		receipt.Date.Value.Should().Be(new DateOnly(2026, 1, 14));
		receipt.Items.Should().HaveCount(2);
		receipt.Items[0].Code.Value.Should().Be("078742228030");
		receipt.Items[0].TotalPrice.Value.Should().Be(3.07m);
		receipt.Items[1].Quantity.Value.Should().Be(2.46m);
		receipt.Total.Value.Should().Be(70.43m);
		receipt.Payments.Should().HaveCount(1);
		receipt.Payments[0].LastFour.Value.Should().Be("3409");
		receipt.Payments[0].LastFour.Confidence.Should().Be(ConfidenceLevel.High);
	}

	[Fact]
	public async Task ExtractAsync_ToolUseInput_AsCanonicalJsonObject_RoundTripsCleanly()
	{
		// Arrange — guards the contract that the model's tool_use.input arrives as a JSON
		// object (not a string-encoded JSON blob). The Anthropic API returns input as a
		// raw JSON object per the public docs; if a future API change wraps it in a string
		// the deserialize call will throw and surface in tests rather than silently passing.
		string payloadJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "datetime": "2026-04-01",
			  "total": 10.00
			}
			""";
		AnthropicReceiptExtractionService service = CreateService(CreateHandler(WrapInToolUseEnvelope(payloadJson)));

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		receipt.StoreName.Value.Should().Be("Walmart");
		receipt.Total.Value.Should().Be(10.00m);
	}

	[Fact]
	public async Task ExtractAsync_SchemaVersionMissing_ThrowsInvalidOperationException()
	{
		// Arrange — same fail-fast contract as the Ollama service (RECEIPTS-639). A payload
		// without schema_version is either an old model or wrong-shape response.
		string payloadJson = """
			{
			  "store": { "name": "Walmart" },
			  "total": 10.00
			}
			""";
		AnthropicReceiptExtractionService service = CreateService(CreateHandler(WrapInToolUseEnvelope(payloadJson)));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		ExceptionAssertions<InvalidOperationException> thrown = await act.Should().ThrowAsync<InvalidOperationException>();
		thrown.Which.Message.Should().Contain("schema_version mismatch");
		thrown.Which.Message.Should().Contain("expected=1");
		thrown.Which.Message.Should().Contain("actual=null");
		thrown.Which.Message.Should().Contain("promptVersion=V4");
		thrown.Which.Message.Should().NotContain("Walmart"); // PII gate
	}

	[Fact]
	public async Task ExtractAsync_SchemaVersionMismatch_ThrowsInvalidOperationException()
	{
		// Arrange — payload claims a future schema version. Don't try to interpret it.
		string payloadJson = """
			{
			  "schema_version": 2,
			  "store": { "name": "Walmart" },
			  "total": 10.00
			}
			""";
		AnthropicReceiptExtractionService service = CreateService(CreateHandler(WrapInToolUseEnvelope(payloadJson)));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		ExceptionAssertions<InvalidOperationException> thrown = await act.Should().ThrowAsync<InvalidOperationException>();
		thrown.Which.Message.Should().Contain("schema_version mismatch");
		thrown.Which.Message.Should().Contain("actual=2");
		thrown.Which.Message.Should().NotContain("Walmart");
	}

	[Fact]
	public async Task ExtractAsync_NoToolUseBlock_ThrowsInvalidOperationException()
	{
		// Arrange — model refused or emitted a free-form text response. With tool_choice
		// pinned this should be rare, but it must surface a clear error rather than NRE.
		string envelope = """
			{
			  "id": "msg_test",
			  "type": "message",
			  "role": "assistant",
			  "model": "claude-haiku-4-5",
			  "stop_reason": "end_turn",
			  "content": [
			    { "type": "text", "text": "I cannot read this receipt." }
			  ]
			}
			""";
		AnthropicReceiptExtractionService service = CreateService(CreateHandler(envelope));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		ExceptionAssertions<InvalidOperationException> thrown = await act.Should().ThrowAsync<InvalidOperationException>();
		thrown.Which.Message.Should().Contain("did not call the submit_receipt tool");
		thrown.Which.Message.Should().Contain("stop_reason=end_turn");
		thrown.Which.Message.Should().Contain("I cannot read");
	}

	[Fact]
	public async Task ExtractAsync_MalformedToolUseInput_ThrowsWithTruncatedRawInputInMessage()
	{
		// Arrange — tool_use block exists but `input` is shaped wrong (items as object,
		// not array). The deserializer raises JsonException; service translates to
		// InvalidOperationException with a truncated raw-input fragment for debugging.
		string envelope = """
			{
			  "id": "msg_test",
			  "model": "claude-haiku-4-5",
			  "stop_reason": "tool_use",
			  "content": [
			    {
			      "type": "tool_use",
			      "id": "toolu_test",
			      "name": "submit_receipt",
			      "input": {
			        "schema_version": 1,
			        "items": "not an array"
			      }
			    }
			  ]
			}
			""";
		AnthropicReceiptExtractionService service = CreateService(CreateHandler(envelope));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		ExceptionAssertions<InvalidOperationException> thrown = await act.Should().ThrowAsync<InvalidOperationException>();
		thrown.Which.Message.Should().Contain("Failed to parse Anthropic VLM tool input");
		thrown.Which.InnerException.Should().BeOfType<JsonException>();
	}

	[Fact]
	public async Task ExtractAsync_ApiError_SurfacesStructuredErrorMessage()
	{
		// Arrange — a 401 with the documented Anthropic error envelope.
		string errorBody = """
			{
			  "type": "error",
			  "error": {
			    "type": "authentication_error",
			    "message": "x-api-key header is invalid"
			  }
			}
			""";
		AnthropicReceiptExtractionService service = CreateService(CreateHandler(errorBody, HttpStatusCode.Unauthorized));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — error type and message both surface; HTTP status code present.
		ExceptionAssertions<InvalidOperationException> thrown = await act.Should().ThrowAsync<InvalidOperationException>();
		thrown.Which.Message.Should().Contain("HTTP 401");
		thrown.Which.Message.Should().Contain("authentication_error");
		thrown.Which.Message.Should().Contain("x-api-key header is invalid");
	}

	[Fact]
	public async Task ExtractAsync_ApiErrorWithUnparseableBody_SurfacesTruncatedBody()
	{
		// Arrange — Cloudflare or proxy returned HTML instead of JSON. Falls back to
		// a generic message that includes the truncated raw body for debugging.
		const string htmlBody = "<html><body>Service Unavailable</body></html>";
		AnthropicReceiptExtractionService service = CreateService(CreateHandler(htmlBody, HttpStatusCode.ServiceUnavailable));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		ExceptionAssertions<InvalidOperationException> thrown = await act.Should().ThrowAsync<InvalidOperationException>();
		thrown.Which.Message.Should().Contain("HTTP 503");
		thrown.Which.Message.Should().Contain("Service Unavailable");
	}

	[Fact]
	public async Task ExtractAsync_OperationCanceled_Propagates()
	{
		// Arrange — handler blocks until canceled; caller cancels via CTS.
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
		AnthropicReceiptExtractionService service = CreateService(handlerMock.Object);
		using CancellationTokenSource cts = new();
		cts.CancelAfter(TimeSpan.FromMilliseconds(50));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, cts.Token);

		// Assert — caller-initiated cancellation surfaces as OperationCanceledException.
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task ExtractAsync_CancellationToken_PassedThroughToHandler()
	{
		// Arrange — capture the token the handler receives and confirm it tracks the
		// caller's CTS. Mirrors the Ollama suite's capture-callback assertion.
		CancellationToken capturedToken = default;
		Mock<HttpMessageHandler> handlerMock = new();
		handlerMock.Protected()
			.Setup<Task<HttpResponseMessage>>("SendAsync",
				ItExpr.IsAny<HttpRequestMessage>(),
				ItExpr.IsAny<CancellationToken>())
			.Returns((HttpRequestMessage _, CancellationToken ct) =>
			{
				capturedToken = ct;
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(WrapInToolUseEnvelope("""{ "schema_version": 1 }"""),
						System.Text.Encoding.UTF8, "application/json"),
				});
			});
		AnthropicReceiptExtractionService service = CreateService(handlerMock.Object);
		using CancellationTokenSource cts = new();

		// Act
		await service.ExtractAsync(FakeImage, cts.Token);

		// Assert — handler saw a non-default token (HttpClient wraps it but it remains
		// linked to the caller's token).
		capturedToken.Should().NotBe(default(CancellationToken));
	}

	[Fact]
	public async Task ExtractAsync_Timeout_ThrowsTimeoutException()
	{
		// Arrange — handler delays past the per-attempt timeout. The Polly Timeout
		// strategy aborts the attempt; the service translates TimeoutRejectedException
		// into TimeoutException — same surface contract as the Ollama service.
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
		AnthropicOptions options = new()
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
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
	public async Task ExtractAsync_Request_TargetsMessagesEndpointAndIncludesPromptCacheControl()
	{
		// Arrange — capture the outgoing request body and assert the canonical Anthropic
		// shape: tool_choice pinned to submit_receipt, prompt block cache_control: ephemeral,
		// image as base64, model + max_tokens stamped from options.
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
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(WrapInToolUseEnvelope("""{ "schema_version": 1 }"""),
						System.Text.Encoding.UTF8, "application/json"),
				};
			});
		AnthropicReceiptExtractionService service = CreateService(handlerMock.Object);

		// Act
		await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — request shape
		capturedRequest.Should().NotBeNull();
		capturedRequest!.Method.Should().Be(HttpMethod.Post);
		capturedRequest.RequestUri!.AbsolutePath.Should().EndWith("/v1/messages");
		capturedBody.Should().NotBeNullOrEmpty();

		using JsonDocument doc = JsonDocument.Parse(capturedBody!);
		JsonElement root = doc.RootElement;
		root.GetProperty("model").GetString().Should().Be("claude-haiku-4-5");
		root.GetProperty("max_tokens").GetInt32().Should().BeGreaterThan(0);

		// tool_choice forces the model to call submit_receipt.
		JsonElement toolChoice = root.GetProperty("tool_choice");
		toolChoice.GetProperty("type").GetString().Should().Be("tool");
		toolChoice.GetProperty("name").GetString().Should().Be("submit_receipt");

		// system block carries the prompt with cache_control: ephemeral.
		JsonElement system = root.GetProperty("system");
		system.GetArrayLength().Should().BeGreaterThan(0);
		JsonElement promptBlock = system[0];
		promptBlock.GetProperty("type").GetString().Should().Be("text");
		promptBlock.GetProperty("text").GetString().Should().Contain("receipt");
		promptBlock.GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");

		// User message contains the image as base64 PNG.
		JsonElement messages = root.GetProperty("messages");
		messages.GetArrayLength().Should().Be(1);
		JsonElement userMsg = messages[0];
		userMsg.GetProperty("role").GetString().Should().Be("user");
		JsonElement userContent = userMsg.GetProperty("content");
		bool foundImage = false;
		for (int i = 0; i < userContent.GetArrayLength(); i++)
		{
			JsonElement block = userContent[i];
			if (string.Equals(block.GetProperty("type").GetString(), "image", StringComparison.Ordinal))
			{
				JsonElement source = block.GetProperty("source");
				source.GetProperty("type").GetString().Should().Be("base64");
				source.GetProperty("media_type").GetString().Should().Be("image/png");
				source.GetProperty("data").GetString().Should().Be(Convert.ToBase64String(FakeImage));
				foundImage = true;
			}
		}
		foundImage.Should().BeTrue();

		// Tool definition references submit_receipt and an input_schema describing the payload.
		JsonElement tools = root.GetProperty("tools");
		tools.GetArrayLength().Should().Be(1);
		tools[0].GetProperty("name").GetString().Should().Be("submit_receipt");
		tools[0].TryGetProperty("input_schema", out _).Should().BeTrue();
	}

	[Fact]
	public async Task RegisterReceiptExtractionService_AnthropicProvider_SlowResponse_NotCancelledByServiceDefaultsStandardHandler()
	{
		// Regression for the same RECEIPTS-630 contract on the Anthropic side. The
		// production composition is: ServiceDefaults injects an aggressive standard
		// handler (200ms attempt timeout); RegisterReceiptExtractionService must remove
		// it. A handler that delays 1s — past the 200ms ceiling but well under the
		// per-call Anthropic timeout — must complete successfully.
		Mock<HttpMessageHandler> handlerMock = new();
		string successBody = WrapInToolUseEnvelope("""{ "schema_version": 1, "store": { "name": "Walmart" }, "total": 10.00 }""");
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

		// Replicate Receipts.ServiceDefaults: aggressive standard handler everywhere.
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

		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[ConfigurationVariables.OcrVlmProvider] = "anthropic",
				[$"{ConfigurationVariables.AnthropicSection}:{nameof(AnthropicOptions.ApiKey)}"] = "test-api-key",
				[$"{ConfigurationVariables.AnthropicSection}:{nameof(AnthropicOptions.TimeoutSeconds)}"] = "5",
			})
			.Build();

		InfrastructureService.RegisterReceiptExtractionService(services, configuration);

		// Override primary handler so we don't hit the real API.
		services.AddHttpClient<IReceiptExtractionService, AnthropicReceiptExtractionService>()
			.ConfigurePrimaryHttpMessageHandler(() => handlerMock.Object);

		using ServiceProvider sp = services.BuildServiceProvider();
		IReceiptExtractionService service = sp.GetRequiredService<IReceiptExtractionService>();

		// Act
		ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — the call survives despite the aggressive standard handler.
		receipt.StoreName.Value.Should().Be("Walmart");
		receipt.Total.Value.Should().Be(10.00m);
	}

	[Fact]
	public async Task ExtractAsync_ImageExceedsMaxBytes_ThrowsArgumentException()
	{
		// Arrange — same RECEIPTS-640 guard as the Ollama side, on the Anthropic options.
		AnthropicOptions options = new()
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
			TimeoutSeconds = 30,
			MaxImageBytes = 100,
		};
		AnthropicReceiptExtractionService service = CreateService(
			CreateHandler(WrapInToolUseEnvelope("""{ "schema_version": 1 }""")), options);
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
		// Arrange — boundary check: an image exactly at MaxImageBytes is allowed (the
		// guard uses strict `>`, not `>=`).
		AnthropicOptions options = new()
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
			TimeoutSeconds = 30,
			MaxImageBytes = 100,
		};
		string payloadJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "total": 1.0
			}
			""";
		AnthropicReceiptExtractionService service = CreateService(CreateHandler(WrapInToolUseEnvelope(payloadJson)), options);
		byte[] atLimit = new byte[100];

		// Act / Assert — no throw
		await service.ExtractAsync(atLimit, CancellationToken.None);
	}

	[Fact]
	public async Task ExtractAsync_EmptyImageBytes_ThrowsArgumentException()
	{
		// Arrange
		AnthropicReceiptExtractionService service = CreateService(
			CreateHandler(WrapInToolUseEnvelope("""{ "schema_version": 1 }""")));

		// Act
		Func<Task> act = () => service.ExtractAsync(Array.Empty<byte>(), CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<ArgumentException>()
			.WithMessage("*cannot be empty*");
	}

	[Fact]
	public void Constructor_NullHttpClient_Throws()
	{
		Action act = () => new AnthropicReceiptExtractionService(
			null!, Options.Create(new AnthropicOptions { ApiKey = "k" }), NullLogger<AnthropicReceiptExtractionService>.Instance);

		act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("httpClient");
	}

	[Fact]
	public void Constructor_NullOptions_Throws()
	{
		Action act = () => new AnthropicReceiptExtractionService(
			new HttpClient { BaseAddress = new Uri("http://test/") },
			null!,
			NullLogger<AnthropicReceiptExtractionService>.Instance);

		act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("options");
	}

	[Fact]
	public void Constructor_NullLogger_Throws()
	{
		Action act = () => new AnthropicReceiptExtractionService(
			new HttpClient { BaseAddress = new Uri("http://test/") },
			Options.Create(new AnthropicOptions { ApiKey = "k" }),
			null!);

		act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("logger");
	}

	[Fact]
	public async Task AddAnthropicVlmClient_HostStartAsync_FailsAtStartupWhenApiKeyMissing()
	{
		// Arrange — RECEIPTS-652 / RECEIPTS-638: a missing ApiKey must fail when the host
		// starts, before the first user upload. ValidateOnStart() registers an
		// IHostedService that runs validation during IHost.StartAsync().
		HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			// No ApiKey — should fail validation.
			[$"{ConfigurationVariables.AnthropicSection}:{nameof(AnthropicOptions.Model)}"] = "claude-haiku-4-5",
		});
		builder.Services.AddLogging();
		builder.Services.AddAnthropicVlmClient(builder.Configuration);

		using IHost host = builder.Build();

		// Act
		Func<Task> act = () => host.StartAsync();

		// Assert
		await act.Should().ThrowAsync<OptionsValidationException>()
			.WithMessage("*ApiKey*");
	}

	[Fact]
	public void AddAnthropicVlmClient_Instance_EmptyApiKey_Throws()
	{
		// Arrange — instance overload must enforce the same DataAnnotations contract as
		// the IConfiguration overload. Mirrors VlmEval consumer pattern (RECEIPTS-638).
		ServiceCollection services = new();
		services.AddLogging();
		AnthropicOptions options = new() { ApiKey = "" };

		// Act
		Action act = () => services.AddAnthropicVlmClient(options);

		// Assert
		act.Should().Throw<ArgumentException>().WithMessage("*ApiKey*");
	}

	[Fact]
	public void AddAnthropicVlmClient_Instance_OutOfRangeTimeout_Throws()
	{
		// Arrange — TimeoutSeconds=0 outside [Range(1, 600)].
		ServiceCollection services = new();
		services.AddLogging();
		AnthropicOptions options = new() { ApiKey = "k", TimeoutSeconds = 0 };

		// Act
		Action act = () => services.AddAnthropicVlmClient(options);

		// Assert
		act.Should().Throw<ArgumentException>().WithMessage("*TimeoutSeconds*");
	}

	[Fact]
	public void AddAnthropicVlmClient_HappyPath_RegistersServiceAndOptions()
	{
		// Arrange
		ServiceCollection services = new();
		services.AddLogging();
		AnthropicOptions options = new()
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
			TimeoutSeconds = 60,
		};

		// Act
		services.AddAnthropicVlmClient(options);

		// Assert
		using ServiceProvider sp = services.BuildServiceProvider();
		sp.GetRequiredService<IOptions<AnthropicOptions>>().Value.Should().BeSameAs(options);
		sp.GetRequiredService<IReceiptExtractionService>().Should().BeOfType<AnthropicReceiptExtractionService>();
	}

	[Fact]
	public void RegisterReceiptExtractionService_ProviderAnthropic_ResolvesAnthropicService()
	{
		// Arrange — Provider=anthropic must resolve AnthropicReceiptExtractionService.
		ServiceCollection services = new();
		services.AddLogging();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[ConfigurationVariables.OcrVlmProvider] = "anthropic",
				[$"{ConfigurationVariables.AnthropicSection}:{nameof(AnthropicOptions.ApiKey)}"] = "test-api-key",
			})
			.Build();

		// Act
		InfrastructureService.RegisterReceiptExtractionService(services, configuration);

		// Assert
		using ServiceProvider sp = services.BuildServiceProvider();
		sp.GetRequiredService<IReceiptExtractionService>().Should().BeOfType<AnthropicReceiptExtractionService>();
	}

	[Fact]
	public void RegisterReceiptExtractionService_ProviderOllama_ResolvesOllamaService()
	{
		// Arrange — Provider=ollama (or unset) must resolve OllamaReceiptExtractionService.
		ServiceCollection services = new();
		services.AddLogging();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[ConfigurationVariables.OcrVlmProvider] = "ollama",
			})
			.Build();

		// Act
		InfrastructureService.RegisterReceiptExtractionService(services, configuration);

		// Assert
		using ServiceProvider sp = services.BuildServiceProvider();
		sp.GetRequiredService<IReceiptExtractionService>().Should().BeOfType<OllamaReceiptExtractionService>();
	}

	[Fact]
	public void RegisterReceiptExtractionService_ProviderUnset_DefaultsToOllama()
	{
		// Arrange — no Provider key set: default behavior must be Ollama (preserves the
		// existing production default while the POC graduates).
		ServiceCollection services = new();
		services.AddLogging();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection([])
			.Build();

		// Act
		InfrastructureService.RegisterReceiptExtractionService(services, configuration);

		// Assert
		using ServiceProvider sp = services.BuildServiceProvider();
		sp.GetRequiredService<IReceiptExtractionService>().Should().BeOfType<OllamaReceiptExtractionService>();
	}

	[Fact]
	public void RegisterReceiptExtractionService_UnknownProvider_Throws()
	{
		// Arrange — typo'd provider value must fail loudly so the deployment doesn't
		// silently fall back to a default.
		ServiceCollection services = new();
		services.AddLogging();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[ConfigurationVariables.OcrVlmProvider] = "openai", // not supported
			})
			.Build();

		// Act
		Action act = () => InfrastructureService.RegisterReceiptExtractionService(services, configuration);

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Unknown VLM provider*openai*");
	}

	[Fact]
	public void RegisterReceiptExtractionService_ProviderAnthropic_CaseInsensitive()
	{
		// Arrange — provider key value is case-insensitive: "Anthropic" works the same
		// as "anthropic". A future env-var typo where someone mixed case must not
		// silently fall through to Ollama.
		ServiceCollection services = new();
		services.AddLogging();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[ConfigurationVariables.OcrVlmProvider] = "Anthropic",
				[$"{ConfigurationVariables.AnthropicSection}:{nameof(AnthropicOptions.ApiKey)}"] = "test-api-key",
			})
			.Build();

		// Act
		InfrastructureService.RegisterReceiptExtractionService(services, configuration);

		// Assert
		using ServiceProvider sp = services.BuildServiceProvider();
		sp.GetRequiredService<IReceiptExtractionService>().Should().BeOfType<AnthropicReceiptExtractionService>();
	}

	// ----------------------------------------------------------------------
	// Truncate helper (mirror of OllamaReceiptExtractionService.Truncate tests)
	// ----------------------------------------------------------------------

	[Theory]
	[InlineData("short", 500, "short")]
	[InlineData("", 100, "")]
	public void Truncate_InputShorterThanOrEqualToLimit_ReturnedVerbatim(string input, int max, string expected)
	{
		string result = AnthropicReceiptExtractionService.Truncate(input, max);
		result.Should().Be(expected);
	}

	[Fact]
	public void Truncate_InputLongerThanLimit_TruncatedAndAppendedWithMarker()
	{
		string result = AnthropicReceiptExtractionService.Truncate(new string('a', 1000), 500);
		result.Should().StartWith(new string('a', 500));
		result.Should().EndWith("[truncated]");
	}

	[Fact]
	public void Truncate_CutBoundaryInsideSurrogatePair_DoesNotOrphanHighSurrogate()
	{
		// Same surrogate-pair safety contract as the Ollama side. Without it, a lone
		// high surrogate slips into exception messages and crashes downstream JSON
		// formatters in strict mode (see OllamaReceiptExtractionServiceTests for the
		// full RECEIPTS-639 follow-up rationale).
		const int maxChars = 10;
		string padding = new('a', maxChars - 1);
		string supplementary = "\uD83D\uDCA9"; // single emoji, 2 code units
		string input = padding + supplementary + new string('b', 100);

		string result = AnthropicReceiptExtractionService.Truncate(input, maxChars);

		for (int i = 0; i < result.Length; i++)
		{
			if (char.IsHighSurrogate(result[i]))
			{
				bool hasPair = i + 1 < result.Length && char.IsLowSurrogate(result[i + 1]);
				hasPair.Should().BeTrue($"position {i} of the truncated result must not be a lone high surrogate");
			}
		}

		Action serialize = () => JsonSerializer.Serialize(result);
		serialize.Should().NotThrow();
	}

	[Fact]
	public async Task ExtractAsync_LogRawResponses_DefaultFalse_DoesNotLogRawBody()
	{
		// Arrange — same PII gate as Ollama. Default false; raw body must NOT appear in logs.
		string payloadJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart Supercenter" },
			  "total": 70.43,
			  "payments": [
			    { "method": "MCARD", "amount": 70.43, "lastFour": "3409" }
			  ]
			}
			""";
		CapturingLogger<AnthropicReceiptExtractionService> logger = new();
		HttpClient httpClient = new(CreateHandler(WrapInToolUseEnvelope(payloadJson)))
		{
			BaseAddress = new Uri("https://api.anthropic.com/"),
		};
		AnthropicOptions options = new()
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
			TimeoutSeconds = 30,
			// Default — LogRawResponses left false.
		};
		AnthropicReceiptExtractionService service = new(httpClient, Options.Create(options), logger);

		// Act
		await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — no log message contains PII fragments from the body.
		logger.Records.Should().NotBeEmpty();
		logger.Records.Should().NotContain(record => record.FormattedMessage.Contains("Walmart Supercenter"));
		logger.Records.Should().NotContain(record => record.FormattedMessage.Contains("3409"));
		logger.Records.Should().NotContain(record => record.FormattedMessage.Contains("MCARD"));
	}

	[Fact]
	public async Task ExtractAsync_PromptVersion_FlowsIntoLogScope()
	{
		// Arrange — same provider-agnostic observability contract: every record must
		// inherit a scope containing the prompt version.
		string payloadJson = """
			{
			  "schema_version": 1,
			  "store": { "name": "Walmart" },
			  "total": 10.00
			}
			""";
		CapturingLogger<AnthropicReceiptExtractionService> logger = new();
		HttpClient httpClient = new(CreateHandler(WrapInToolUseEnvelope(payloadJson)))
		{
			BaseAddress = new Uri("https://api.anthropic.com/"),
		};
		AnthropicOptions options = new()
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
			TimeoutSeconds = 30,
		};
		AnthropicReceiptExtractionService service = new(httpClient, Options.Create(options), logger);

		// Act
		await service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert
		logger.Records.Should().NotBeEmpty();
		bool anyRecordHasVersionScope = logger.Records.Any(record =>
			record.Scopes.Any(scope =>
				scope.TryGetValue("VlmPromptVersion", out object? v) && v as string == "V4"));
		anyRecordHasVersionScope.Should().BeTrue("the prompt version must flow into a structured log scope");

		bool anyRecordHasProviderScope = logger.Records.Any(record =>
			record.Scopes.Any(scope =>
				scope.TryGetValue("VlmProvider", out object? v) && v as string == "anthropic"));
		anyRecordHasProviderScope.Should().BeTrue("the provider tag must flow into the structured log scope");
	}

	// ----------------------------------------------------------------------
	// RECEIPTS-654: Auto-downscale path for Anthropic image-size cap.
	// Mirrors the failure mode where a 200 DPI rasterized PDF base64-encodes
	// past Anthropic's 5 MB per-image API limit. The service must downscale
	// before the encode rather than letting the API reject with an opaque 400.
	// ----------------------------------------------------------------------

	/// <summary>
	/// Builds a real PNG of <paramref name="width"/>x<paramref name="height"/> filled with
	/// natural-image-like content (a horizontal gradient + sparse noise) so PNG compression
	/// produces non-trivial output but still scales predictably. A solid-color PNG would
	/// compress to a few bytes regardless of dimensions, defeating the size-driven test.
	/// </summary>
	private static byte[] BuildSyntheticPng(int width, int height)
	{
		using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
		// Fill with a deterministic gradient + noise pattern so the encoded PNG is
		// non-trivially sized but still a valid image. Random-seeded so each call is
		// reproducible without test flakiness.
		Random rng = new(width * 31 + height);
		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				byte r = (byte)((x * 255) / Math.Max(1, width - 1));
				byte g = (byte)((y * 255) / Math.Max(1, height - 1));
				byte b = (byte)rng.Next(256);
				bitmap.SetPixel(x, y, new SKColor(r, g, b, 255));
			}
		}
		using SKImage image = SKImage.FromBitmap(bitmap);
		using SKData encoded = image.Encode(SKEncodedImageFormat.Png, quality: 100);
		return encoded.ToArray();
	}

	[Fact]
	public async Task ExtractAsync_OversizeImage_DownscalesUntilFits()
	{
		// Arrange — synthesize a PNG that overflows the configured raw-byte cap. The
		// service must downscale before encoding and the outgoing request body must
		// carry an image small enough that base64 stays under Anthropic's 5 MB API limit.
		byte[] largeImage = BuildSyntheticPng(2400, 2400);
		largeImage.Length.Should().BeGreaterThan(2_000_000,
			"synthetic gradient must be large enough to require downscaling");

		// Configure a tight cap so downscaling is forced even on the synthetic image.
		AnthropicOptions options = new()
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
			TimeoutSeconds = 30,
			MaxRawImageBytes = 500_000, // well under largeImage.Length
			MaxImageBytes = 50 * 1024 * 1024, // hard ceiling well above input
		};

		string? capturedDataField = null;
		Mock<HttpMessageHandler> handlerMock = new();
		handlerMock.Protected()
			.Setup<Task<HttpResponseMessage>>("SendAsync",
				ItExpr.IsAny<HttpRequestMessage>(),
				ItExpr.IsAny<CancellationToken>())
			.Returns(async (HttpRequestMessage req, CancellationToken _) =>
			{
				string body = await req.Content!.ReadAsStringAsync();
				using JsonDocument doc = JsonDocument.Parse(body);
				JsonElement userContent = doc.RootElement
					.GetProperty("messages")[0]
					.GetProperty("content");
				for (int i = 0; i < userContent.GetArrayLength(); i++)
				{
					JsonElement block = userContent[i];
					if (string.Equals(block.GetProperty("type").GetString(), "image", StringComparison.Ordinal))
					{
						capturedDataField = block.GetProperty("source").GetProperty("data").GetString();
						break;
					}
				}
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(WrapInToolUseEnvelope("""{ "schema_version": 1 }"""),
						System.Text.Encoding.UTF8, "application/json"),
				};
			});
		AnthropicReceiptExtractionService service = CreateService(handlerMock.Object, options);

		// Act
		await service.ExtractAsync(largeImage, CancellationToken.None);

		// Assert — image was downscaled before encoding.
		capturedDataField.Should().NotBeNull();
		byte[] sentBytes = Convert.FromBase64String(capturedDataField!);
		sentBytes.Length.Should().BeLessThanOrEqualTo(options.MaxRawImageBytes,
			"the downscale loop must produce an output below the configured cap");
		sentBytes.Length.Should().BeLessThan(largeImage.Length,
			"the request body must carry the resampled image, not the original");
	}

	[Fact]
	public void DownscaleIfNeeded_ImageUnderCap_ReturnsOriginalBuffer()
	{
		// Arrange — image smaller than cap must short-circuit (no allocation, no resample)
		// and return the same array reference. This is the hot path for camera/JPEG
		// uploads that already fit comfortably under the limit.
		AnthropicOptions options = new()
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
			TimeoutSeconds = 30,
			MaxRawImageBytes = 1_000_000,
		};
		HttpClient httpClient = new(CreateHandler(WrapInToolUseEnvelope("""{ "schema_version": 1 }""")))
		{
			BaseAddress = new Uri("https://api.anthropic.com/"),
		};
		AnthropicReceiptExtractionService service = new(
			httpClient, Options.Create(options), NullLogger<AnthropicReceiptExtractionService>.Instance);

		byte[] smallImage = BuildSyntheticPng(100, 100);
		smallImage.Length.Should().BeLessThan(options.MaxRawImageBytes);

		// Act
		byte[] result = service.DownscaleIfNeeded(smallImage);

		// Assert — same reference, no allocation.
		result.Should().BeSameAs(smallImage);
	}

	[Fact]
	public void DownscaleIfNeeded_OversizeImage_ResamplesUnderCap()
	{
		// Arrange
		AnthropicOptions options = new()
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
			TimeoutSeconds = 30,
			MaxRawImageBytes = 200_000,
		};
		HttpClient httpClient = new(CreateHandler(WrapInToolUseEnvelope("""{ "schema_version": 1 }""")))
		{
			BaseAddress = new Uri("https://api.anthropic.com/"),
		};
		AnthropicReceiptExtractionService service = new(
			httpClient, Options.Create(options), NullLogger<AnthropicReceiptExtractionService>.Instance);

		byte[] largeImage = BuildSyntheticPng(2000, 2000);
		largeImage.Length.Should().BeGreaterThan(options.MaxRawImageBytes);

		// Act
		byte[] result = service.DownscaleIfNeeded(largeImage);

		// Assert
		result.Length.Should().BeLessThanOrEqualTo(options.MaxRawImageBytes);
		result.Should().NotBeSameAs(largeImage); // resampled buffer
	}

	[Fact]
	public void DownscaleIfNeeded_DownscaleFailureMessageIncludesByteCounts()
	{
		// Arrange — pin MaxRawImageBytes to a value below the absolute floor of any
		// 1×1 PNG (PNG header alone is ~67 bytes for a minimal image), which forces
		// the downscale loop to exhaust its attempts and throw the failure exception.
		// The exception message must carry both byte counts so the user gets actionable
		// feedback ("the image was X bytes, dropped to Y, still over the cap").
		AnthropicOptions options = new()
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
			TimeoutSeconds = 30,
			MaxRawImageBytes = 50, // smaller than any encoded PNG header
		};
		HttpClient httpClient = new(CreateHandler(WrapInToolUseEnvelope("""{ "schema_version": 1 }""")))
		{
			BaseAddress = new Uri("https://api.anthropic.com/"),
		};
		AnthropicReceiptExtractionService service = new(
			httpClient, Options.Create(options), NullLogger<AnthropicReceiptExtractionService>.Instance);

		byte[] image = BuildSyntheticPng(800, 800);
		image.Length.Should().BeGreaterThan(options.MaxRawImageBytes);

		// Act
		Action act = () => service.DownscaleIfNeeded(image);

		// Assert — message names both the original and the final byte count.
		ExceptionAssertions<InvalidOperationException> thrown = act.Should().Throw<InvalidOperationException>();
		thrown.Which.Message.Should().Contain("could not be downscaled");
		thrown.Which.Message.Should().Contain($"original={image.Length}");
		thrown.Which.Message.Should().Contain("final=");
		thrown.Which.Message.Should().Contain("attempts");
	}

	[Fact]
	public void DownscaleIfNeeded_LogsDownscaleAtInfo()
	{
		// Arrange — first downscale path must log at Info so operators see the cap engage
		// in production telemetry. The log must include the original/final byte counts
		// and scale factor — the same info echoed in the failure exception, so a future
		// silent regression (downscale stops but logs nothing) is detectable.
		AnthropicOptions options = new()
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
			TimeoutSeconds = 30,
			MaxRawImageBytes = 200_000,
		};
		CapturingLogger<AnthropicReceiptExtractionService> logger = new();
		HttpClient httpClient = new(CreateHandler(WrapInToolUseEnvelope("""{ "schema_version": 1 }""")))
		{
			BaseAddress = new Uri("https://api.anthropic.com/"),
		};
		AnthropicReceiptExtractionService service = new(
			httpClient, Options.Create(options), logger);

		byte[] largeImage = BuildSyntheticPng(2000, 2000);

		// Act
		service.DownscaleIfNeeded(largeImage);

		// Assert
		logger.Records.Should().Contain(record =>
			record.Level == LogLevel.Information
			&& record.FormattedMessage.Contains("downscaled")
			&& record.FormattedMessage.Contains("originalBytes=")
			&& record.FormattedMessage.Contains("finalBytes=")
			&& record.FormattedMessage.Contains("scale="));
	}

	[Fact]
	public void ResamplePng_ScalesLinearDimensions()
	{
		// Arrange / Act
		byte[] source = BuildSyntheticPng(400, 200);
		byte[] resampled = AnthropicReceiptExtractionService.ResamplePng(source, 0.5);

		// Assert — decoded output has half the linear dimensions.
		using SKBitmap decoded = SKBitmap.Decode(resampled);
		decoded.Width.Should().BeCloseTo(200, 1);
		decoded.Height.Should().BeCloseTo(100, 1);
	}

	[Fact]
	public void ResamplePng_FloorsAtOnePixel()
	{
		// Arrange — extreme scale must not produce zero-pixel output; the helper floors
		// dimensions at 1 to keep SkiaSharp's resize call from rejecting the input.
		byte[] source = BuildSyntheticPng(10, 10);

		// Act
		byte[] resampled = AnthropicReceiptExtractionService.ResamplePng(source, 0.001);

		// Assert
		using SKBitmap decoded = SKBitmap.Decode(resampled);
		decoded.Width.Should().BeGreaterThanOrEqualTo(1);
		decoded.Height.Should().BeGreaterThanOrEqualTo(1);
	}

	// ----------------------------------------------------------------------
	// RECEIPTS-654: Resilience-pipeline retry predicate.
	// Permanent client errors (400, 401, 403, 404, 422) must NOT be retried.
	// Transient errors (408, 429, 500, 502, 503, 504, 529) MUST be retried.
	// ----------------------------------------------------------------------

	/// <summary>
	/// Counts handler invocations, returns the configured response body and status on every
	/// call. The status is stable so retry-driven invocations all hit the same outcome —
	/// useful for "must be invoked exactly once" assertions on permanent errors.
	/// </summary>
	private static (HttpMessageHandler Handler, Func<int> CallCount) CreateCountingHandler(
		HttpStatusCode status,
		string body = "{}")
	{
		int callCount = 0;
		Mock<HttpMessageHandler> handlerMock = new();
		handlerMock.Protected()
			.Setup<Task<HttpResponseMessage>>("SendAsync",
				ItExpr.IsAny<HttpRequestMessage>(),
				ItExpr.IsAny<CancellationToken>())
			.Returns(() =>
			{
				Interlocked.Increment(ref callCount);
				return Task.FromResult(new HttpResponseMessage(status)
				{
					Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
				});
			});
		return (handlerMock.Object, () => callCount);
	}

	private static async Task RunWithProductionPipelineAsync(
		HttpMessageHandler primaryHandler,
		AnthropicOptions options,
		Func<IReceiptExtractionService, Task> action)
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton<IOptions<AnthropicOptions>>(Options.Create(options));
#pragma warning disable EXTEXP0001
		services.AddHttpClient<IReceiptExtractionService, AnthropicReceiptExtractionService>(client =>
		{
			client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
			client.DefaultRequestHeaders.Add("x-api-key", options.ApiKey);
			client.DefaultRequestHeaders.Add("anthropic-version", options.ApiVersion);
			client.Timeout = Timeout.InfiniteTimeSpan;
		})
		.ConfigurePrimaryHttpMessageHandler(() => primaryHandler)
		.RemoveAllResilienceHandlers()
		.AddResilienceHandler("anthropic-vlm-test", builder =>
			InfrastructureService.ConfigureAnthropicVlmResilience(builder, options));
#pragma warning restore EXTEXP0001

		await using ServiceProvider sp = services.BuildServiceProvider();
		IReceiptExtractionService service = sp.GetRequiredService<IReceiptExtractionService>();
		await action(service);
	}

	[Theory]
	[InlineData(HttpStatusCode.BadRequest)]                  // 400
	[InlineData(HttpStatusCode.Unauthorized)]                // 401
	[InlineData(HttpStatusCode.Forbidden)]                   // 403
	[InlineData(HttpStatusCode.NotFound)]                    // 404
	[InlineData(HttpStatusCode.UnprocessableEntity)]         // 422
	public async Task Resilience_PermanentClientError_IsNotRetried(HttpStatusCode status)
	{
		// Arrange — permanent client errors (RECEIPTS-654) must short-circuit the retry
		// pipeline. The Anthropic 400 (image-too-large) was the canonical failure mode
		// that drove this change; the other 4xx codes are covered to lock in the policy.
		(HttpMessageHandler handler, Func<int> callCount) = CreateCountingHandler(
			status,
			"""{ "type": "error", "error": { "type": "invalid_request_error", "message": "test" } }""");
		AnthropicOptions options = new()
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
			TimeoutSeconds = 30,
		};

		await RunWithProductionPipelineAsync(handler, options, async service =>
		{
			// Act
			Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

			// Assert — the call surfaces the upstream error with a single handler invocation.
			await act.Should().ThrowAsync<InvalidOperationException>();
			callCount().Should().Be(1, $"HTTP {(int)status} is permanent and must not be retried");
		});
	}

	[Theory]
	[InlineData(HttpStatusCode.RequestTimeout)]              // 408
	[InlineData(HttpStatusCode.TooManyRequests)]             // 429
	[InlineData(HttpStatusCode.InternalServerError)]         // 500
	[InlineData(HttpStatusCode.BadGateway)]                  // 502
	[InlineData(HttpStatusCode.ServiceUnavailable)]          // 503
	[InlineData(HttpStatusCode.GatewayTimeout)]              // 504
	[InlineData((HttpStatusCode)529)]                        // 529 (Anthropic overloaded)
	public async Task Resilience_TransientServerError_IsRetried(HttpStatusCode status)
	{
		// Arrange — transient errors must be retried. The pipeline retries up to 3 times
		// (4 total attempts). Asserting >1 invocations is sufficient — the exact retry
		// count depends on the circuit breaker / Retry-After / jitter machinery and is
		// not the contract under test here.
		(HttpMessageHandler handler, Func<int> callCount) = CreateCountingHandler(
			status,
			"""{ "type": "error", "error": { "type": "transient", "message": "test" } }""");
		AnthropicOptions options = new()
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
			TimeoutSeconds = 30,
		};

		await RunWithProductionPipelineAsync(handler, options, async service =>
		{
			// Act
			Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

			// Assert — transient errors trigger retries.
			await act.Should().ThrowAsync<Exception>();
			callCount().Should().BeGreaterThan(1, $"HTTP {(int)status} is transient and must be retried");
		});
	}

	[Fact]
	public async Task Resilience_TransientThenSuccess_ReturnsReceipt()
	{
		// Arrange — fail twice with 503, then succeed. Mirror the Ollama test
		// (ExtractAsync_RetryThenSuccess_ReturnsReceipt) on the Anthropic side so the
		// retry pipeline is verified to actually recover, not just to swallow errors.
		int callCount = 0;
		string successBody = WrapInToolUseEnvelope("""{ "schema_version": 1, "store": { "name": "Walmart" }, "total": 10.00 }""");
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
		AnthropicOptions options = new()
		{
			ApiKey = "test-api-key",
			Model = "claude-haiku-4-5",
			TimeoutSeconds = 30,
		};

		await RunWithProductionPipelineAsync(handlerMock.Object, options, async service =>
		{
			// Act
			ParsedReceipt receipt = await service.ExtractAsync(FakeImage, CancellationToken.None);

			// Assert
			receipt.StoreName.Value.Should().Be("Walmart");
			callCount.Should().Be(3); // 2 transient failures + 1 success
		});
	}

	[Fact]
	public void IsRetryableStatusCode_PermanentErrors_ReturnsFalse()
	{
		// Direct unit test of the predicate. Locks in the contract that no permanent
		// 4xx is retryable.
		InfrastructureService.IsRetryableStatusCode(HttpStatusCode.BadRequest).Should().BeFalse();
		InfrastructureService.IsRetryableStatusCode(HttpStatusCode.Unauthorized).Should().BeFalse();
		InfrastructureService.IsRetryableStatusCode(HttpStatusCode.Forbidden).Should().BeFalse();
		InfrastructureService.IsRetryableStatusCode(HttpStatusCode.NotFound).Should().BeFalse();
		InfrastructureService.IsRetryableStatusCode(HttpStatusCode.UnprocessableEntity).Should().BeFalse();
	}

	[Fact]
	public void IsRetryableStatusCode_TransientErrors_ReturnsTrue()
	{
		InfrastructureService.IsRetryableStatusCode(HttpStatusCode.RequestTimeout).Should().BeTrue();
		InfrastructureService.IsRetryableStatusCode(HttpStatusCode.TooManyRequests).Should().BeTrue();
		InfrastructureService.IsRetryableStatusCode(HttpStatusCode.InternalServerError).Should().BeTrue();
		InfrastructureService.IsRetryableStatusCode(HttpStatusCode.BadGateway).Should().BeTrue();
		InfrastructureService.IsRetryableStatusCode(HttpStatusCode.ServiceUnavailable).Should().BeTrue();
		InfrastructureService.IsRetryableStatusCode(HttpStatusCode.GatewayTimeout).Should().BeTrue();
		InfrastructureService.IsRetryableStatusCode((HttpStatusCode)529).Should().BeTrue();
	}

	[Fact]
	public void IsRetryableStatusCode_SuccessCodes_ReturnsFalse()
	{
		// Sanity check — a 200 is not retryable. Helps lock in the policy against an
		// accidental "retry on anything not 2xx" regression.
		InfrastructureService.IsRetryableStatusCode(HttpStatusCode.OK).Should().BeFalse();
		InfrastructureService.IsRetryableStatusCode(HttpStatusCode.Created).Should().BeFalse();
		InfrastructureService.IsRetryableStatusCode(HttpStatusCode.NoContent).Should().BeFalse();
	}

	[Fact]
	public async Task ThrowFromAnthropicError_PreservesUpstreamMessage()
	{
		// Arrange — the user-facing failure mode driven by the original RECEIPTS-654 bug
		// report: a 400 from Anthropic carries an actionable error message
		// ("image exceeds 5 MB maximum: ..."). The InvalidOperationException must carry
		// that message verbatim so it propagates through ScanReceiptCommandHandler and
		// surfaces in the 422 problem-details `detail` field — the user uploading a
		// too-large PDF needs actionable feedback, not "Failed to process scanned receipt".
		const string upstreamMessage = "messages.0.content.0.image.source.base64: image exceeds 5 MB maximum: 7827556 bytes > 5242880 bytes";
		string errorBody = $$"""
			{
			  "type": "error",
			  "error": {
			    "type": "invalid_request_error",
			    "message": "{{upstreamMessage}}"
			  }
			}
			""";
		AnthropicReceiptExtractionService service = CreateService(CreateHandler(errorBody, HttpStatusCode.BadRequest));

		// Act
		Func<Task> act = () => service.ExtractAsync(FakeImage, CancellationToken.None);

		// Assert — the upstream message is verbatim in the exception message, available
		// for ReceiptScanController to relay as the 422 detail.
		ExceptionAssertions<InvalidOperationException> thrown = await act.Should().ThrowAsync<InvalidOperationException>();
		thrown.Which.Message.Should().Contain(upstreamMessage);
		thrown.Which.Message.Should().Contain("HTTP 400");
		thrown.Which.Message.Should().Contain("invalid_request_error");
	}

	/// <summary>
	/// Captures formatted log messages and the active scope chain at log time. Mirrors the
	/// CapturingLogger pattern from <see cref="OllamaReceiptExtractionServiceTests"/>.
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
