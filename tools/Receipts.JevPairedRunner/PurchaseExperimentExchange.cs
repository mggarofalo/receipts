using Application.Classification;
using Application.Classification.Experimental;
using Receipts.JevCandidateParity;

namespace Receipts.JevPairedRunner;

// Explicit terminals are supplied before responses; the candidate planner expands their ancestors.
public sealed record PurchaseExperimentInput(CandidatePurchase Purchase, string TaxonomyRevision,
		IReadOnlyList<SuggestionCategory> Taxonomy, IReadOnlyList<string> TerminalIds);
public sealed record PurchaseExperimentReply(string Variant, string Response);

/// <summary>Offline question export and raw-response validation; no transport or assignment.</summary>
public static class PurchaseExperimentExchange
{
	public static object Execute(string command, PurchaseExperimentInput input, PurchaseExperimentReply? reply)
	{
		ArgumentNullException.ThrowIfNull(input);
		if (input.TerminalIds is null || input.TerminalIds.Count == 0 ||
			input.TerminalIds.Any(string.IsNullOrWhiteSpace) ||
			input.TerminalIds.Distinct(StringComparer.Ordinal).Count() != input.TerminalIds.Count ||
			!input.TerminalIds.SequenceEqual(input.TerminalIds.Order(StringComparer.Ordinal)))
		{
			throw new ArgumentException("Explicit sorted unique terminals required.");
		}
		CandidateVerificationPlan plan = SupportedCategoryCandidate.Plan(input.Purchase, input.TaxonomyRevision,
				input.Taxonomy, input.TerminalIds);
		var purchase = PurchaseEvidenceExperiment.PreparePurchaseOnly(plan, CandidateWire.SupportedEvaluationModel);
		var purpose = PurchaseEvidenceExperiment.PrepareUnresolvedPurpose(plan, CandidateWire.SupportedEvaluationModel);
		if (command == "prepare-purchase-experiment" && reply is null)
		{
			return new
			{
				format = "purchase-experiment-exchange-v1",
				paths = plan.Paths,
				purchaseOnly = new { purchase.PlanSha256, purchase.Payload, purchase.QuestionPaths },
				unresolvedPurpose = new { purpose.PlanSha256, purpose.Payload, purpose.QuestionPaths },
				maximumNetworkCalls = 0,
				retries = 0
			};
		}
		if (command != "validate-purchase-experiment" || reply is null)
		{
			throw new ArgumentException("Invalid experiment command or reply.");
		}
		var request = reply.Variant switch
		{
			"purchase-only" => purchase,
			"unresolved-purpose" => purpose,
			_ => throw new ArgumentException("Unknown experiment variant.")
		};
		var result = request.ParseResponse(reply.Response);
		if (result.Failure is not null || result.Answers is null)
		{
			throw new ArgumentException("Invalid experiment response.");
		}
		return new
		{
			validated = true,
			reply.Variant,
			request.PlanSha256,
			result.Answers,
			maximumNetworkCalls = 0,
			retries = 0
		};
	}
}
