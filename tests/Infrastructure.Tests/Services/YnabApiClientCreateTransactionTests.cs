using System.Net;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace Infrastructure.Tests.Services;

public class YnabApiClientCreateTransactionTests
{
	[Theory]
	[InlineData(false, "YNAB:receipt:1")]
	[InlineData(true, "YNAB:receipt:1")]
	[InlineData(false, null)]
	public async Task CreateTransaction_IsUnclearedManualEntry_WithOriginalTransactionFields(bool split, string? importId)
	{
		YnabCreateTransactionRequest request = new(
			"account-1", new DateOnly(2026, 10, 1), -15000, "Milk and bread", "Corner store",
			split ? null : "groceries", false,
			split ? [new(-10000, "groceries", "Milk"), new(-5000, "household", "Soap")] : null,
			importId);

		JsonElement transaction = await SendAndCaptureAsync(request);

		transaction.TryGetProperty("import_id", out _).Should().BeFalse();
		transaction.GetProperty("cleared").GetString().Should().Be("uncleared");
		transaction.GetProperty("account_id").GetString().Should().Be("account-1");
		transaction.GetProperty("date").GetString().Should().Be("2026-10-01");
		transaction.GetProperty("amount").GetInt64().Should().Be(-15000);
		transaction.GetProperty("payee_name").GetString().Should().Be("Corner store");
		transaction.GetProperty("approved").GetBoolean().Should().BeFalse();
		transaction.GetProperty("memo").GetString().Should().Be(importId is null
			? "Milk and bread" : "[Receipts push:YNAB:receipt:1] Milk and bread");
		if (split)
		{
			JsonElement subtransactions = transaction.GetProperty("subtransactions");
			subtransactions.GetArrayLength().Should().Be(2);
			subtransactions[0].GetProperty("amount").GetInt64().Should().Be(-10000);
			subtransactions[0].GetProperty("category_id").GetString().Should().Be("groceries");
			subtransactions[1].GetProperty("amount").GetInt64().Should().Be(-5000);
			subtransactions[1].GetProperty("memo").GetString().Should().Be("Soap");
		}
		else
		{
			transaction.GetProperty("category_id").GetString().Should().Be("groceries");
		}
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("Long")]
	public async Task CreateTransaction_MemoPreservesRecoveryMarkerWithinYnabLimit(string? memo)
	{
		string? originalMemo = memo is "Long" ? new string('x', 250) : memo;
		YnabCreateTransactionRequest request = new(
			"account-1", new DateOnly(2026, 10, 1), -15000, originalMemo, "Store", "groceries", false,
			ImportId: "YNAB:receipt:1");

		JsonElement transaction = await SendAndCaptureAsync(request);

		string expected = memo is "Long"
			? "[Receipts push:YNAB:receipt:1] " + new string('x', 169)
			: "[Receipts push:YNAB:receipt:1]";
		transaction.GetProperty("memo").GetString().Should().Be(expected);
		transaction.GetProperty("memo").GetString()!.Length.Should().BeLessThanOrEqualTo(200);
		request.Memo.Should().Be(originalMemo, "persisted requests must remain unchanged");
	}

	private static async Task<JsonElement> SendAndCaptureAsync(YnabCreateTransactionRequest request)
	{
		string? body = null;
		Mock<HttpMessageHandler> handler = new();
		handler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
			ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
			.Returns(async (HttpRequestMessage message, CancellationToken ct) =>
			{
				message.Method.Should().Be(HttpMethod.Post);
				body = await message.Content!.ReadAsStringAsync(ct);
				return new HttpResponseMessage(HttpStatusCode.Created)
				{
					Content = new StringContent("{\"data\":{\"transaction\":{\"id\":\"remote-1\"}}}")
				};
			});
		using HttpClient http = new(handler.Object) { BaseAddress = new Uri("https://api.ynab.com/v1/") };
		using MemoryCache cache = new(new MemoryCacheOptions());
		IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
			new Dictionary<string, string?> { ["YNAB_PAT"] = "test-pat" }).Build();
		YnabApiClient client = new(http, cache, configuration, Mock.Of<IYnabRateLimitTracker>(),
			new YnabResponseContext(), Mock.Of<ILogger<YnabApiClient>>());

		YnabCreateTransactionResponse response = await client.CreateTransactionAsync("budget-1", request, CancellationToken.None);
		response.TransactionId.Should().Be("remote-1");
		using JsonDocument document = JsonDocument.Parse(body!);
		return document.RootElement.GetProperty("transaction").Clone();
	}
}

