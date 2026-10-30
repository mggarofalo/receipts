using System.Text;
using System.Text.Json;

namespace Receipts.JevProductionParity;

/// <summary>Round-4 plan, replay and dry-run reports. Never writes request bodies or identifiers to the console.</summary>
public static class TwoStageCommand
{
	/// <summary>Budget and size numbers shared by the plan and the dry run. No request content.</summary>
	public static object Numbers(TwoStagePacket packet)
	{
		ArgumentNullException.ThrowIfNull(packet);
		TwoStagePolicy policy = packet.Policy;
		return new
		{
			packetKind = TwoStagePolicy.PacketKind,
			questionPolicy = TwoStageQuestions.QuestionPolicy,
			model = TwoStageQuestions.Model,
			totalCases = packet.Cases.Count,
			maximumAttempts = policy.MaximumAttempts,
			approvedMaximumAttempts = policy.ApprovedMaximumAttempts,
			stage1Requests = packet.Stage1Payloads.Count,
			stage1Bytes = Range(packet.Stage1Payloads.Select(p => p.Length)),
			maximumStage2Bytes = Range(packet.MaximumStage2Bytes),
			reservationBasis = TwoStagePolicy.ReservationBasis,
			nanodollarsPerInputToken = TwoStagePolicy.NanodollarsPerInputToken,
			headroomInputTokens = TwoStagePolicy.HeadroomInputTokens,
			maximumReservationTokens = packet.MaximumReservationTokens,
			maximumReservationNanodollars = packet.MaximumReservationNanodollars,
			approvedReservationCapNanodollars = policy.ApprovedReservationCapNanodollars,
			priorCarryNanodollars = policy.PriorCarryNanodollars,
			combinedMaximumReservationNanodollars = packet.CombinedMaximumReservationNanodollars,
			ceilingNanodollars = policy.CeilingNanodollars,
			fixedReservationComparisonNanodollars = policy.MaximumAttempts * EvaluationPolicy.PerAttemptNanodollars,
			pricingVerified = false,
			liveAuthorized = false
		};
	}

	public static object Plan(TwoStagePacket packet)
	{
		ArgumentNullException.ThrowIfNull(packet);
		return new
		{
			mode = "offline-only-no-dispatch",
			numbers = Numbers(packet),
			cases = packet.Cases.Select((c, i) => new
			{
				c.Id,
				requestSha256 = OfflinePairedEvaluation.Hash(packet.Stage1Payloads[i]),
				requestJson = Encoding.UTF8.GetString(packet.Stage1Payloads[i]),
				maximumStage2Bytes = packet.MaximumStage2Bytes[i]
			}).ToArray()
		};
	}

	/// <summary>Journal identity: the frozen packet, the exact approval, and this tool's build. The credential is bound separately.</summary>
	public static string Identity(string freezeHash, string approvalHash) => freezeHash + ":" + approvalHash + ":" +
		OfflinePairedEvaluation.Hash(File.ReadAllBytes(typeof(TwoStageCommand).Assembly.Location));

	public static string? ApprovalProblem(LiveApproval? approval, TwoStagePacket packet, string freezeHash, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(packet);
		return LiveCommand.ApprovalProblem(approval, freezeHash, packet.Policy.PriorCarryNanodollars, packet.Policy.CeilingNanodollars,
			packet.MaximumReservationNanodollars, now);
	}

	/// <summary>Reports readiness without request bodies, descriptions, identifiers or the credential.</summary>
	public static async Task WriteDryRunAsync(string outputPath, TwoStagePacket packet, string freezeHash, string approvalPath,
		string journalPath, string? key)
	{
		(string? approvalHash, string approvalStatus) = LiveCommand.ReadApprovalStatus(approvalPath,
			approval => ApprovalProblem(approval, packet, freezeHash, DateTimeOffset.UtcNow));
		(string journalStatus, int completedExchanges, bool? identityMatches) = LiveCommand.ReadJournalStatus(journalPath,
			approvalHash is not null && key is not null ? LiveCommand.BindCredential(Identity(freezeHash, approvalHash), key) : null);
		object report = new
		{
			mode = "dry-run-no-dispatch",
			numbers = Numbers(packet),
			credentialPresent = key is not null,
			approval = new { present = approvalHash is not null, valid = approvalStatus == "valid", status = approvalStatus },
			journal = new { status = journalStatus, completedExchanges, identityMatches },
			stage1RequestSha256s = packet.Stage1Payloads.Select(OfflinePairedEvaluation.Hash).ToArray()
		};
		using FileStream output = new(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		await JsonSerializer.SerializeAsync(output, report, ProductionParity.JsonOptions);
		output.Flush(flushToDisk: true);
	}

	private static object Range(IEnumerable<int> values)
	{
		int[] all = [.. values];
		return new { minimum = all.Min(), maximum = all.Max(), total = all.Sum(v => (long)v) };
	}
}
