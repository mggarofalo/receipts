namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	// Full candidate archives must pass ordinary authority validation as well as structural
	// effect closure. A sealed member set and a matching assignment do not authorize a write.
	internal static void ValidateEffectAssignmentAuthorityCandidate(State history, EffectEvidence effects)
	{
		Validate(history);
		ValidateEffectEvidence(history, effects);
		Require(!effects.Outcomes.Any(o => o.Disposition == "applied"),
			"Applied effect assignment authority requires a versioned request and decision contract.");

		// Until that contract defines its own operation namespace, no effect reservation may
		// borrow ordinary operation evidence, including under a different actor.
		HashSet<Guid> ordinaryOperations = [.. history.Operations.Select(o => o.OperationId),
			.. history.ClassificationOperations.Select(o => o.OperationId),
			.. history.Migrations.Select(o => o.OperationId)];
		Require(effects.Items.All(i => !ordinaryOperations.Contains(i.ApplyOperationId)
			&& !ordinaryOperations.Contains(i.UndoOperationId)),
			"Effect reservation collides with ordinary assignment or taxonomy authority.");
	}
}
