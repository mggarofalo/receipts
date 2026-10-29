using Application.Taxonomy;

namespace Application.Classification;

public enum OrganizationEligibility
{
	Eligible,
	Unavailable,
	LeaveAsIs,
	Suppressed,
	ManualChoice,
	ExistingAssignment,
	UnknownProvenance
}

/// <summary>
/// Pure purchase preservation rules, not authorization to dispatch or write. A future caller must
/// recheck these facts and all authorization, decision and revision fences in its effect transaction.
/// No automatic writer is registered by this policy.
/// </summary>
public static class OrganizationEffectPolicy
{
	/// <summary>
	/// Only absent assignments and known unresolved legacy observations are eligible. A deliberate
	/// manual null is a choice, not an unresolved observation. Existing automation is preserved here;
	/// correction of still-owned effects requires a separate explicit reconciliation policy.
	/// </summary>
	public static OrganizationEligibility Eligibility(PurchaseClassification? current, bool available,
		bool leaveAsIs, bool suppressed)
	{
		if (!available) { return OrganizationEligibility.Unavailable; }
		if (leaveAsIs) { return OrganizationEligibility.LeaveAsIs; }
		if (suppressed) { return OrganizationEligibility.Suppressed; }
		return current switch
		{
			{ Source: "manual" } => OrganizationEligibility.ManualChoice,
			{ CategoryId: not null } => OrganizationEligibility.ExistingAssignment,
			{ Source: "legacy_import" } => OrganizationEligibility.Eligible,
			{ Source: "legacy_write", LegacyResolution: "unresolved_path" } => OrganizationEligibility.Eligible,
			null => OrganizationEligibility.Eligible,
			_ => OrganizationEligibility.UnknownProvenance
		};
	}

	/// <summary>
	/// A recorded automatic effect may be compensated only while its exact assignment and source
	/// evidence remain current. Category equality never proves ownership. The caller supplies the
	/// recorded effect, checks its scope/provenance, and appends compensation transactionally;
	/// history is never deleted. CategorySelectable refers to the previous category for this call.
	/// </summary>
	public static HistoricalDecision Undo(HistoricalReclassificationItem effect, Guid? appliedAssignmentId,
		Guid expectedTaxonomyRevision, HistoricalCurrentState current)
	{
		if (appliedAssignmentId is null || appliedAssignmentId == Guid.Empty)
		{ return new(HistoricalDecisionKind.Skip, "not-applied"); }
		if (!current.Available) { return new(HistoricalDecisionKind.Skip, "source-unavailable"); }
		if (current.AssignmentId != appliedAssignmentId)
		{ return new(HistoricalDecisionKind.Conflict, "assignment-no-longer-owned"); }
		// Exact raw tuple equality preserves null/empty, whitespace and case differences, even
		// when normalized discovery keys collide. Revisions also fence changes away and back.
		if (current.Group != effect.Group || current.ItemRevision != effect.ItemRevision
			|| current.ReceiptRevision != effect.ReceiptRevision)
		{ return new(HistoricalDecisionKind.Conflict, "source-changed"); }
		if (current.TaxonomyRevisionId != expectedTaxonomyRevision)
		{ return new(HistoricalDecisionKind.Conflict, "taxonomy-changed"); }
		if (effect.PreviousCategoryId.HasValue && !current.CategorySelectable)
		{ return new(HistoricalDecisionKind.Conflict, "previous-category-unavailable"); }
		return new(HistoricalDecisionKind.Append);
	}
}
