namespace Domain.Taxonomy;

// Presence of this object is an explicit assignment; a null category clears it.
public sealed record ClassificationIntent(Guid? CategoryId, Guid TaxonomyRevisionId,
	Guid? ExpectedAssignmentId, Guid OperationId, string Reason);
