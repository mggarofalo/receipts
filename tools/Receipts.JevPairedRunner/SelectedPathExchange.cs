using System.Security.Cryptography;
using System.Text.Json;
using Application.Classification;
using Application.Classification.Experimental;
using Receipts.JevCandidateParity;

namespace Receipts.JevPairedRunner;

// A response is accompanied by the binding exported before it was requested.
public sealed record SelectedPathReply(string Production, string? BindingSha256, string? Response);

/// <summary>Offline production replay and independent selected-path diagnostic.</summary>
public static class SelectedPathExchange
{
    public static async Task<object> ExecuteAsync(string command, PairedInput input, SelectedPathReply reply)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Purchase);
        ArgumentNullException.ThrowIfNull(reply);
        if (string.IsNullOrEmpty(reply.Production)) throw new ArgumentException();

        // Replay exactly the original production plan; store and code were never production evidence.
        PairedReplayPlan baselinePlan = PairedReplay.Prepare(input);
        CategorySuggestionResult baseline = await baselinePlan.ParseProductionAsync(reply.Production);
        if (baseline.Failure is not null || baseline.Suggestion is null) throw new ArgumentException();
        CategorySuggestion suggestion = baseline.Suggestion;
        if (suggestion.CategoryId is null)
        {
            if (command != "prepare-selected-path" || reply.BindingSha256 is not null || reply.Response is not null)
                throw new ArgumentException();
            return new { status = "baseline-abstained", suggestion.FitProbability, suggestion.AbstentionReason,
                maximumNetworkCalls = 0, retries = 0 };
        }

        // Preregistered controlled comparison: both arms receive description only.
        CandidatePurchase purchase = input.Purchase with { Store = null, ProductCode = null };
        // The question ID itself commits to the exact baseline bytes and original input, so a
        // response from another baseline cannot be accepted even when its selected path matches.
        string evidenceBinding = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "selected-path-evidence-v1", input, baselinePlan.ProductionPayload,
            productionResponse = reply.Production
        }, CandidateParity.JsonOptions)));
        CandidateVerificationPlan plan = SupportedCategoryCandidate.Plan(purchase, "selected-" + evidenceBinding,
            input.Taxonomy, [suggestion.CategoryId]);
        var selected = PurchaseEvidenceExperiment.PrepareSelectedPath(plan, suggestion.CategoryId,
            suggestion.FitProbability, CandidateWire.SupportedEvaluationModel);
        string binding = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "selected-path-exchange-v1", input, baselinePlan.ProductionPayload,
            productionResponse = reply.Production, selected.PlanSha256, selected.Payload
        }, CandidateParity.JsonOptions)));
        if (command == "prepare-selected-path" && reply.BindingSha256 is null && reply.Response is null)
        {
            return new { status = "ready", bindingSha256 = binding, selected.PlanSha256, selected.Payload,
                selected.QuestionPaths, suggestion.CategoryId, suggestion.Path, suggestion.FitProbability,
                maximumNetworkCalls = 0, retries = 0 };
        }
        if (command != "validate-selected-path" || reply.BindingSha256 != binding || reply.Response is null)
            throw new ArgumentException();
        SelectedPathExperimentResult result = selected.ParseResponse(reply.Response);
        if (result.Failure is not null) throw new ArgumentException();
        return new { validated = true, bindingSha256 = binding, result, maximumNetworkCalls = 0, retries = 0 };
    }
}
