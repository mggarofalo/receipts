using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Classification;
using Application.Taxonomy;

namespace Infrastructure.Services;

// INACTIVE semantic evidence only. Neither construction, hashing nor a matching result
// authenticates these caller-provided facts. A future PostgreSQL transaction must derive
// and durably seal them from authoritative rows, then recheck before any conditional write.
internal static class OrganizationEffectFenceContractV1
{
	internal sealed record CaptureEvidence(int Version, Guid ManifestId, string ManifestRequestHash,
		Guid SettingsRevision, Guid RestoreGeneration);
	internal sealed record ExecutionEvidence(int Version, string CaptureEvidenceHash,
		string ItemRequestHash, string PhaseCommandHash);

	// Settings is a projection of category_assistance.Settings, not organization.Runs.
	// Revision changes on every settings update; RestoreGeneration changes on restore.
	internal sealed record SettingsState(CategoryAssistanceSettings Settings, Guid RestoreGeneration);
	internal sealed record CurrentState(SettingsState Policy, OrganizationEffectContractV1.Source Source,
		PurchaseClassification? Assignment, Guid TaxonomyRevisionId, Guid MappingRevisionId,
		bool Available, bool LeaveAsIs, bool Suppressed, bool CategorySelectable);

	internal static string WriteCapture(CaptureEvidence evidence)
	{
		if (evidence.Version != 1 || evidence.ManifestId == Guid.Empty
			|| evidence.SettingsRevision == Guid.Empty || evidence.RestoreGeneration == Guid.Empty)
		{ throw new InvalidDataException("Invalid effect capture fence evidence."); }
		Hash(evidence.ManifestRequestHash);
		return Canonical(evidence);
	}

	internal static string CaptureHash(CaptureEvidence evidence) =>
		Digest("receipts.organization-effect.capture-fences.v1", WriteCapture(evidence));

	internal static string ExecutionHash(ExecutionEvidence evidence)
	{
		if (evidence.Version != 1) { throw new InvalidDataException("Unknown effect execution fence version."); }
		Hash(evidence.CaptureEvidenceHash); Hash(evidence.ItemRequestHash); Hash(evidence.PhaseCommandHash);
		return Digest("receipts.organization-effect.execution-fences.v1", Canonical(evidence));
	}

	// Null means only that the supplied semantic facts match. It is never execution authority.
	// The caller must separately authenticate ownership/undo lineage, actor permissions,
	// mapping authority, complete capture membership and the applied-effect barrier.
	internal static string? Mismatch(CaptureEvidence capture, ExecutionEvidence execution,
		OrganizationEffectContractV1.ManifestRequest manifest, OrganizationEffectContractV1.ItemRequest item,
		OrganizationEffectContractV1.PhaseCommand phase, SettingsState capturedPolicy, CurrentState current)
	{
		_ = ExecutionHash(execution);
		string captureHash = CaptureHash(capture);
		string itemHash = OrganizationEffectContractV1.RequestHash(item);
		string manifestHash = OrganizationEffectContractV1.ManifestRequestHash(manifest);
		string phaseHash = OrganizationEffectContractV1.PhaseHash(phase);
		if (capture.ManifestId != manifest.ManifestId || capture.ManifestRequestHash != manifestHash
			|| execution.CaptureEvidenceHash != captureHash || execution.ItemRequestHash != itemHash
			|| execution.PhaseCommandHash != phaseHash || phase.ItemRequestHash != itemHash
			|| !manifest.Members.Any(member => OrganizationEffectContractV1.RequestHash(member) == itemHash))
		{ return "evidence-binding-changed"; }
		if (!Valid(capturedPolicy) || !Valid(current.Policy)) { return "invalid-settings"; }
		if (!capturedPolicy.Settings.Enabled || capture.SettingsRevision != capturedPolicy.Settings.Revision
			|| capture.RestoreGeneration != capturedPolicy.RestoreGeneration)
		{ return "capture-policy-mismatch"; }
		if (capture.RestoreGeneration != current.Policy.RestoreGeneration) { return "restore-generation-changed"; }
		if (!current.Available) { return "source-unavailable"; }
		// Record equality compares the exact raw tuple and both durable business revisions.
		if (item.Source != current.Source) { return "source-changed"; }
		if (current.Assignment?.ReceiptItemId is Guid assignmentItem && assignmentItem != item.Source.ItemId)
		{ return "assignment-item-mismatch"; }
		if (current.Assignment?.Id != phase.ExpectedAssignmentId) { return "assignment-changed"; }
		if (current.TaxonomyRevisionId != phase.Target.TaxonomyRevisionId) { return "taxonomy-changed"; }
		if (phase.Target.CategoryId.HasValue && !current.CategorySelectable) { return "category-unavailable"; }
		if (phase.Phase == "apply")
		{
			if (phase.OperationId != item.ApplyOperationId || phase.ExpectedAssignmentId != item.ExpectedAssignmentId
				|| phase.Target.CategoryId != item.Target.CategoryId
				|| phase.Target.TaxonomyRevisionId != item.Target.TaxonomyRevisionId
				|| !phase.Target.Path.SequenceEqual(item.Target.Path))
			{ return "apply-intent-changed"; }
			if (!current.Policy.Settings.Enabled) { return "disabled"; }
			if (capture.SettingsRevision != current.Policy.Settings.Revision) { return "settings-revision-changed"; }
			if (current.MappingRevisionId != item.Decision.MappingRevisionId) { return "mapping-revision-changed"; }
			if (OrganizationEffectPolicy.Eligibility(current.Assignment, current.Available,
				current.LeaveAsIs, current.Suppressed) != OrganizationEligibility.Eligible)
			{ return "purchase-protected"; }
		}
		else if (phase.OperationId != item.UndoOperationId) { return "undo-operation-changed"; }
		// Undo does not require enabled consent, a provider or current mapping approval.
		// It still requires authenticated applied-assignment and prior-category lineage,
		// which this pure fence contract cannot establish and deliberately does not claim.
		return null;
	}

	private static bool Valid(SettingsState state) => state.Settings.Revision != Guid.Empty
		&& state.RestoreGeneration != Guid.Empty && CategoryAssistancePolicy.IsValid(state.Settings);
	private static string Canonical<T>(T evidence) => PortableTaxonomyArchive.CanonicalEffectDecision(JsonSerializer.Serialize(evidence));
	private static string Digest(string domain, string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(domain + "\0" + value)));
	private static void Hash(string value)
	{
		if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
		{ throw new InvalidDataException("Expected uppercase SHA-256 value."); }
	}
}
