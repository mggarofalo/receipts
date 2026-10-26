using Domain.Taxonomy;

namespace Application.Taxonomy;

public sealed record TaxonomyChangeCommand(Guid ExpectedRevisionId, Guid OperationId, string Reason, IReadOnlyList<TaxonomyEdit> Changes);
public sealed record TaxonomyRevision(Guid Id, long Sequence, Guid? PreviousRevisionId, DateTimeOffset RecordedAt,
	string Actor, string Reason, Guid OperationId, IReadOnlyList<TaxonomyNode> Nodes, int AffectedCount);
public sealed class TaxonomyException(int status, string message, IDictionary<string, object?>? extensions = null) : Exception(message)
{
	public int Status { get; } = status;
	public IDictionary<string, object?> Extensions { get; } = extensions ?? new Dictionary<string, object?>();
}
public interface ITaxonomyService
{
	Task<TaxonomyRevision?> ReadAsync(Guid? revisionId, CancellationToken cancellationToken = default);
	Task<TaxonomyRevision> ChangeAsync(string actor, TaxonomyChangeCommand command, CancellationToken cancellationToken = default);
}
