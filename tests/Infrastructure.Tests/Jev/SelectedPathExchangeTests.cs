using System.Text.Json;
using Application.Classification.Experimental;
using Receipts.JevCandidateParity;
using Receipts.JevPairedRunner;

namespace Infrastructure.Tests.Jev;

public class SelectedPathExchangeTests
{
    private static PairedInput Input() => new(new CandidatePurchase("Fresh apples", "Shop", "Code"), "v1",
        [new("food", "Food", "Edible goods", null), new("apple", "Apples", "Fresh apples", "food"),
         new("tools", "Tools", "Hand tools", null)]);

    private const string Production = """
        {"model":"jev-1.14.0","answers":{
          "category":{"type":"choice","choice":"food","confidence":0.95,"probabilities":{"food":0.95,"tools":0.05}},
          "branch_0":{"type":"choice","choice":"apple","confidence":0.95,"probabilities":{"food":0.05,"apple":0.95}},
          "fits":{"type":"noul","noul":0.95}},"usage":{"input_tokens":100,"output_tokens":10}}
        """;

    [Fact]
    public async Task ProductionSelectionBindsQuestionAndExactEvidence()
    {
        PairedInput input = Input();
        JsonElement prepared = JsonSerializer.SerializeToElement(await SelectedPathExchange.ExecuteAsync(
            "prepare-selected-path", input, new(Production, null, null)), CandidateParity.JsonOptions);
        Assert.Equal("apple", prepared.GetProperty("categoryId").GetString());
        Assert.Equal(new[] { "food", "apple" }, prepared.GetProperty("path").EnumerateArray().Select(x => x.GetString()));
        string payload = prepared.GetProperty("payload").GetString()!;
        using JsonDocument question = JsonDocument.Parse(payload);
        Assert.Equal(JsonValueKind.Null, question.RootElement.GetProperty("state").GetProperty("purchase").GetProperty("store").ValueKind);
        Assert.Equal(JsonValueKind.Null, question.RootElement.GetProperty("state").GetProperty("purchase").GetProperty("productCode").ValueKind);
        Assert.DoesNotContain("fitProbability", payload, StringComparison.OrdinalIgnoreCase);
        string id = question.RootElement.GetProperty("questions").EnumerateObject().Single().Name;
        string response = JsonSerializer.Serialize(new { model = "jev-1.14.0", answers = new Dictionary<string, object>
            { [id] = new { type = "noul", noul = .95 } }, usage = new { input_tokens = 1, output_tokens = 1 } });
        string binding = prepared.GetProperty("bindingSha256").GetString()!;
        JsonElement result = JsonSerializer.SerializeToElement(await SelectedPathExchange.ExecuteAsync(
            "validate-selected-path", input, new(Production, binding, response)), CandidateParity.JsonOptions);
        Assert.Equal("apple", result.GetProperty("result").GetProperty("categoryId").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => SelectedPathExchange.ExecuteAsync(
            "validate-selected-path", input, new(Production + " ", binding, response)));
        JsonElement changed = JsonSerializer.SerializeToElement(await SelectedPathExchange.ExecuteAsync(
            "prepare-selected-path", input, new(Production + " ", null, null)), CandidateParity.JsonOptions);
        await Assert.ThrowsAsync<ArgumentException>(() => SelectedPathExchange.ExecuteAsync(
            "validate-selected-path", input, new(Production + " ", changed.GetProperty("bindingSha256").GetString(), response)));
        await Assert.ThrowsAsync<ArgumentException>(() => SelectedPathExchange.ExecuteAsync(
            "validate-selected-path", input with { Purchase = input.Purchase with { Description = "Other" } }, new(Production, binding, response)));
        await Assert.ThrowsAsync<ArgumentException>(() => SelectedPathExchange.ExecuteAsync(
            "validate-selected-path", input, new(Production, binding, response.Replace(id, "wrong"))));
    }

    [Fact]
    public async Task ProductionAbstentionNeverCreatesFollowup()
    {
        string abstention = Production.Replace("0.95}},\"usage\"", "0.2}},\"usage\"");
        JsonElement result = JsonSerializer.SerializeToElement(await SelectedPathExchange.ExecuteAsync(
            "prepare-selected-path", Input(), new(abstention, null, null)), CandidateParity.JsonOptions);
        Assert.Equal("baseline-abstained", result.GetProperty("status").GetString());
        Assert.False(result.TryGetProperty("payload", out _));
        await Assert.ThrowsAsync<ArgumentException>(() => SelectedPathExchange.ExecuteAsync(
            "validate-selected-path", Input(), new(abstention, "anything", "{}")));
    }
}
