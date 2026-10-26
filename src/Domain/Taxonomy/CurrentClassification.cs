namespace Domain.Taxonomy;

/// <summary>The current assignment, pinned to its recorded taxonomy revision.</summary>
public sealed record CurrentClassification(Guid AssignmentId, Guid? CategoryId,
	Guid TaxonomyRevisionId, IReadOnlyList<TaxonomyNode> RecordedPath);
