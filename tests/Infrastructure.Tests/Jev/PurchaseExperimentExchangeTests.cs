using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Classification.Experimental;
using Receipts.JevCandidateParity;
using Receipts.JevPairedRunner;

namespace Infrastructure.Tests.Jev;

public class PurchaseExperimentExchangeTests
{
	[Fact]
	public async Task CommandsExportExactVariantsAndValidateOnlyTheirBoundResponses()
	{
		string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
		Directory.CreateDirectory(directory);
		try
		{
			string input = Path.Combine(directory, "input.json");
			string reply = Path.Combine(directory, "reply.json");
			string output = Path.Combine(directory, "output.json");
			var fixture = new PurchaseExperimentInput(new("Bread", null, null), "synthetic-v1",
					[new("food", "Food", "Purchased food", null), new("business", "Business food", "Food for business", "food")],
					["business"]);
			await File.WriteAllTextAsync(input, JsonSerializer.Serialize(fixture, CandidateParity.JsonOptions));
			await File.WriteAllTextAsync(reply, "null");
			Assert.Equal(0, await OfflineExchangeCommand.RunAsync(["prepare-purchase-experiment", input, reply, output]));
			JsonNode export = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
			Assert.Equal(0, export["maximumNetworkCalls"]!.GetValue<int>());
			Assert.Equal(0, export["retries"]!.GetValue<int>());
			Assert.Equal(2, export["paths"]!.AsArray().Count);
			Assert.Equal(2, await OfflineExchangeCommand.RunAsync(["prepare-purchase-experiment", input, reply, output]));
			string purchasePayload = export["purchaseOnly"]!["payload"]!.GetValue<string>();
			string purposePayload = export["unresolvedPurpose"]!["payload"]!.GetValue<string>();
			Assert.Null(JsonNode.Parse(purchasePayload)!["questions"]!.AsObject().First().Value!["instructions"]!["path"]);
			Assert.Equal(2, JsonNode.Parse(purposePayload)!["questions"]!.AsObject().Count);
			string response = Response(purposePayload);
			await File.WriteAllTextAsync(reply, JsonSerializer.Serialize(new PurchaseExperimentReply("unresolved-purpose", response), CandidateParity.JsonOptions));
			string validated = Path.Combine(directory, "validated.json");
			Assert.Equal(0, await OfflineExchangeCommand.RunAsync(["validate-purchase-experiment", input, reply, validated]));
			JsonNode parsed = JsonNode.Parse(await File.ReadAllTextAsync(validated))!;
			Assert.True(parsed["validated"]!.GetValue<bool>());
			Assert.Equal(export["unresolvedPurpose"]!["planSha256"]!.GetValue<string>(), parsed["planSha256"]!.GetValue<string>());
			await File.WriteAllTextAsync(reply, JsonSerializer.Serialize(new PurchaseExperimentReply("purchase-only", response), CandidateParity.JsonOptions));
			Assert.Equal(2, await OfflineExchangeCommand.RunAsync(["validate-purchase-experiment", input, reply, Path.Combine(directory, "wrong.json")]));
			await File.WriteAllTextAsync(reply, JsonSerializer.Serialize(new PurchaseExperimentReply("unresolved-purpose", response.Replace("\"answers\":", "\"model\":\"jev-1.14.0\",\"answers\":")), CandidateParity.JsonOptions));
			Assert.Equal(2, await OfflineExchangeCommand.RunAsync(["validate-purchase-experiment", input, reply, Path.Combine(directory, "duplicate.json")]));
			string wrapper = JsonSerializer.Serialize(new PurchaseExperimentReply("unresolved-purpose", response), CandidateParity.JsonOptions);
			await File.WriteAllTextAsync(reply, wrapper.Replace("\"variant\":", "\"variant\":\"purchase-only\",\"variant\":"));
			string duplicateWrapper = Path.Combine(directory, "duplicate-wrapper.json");
			Assert.Equal(2, await OfflineExchangeCommand.RunAsync(["validate-purchase-experiment", input, reply, duplicateWrapper]));
			Assert.False(File.Exists(duplicateWrapper));
			await File.WriteAllTextAsync(reply, wrapper.Replace("\"variant\":", "\"Variant\":\"purchase-only\",\"variant\":"));
			string mixedWrapper = Path.Combine(directory, "mixed-wrapper.json");
			Assert.Equal(2, await OfflineExchangeCommand.RunAsync(["validate-purchase-experiment", input, reply, mixedWrapper]));
			Assert.False(File.Exists(mixedWrapper));
			string inputJson = await File.ReadAllTextAsync(input);
			await File.WriteAllTextAsync(input, inputJson.Replace("\"description\":", "\"description\":\"Rice\",\"description\":"));
			await File.WriteAllTextAsync(reply, "null");
			string duplicateInput = Path.Combine(directory, "duplicate-input.json");
			Assert.Equal(2, await OfflineExchangeCommand.RunAsync(["prepare-purchase-experiment", input, reply, duplicateInput]));
			Assert.False(File.Exists(duplicateInput));
			await File.WriteAllTextAsync(input, inputJson.Replace("\"description\":", "\"Description\":\"Rice\",\"description\":"));
			string mixedInput = Path.Combine(directory, "mixed-input.json");
			Assert.Equal(2, await OfflineExchangeCommand.RunAsync(["prepare-purchase-experiment", input, reply, mixedInput]));
			Assert.False(File.Exists(mixedInput));
			await File.WriteAllTextAsync(input, JsonSerializer.Serialize(fixture with { Purchase = new("Rice", null, null) }, CandidateParity.JsonOptions));
			await File.WriteAllTextAsync(reply, JsonSerializer.Serialize(new PurchaseExperimentReply("unresolved-purpose", response), CandidateParity.JsonOptions));
			Assert.Equal(2, await OfflineExchangeCommand.RunAsync(["validate-purchase-experiment", input, reply, Path.Combine(directory, "drift.json")]));
		}
		finally { Directory.Delete(directory, recursive: true); }
	}

	[Fact]
	public void RequiresExplicitUniqueOrderedKnownTerminals()
	{
		var input = new PurchaseExperimentInput(new("Bread", null, null), "v1",
				[new("a", "Food", "Food", null), new("b", "Bread", "Bread", "a")], ["b"]);
		Assert.Throws<ArgumentException>(() => PurchaseExperimentExchange.Execute("prepare-purchase-experiment", input with { TerminalIds = [] }, null));
		Assert.Throws<ArgumentException>(() => PurchaseExperimentExchange.Execute("prepare-purchase-experiment", input with { TerminalIds = ["b", "b"] }, null));
		Assert.Throws<ArgumentException>(() => PurchaseExperimentExchange.Execute("prepare-purchase-experiment", input with { TerminalIds = ["b", "a"] }, null));
		Assert.Throws<ArgumentException>(() => PurchaseExperimentExchange.Execute("prepare-purchase-experiment", input with { TerminalIds = ["missing"] }, null));
		Assert.Throws<ArgumentException>(() => PurchaseExperimentExchange.Execute("prepare-purchase-experiment", input, new("purchase-only", "{}")));
	}

	private static string Response(string payload)
	{
		var questions = JsonNode.Parse(payload)!["questions"]!.AsObject();
		return JsonSerializer.Serialize(new
		{
			model = CandidateWire.SupportedEvaluationModel,
			answers = questions.ToDictionary(q => q.Key, _ => new { type = "noul", noul = .5 }),
			usage = new { input_tokens = 10, output_tokens = 1 }
		});
	}
}
