using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.Classification;
using Infrastructure.Jev;

namespace Receipts.JevProductionParity;

public sealed record ProductionParityFixture(string Id, CategorySuggestionRequest Request, CategoryHierarchyJudgment Judgment);

public sealed record ProductionParityRecord(string FixtureId, string QuestionPolicy, string Model,
	string QuestionSha256, string DecisionSha256, string RequestJson, CategorySuggestionResult Decision);

/// <summary>Offline question/decision parity only: no provider, credentials, transport, or response parsing.</summary>
public static class ProductionParity
{
	public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

	public static ProductionParityRecord Evaluate(ProductionParityFixture fixture)
	{
		ArgumentNullException.ThrowIfNull(fixture);
		if (string.IsNullOrWhiteSpace(fixture.Id) || !CategorySuggestionPolicy.IsValid(fixture.Request))
		{
			throw new ArgumentException("A fixture id and a valid production request are required.", nameof(fixture));
		}

		// Keep production taxonomy order: it controls branch_N identities and exact request bytes.
		CategorySuggestionRequest request = fixture.Request with { Categories = fixture.Request.Categories.ToArray() };
		byte[] payload = JevSuggestionQuestions.Serialize(request);
		if (payload.Length > CategorySuggestionPolicy.MaximumPayloadBytes)
		{
			throw new ArgumentException("Fixture exceeds the production payload limit.", nameof(fixture));
		}

		CategorySuggestionResult decision = CategorySuggestionPolicy.Decide(request, fixture.Judgment, "jev");
		return new(fixture.Id, CategorySuggestionPolicy.Version, CategorySuggestionPolicy.Model,
			Hash(payload), Hash(JsonSerializer.SerializeToUtf8Bytes(decision, JsonOptions)),
			Encoding.UTF8.GetString(payload), decision);
	}

	private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

	private static JsonSerializerOptions CreateJsonOptions()
	{
		JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
		options.Converters.Add(new JsonStringEnumConverter());
		options.MakeReadOnly(populateMissingResolver: true);
		return options;
	}
}
