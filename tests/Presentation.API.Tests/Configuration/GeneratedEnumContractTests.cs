using System.Text.Json;
using System.Text.Json.Serialization;
using API.Configuration;
using API.Generated.Dtos;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Presentation.API.Tests.Configuration;

public class GeneratedEnumContractTests
{
	public static TheoryData<bool, string, int, string> ResponseCases()
	{
		TheoryData<bool, string, int, string> data = [];
		// Literal expectations are the OpenAPI contract, independent of C# member naming.
		(string Family, string[] Values)[] families =
		[
			("similar", ["template", "history"]),
			("normalized", ["active", "pendingReview", "rejected"]),
			("report", ["active", "pendingReview", "rejected"]),
			("balance", ["noTransactions", "balanced", "outOfBalance"]),
			("suggestion", ["location", "global"]),
			("receiptSync", ["notSynced", "pending", "synced", "failed"]),
		];
		foreach (bool http in new[] { false, true })
		{
			foreach ((string family, string[] values) in families)
			{
				for (int value = 0; value < values.Length; value++)
				{
					data.Add(http, family, value, values[value]);
				}
			}
		}
		return data;
	}

	[Theory]
	[MemberData(nameof(ResponseCases))]
	public void GeneratedResponseProperty_UsesDocumentedLiteral(bool http, string family, int value, string expected)
	{
		using ServiceProvider provider = Services();
		(object dto, string property) = family switch
		{
			"similar" => ((object)new SimilarItemResponse { Source = (SimilarItemResponseSource)value }, "source"),
			"normalized" => ((object)new NormalizedDescriptionResponse { Status = (NormalizedDescriptionStatus)value }, "status"),
			"report" => ((object)new SpendingByNormalizedDescriptionItem { Status = (NormalizedDescriptionStatus)value }, "status"),
			"balance" => ((object)new ReceiptListItemResponse { BalanceState = (ReceiptListItemResponseBalanceState)value }, "balanceState"),
			"suggestion" => ((object)new ReceiptItemSuggestionResponse { MatchType = (ReceiptItemSuggestionResponseMatchType)value }, "matchType"),
			"receiptSync" => ((object)new ReceiptYnabSyncStatus { SyncStatus = (ReceiptYnabSyncStatusValue)value }, "syncStatus"),
			_ => throw new ArgumentOutOfRangeException(nameof(family)),
		};
		using JsonDocument body = Serialize(dto, Options(provider, http));
		body.RootElement.GetProperty(property).GetString().Should().Be(expected);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void AlreadyCorrectResponseEnums_AndNullableStatus_PreserveTheirContracts(bool http)
	{
		using ServiceProvider provider = Services();
		JsonSerializerOptions options = Options(provider, http);
		string[] outcomes = ["synced", "alreadySynced", "noMatch", "ambiguous", "currencySkipped", "reconciledSkipped", "failed"];
		for (int value = 0; value < outcomes.Length; value++)
		{
			using JsonDocument body = Serialize(new YnabMemoSyncResultItem { Outcome = (YnabMemoSyncOutcome)value }, options);
			body.RootElement.GetProperty("outcome").GetString().Should().Be(outcomes[value]);
		}
		string[] statuses = ["pending", "synced", "failed"];
		for (int value = 0; value < statuses.Length; value++)
		{
			using JsonDocument body = Serialize(new YnabSyncRecordResponse { SyncStatus = (YnabSyncRecordResponseSyncStatus)value }, options);
			body.RootElement.GetProperty("syncStatus").GetString().Should().Be(statuses[value]);
		}
		string[] types = ["memoUpdate", "transactionPush"];
		for (int value = 0; value < types.Length; value++)
		{
			using JsonDocument body = Serialize(new YnabSyncRecordResponse { SyncType = (YnabSyncRecordResponseSyncType)value }, options);
			body.RootElement.GetProperty("syncType").GetString().Should().Be(types[value]);
		}
		string[] errors = ["invalid_request", "invalid_client", "invalid_grant", "unauthorized_client", "unsupported_grant_type", "invalid_scope"];
		for (int value = 0; value < errors.Length; value++)
		{
			using JsonDocument body = Serialize(new OAuthErrorResponse { Error = (OAuthErrorResponseError)value }, options);
			body.RootElement.GetProperty("error").GetString().Should().Be(errors[value]);
		}
		using JsonDocument report = Serialize(new SpendingByNormalizedDescriptionItem { Status = null }, options);
		report.RootElement.GetProperty("status").ValueKind.Should().Be(JsonValueKind.Null);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RequestEnums_PreserveCaseInsensitiveAndNullableCompatibility(bool http)
	{
		using ServiceProvider provider = Services();
		JsonSerializerOptions options = Options(provider, http);
		foreach (string value in new[] { "\"pendingReview\"", "\"PendingReview\"", "1" })
		{
			JsonSerializer.Deserialize<UpdateNormalizedDescriptionStatusRequest>("{\"status\":" + value + "}", options)!
				.Status.Should().Be(NormalizedDescriptionStatus.PendingReview);
		}
		foreach (string value in new[] { "refreshToken", "RefreshToken" })
		{
			string body = "{\"token\":\"opaque\",\"tokenTypeHint\":\"" + value + "\"}";
			JsonSerializer.Deserialize<TokenIntrospectionRequest>(body, options)!.TokenTypeHint.Should().Be(TokenIntrospectionRequestTokenTypeHint.RefreshToken);
			JsonSerializer.Deserialize<TokenRevocationRequest>(body, options)!.TokenTypeHint.Should().Be(TokenRevocationRequestTokenTypeHint.RefreshToken);
		}
		foreach (string body in new[] { "{\"token\":\"opaque\"}", "{\"token\":\"opaque\",\"tokenTypeHint\":null}" })
		{
			JsonSerializer.Deserialize<TokenIntrospectionRequest>(body, options)!.TokenTypeHint.Should().BeNull();
			JsonSerializer.Deserialize<TokenRevocationRequest>(body, options)!.TokenTypeHint.Should().BeNull();
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void NonGeneratedExplicitConverters_AndOrdinaryStrings_AreNotRewritten(bool http)
	{
		using ServiceProvider provider = Services();
		using JsonDocument body = Serialize(new CustomContract(), Options(provider, http));
		body.RootElement.GetProperty("pascal").GetString().Should().Be("PendingReview");
		body.RootElement.GetProperty("custom").GetString().Should().Be("special-value");
		body.RootElement.GetProperty("label").GetString().Should().Be("PendingReview");
	}

	private static ServiceProvider Services()
	{
		ServiceCollection services = new();
		services.AddApplicationServices(new ConfigurationBuilder().Build());
		return services.BuildServiceProvider();
	}

	private static JsonSerializerOptions Options(IServiceProvider services, bool http) => http
		? services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions
		: services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;

	private static JsonDocument Serialize(object value, JsonSerializerOptions options) =>
		JsonDocument.Parse(JsonSerializer.Serialize(value, value.GetType(), options));

	public enum ContractValue { PendingReview }

	public sealed class CustomContract
	{
		[JsonConverter(typeof(JsonStringEnumConverter<ContractValue>))]
		public ContractValue Pascal { get; set; }
		[JsonConverter(typeof(SpecialConverter))]
		public ContractValue Custom { get; set; }
		public string Label { get; set; } = "PendingReview";
	}

	public sealed class SpecialConverter : JsonConverter<ContractValue>
	{
		public override ContractValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
		public override void Write(Utf8JsonWriter writer, ContractValue value, JsonSerializerOptions options) => writer.WriteStringValue("special-value");
	}
}
