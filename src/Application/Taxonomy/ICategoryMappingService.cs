using System.Security.Cryptography;
using System.Text.Json;
using Application.Normalization;
using Domain.Taxonomy;

namespace Application.Taxonomy;

// This approval is independent of product identity and never changes existing purchases.
public sealed record CategoryMappingCommand(NormalizationSourceKey Source, string SourceFingerprint,
	Guid? ExpectedMappingRevisionId, Guid ExpectedTaxonomyRevisionId, Guid? CategoryId,
	Guid OperationId, string Reason);
public sealed record CategoryMappingRevision(Guid Id, Guid? PreviousRevisionId, NormalizationSourceKey Source,
	string SourceFingerprint, Guid? CategoryId, Guid TaxonomyRevisionId, string? SemanticFingerprint,
	string Actor, Guid OperationId, string Reason, DateTimeOffset RecordedAt);
public sealed record CategoryMappingResolution(string Disposition, Guid? CategoryId, Guid TaxonomyRevisionId,
	Guid? MappingRevisionId = null, Guid? ApprovalTaxonomyRevisionId = null);

public sealed record CategoryMappingHistory(IReadOnlyList<CategoryMappingRevision> Items, Guid? NextBeforeRevisionId);

public interface ICategoryMappingService
{
	Task<CategoryMappingHistory> ReadHistoryAsync(NormalizationSourceKey source, Guid? beforeRevisionId, int limit, CancellationToken ct = default);
	// CategoryId null explicitly revokes. The expected mapping revision is null only for the first approval.
	Task<CategoryMappingRevision> ChangeAsync(string actor, CategoryMappingCommand command, CancellationToken ct = default);
	// A non-null explicitChoice includes explicit clearing (CategoryId null), and always takes precedence.
	// Resolution is advisory; consumers must recheck these revisions in their atomic purchase write.
	Task<CategoryMappingResolution> ResolveAsync(NormalizationSourceKey source, string sourceFingerprint,
		CategoryMappingExplicitChoice? explicitChoice = null, CancellationToken ct = default);
}

// Stored as the reason on approved_mapping assignments. Mapping evidence remains immutable and
// retains its original approval taxonomy; the assignment uses the current taxonomy revision.
public sealed record CategoryMappingReuseEvidence(Guid MappingRevisionId, Guid ApprovalTaxonomyRevisionId, string SourceFingerprint);

public sealed record CategoryMappingExplicitChoice(Guid? CategoryId, Guid TaxonomyRevisionId);

public static class CategoryMappingPolicy
{
	// Names are presentation. Stable identity ancestry and definitions describe approved meaning.
	// A missing/archived node or ancestor cannot be selected, including malformed imported trees.
	public static string? SemanticFingerprint(IReadOnlyList<TaxonomyNode> nodes, Guid categoryId)
	{
		Dictionary<Guid, TaxonomyNode> byId = nodes.ToDictionary(node => node.Id);
		HashSet<Guid> visited = [];
		List<(Guid Id, string? Definition)> path = [];
		Guid? current = categoryId;
		while (current is Guid id)
		{
			if (!visited.Add(id) || !byId.TryGetValue(id, out TaxonomyNode? node) || node.Archived) { return null; }
			path.Add((id, node.Definition));
			current = node.ParentId;
		}
		// Use named records, because ValueTuple fields are not serialized by default.
		return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
			path.Select(node => new { node.Id, node.Definition }))));
	}
}
