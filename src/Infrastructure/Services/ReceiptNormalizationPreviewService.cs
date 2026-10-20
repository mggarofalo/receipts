using Application.Normalization;
using Domain.NormalizedDescriptions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>Local advisory lookup only: no entity creation, inference, or verification side effects.</summary>
public sealed class ReceiptNormalizationPreviewService(IDbContextFactory<ApplicationDbContext> factory) : IReceiptNormalizationPreview
{
	public async Task<ReceiptNormalizationPreview> PreviewAsync(NormalizationSourceKey source, string? query,
		int offset, int limit, CancellationToken cancellationToken)
	{
		if (offset < 0 || limit is < 1 or > 50 || query?.Length > 200)
		{ throw new NormalizationCatalogException(400, "invalid_preview_query", "Use a nonnegative offset, limit between 1 and 50, and a search phrase up to 200 characters."); }
		NormalizationSourceKey key = NormalizationCatalogPolicy.Normalize(source);
		string hash = NormalizationCatalogPolicy.Hash(key);
		await using ApplicationDbContext context = await factory.CreateDbContextAsync(cancellationToken);
		var mapping = await context.NormalizationSourceMappings.AsNoTracking().Where(value => value.SourceHash == hash && value.State == "active")
			.Select(value => new { value.Id, value.Revision, value.TargetId, value.Exclude }).SingleOrDefaultAsync(cancellationToken);
		var target = mapping?.TargetId is Guid targetId
			? await context.NormalizedDescriptions.IgnoreAutoIncludes().AsNoTracking().Where(value => value.Id == targetId && value.Status == NormalizedDescriptionStatus.Active)
				.Select(value => new ReceiptNormalizationCandidate(value.Id, value.NormalizationRevision, value.DisplayLabel ?? value.CanonicalName)).SingleOrDefaultAsync(cancellationToken)
			: null;
		if (mapping is not null && !mapping.Exclude && target is null) { mapping = null; }
		var candidates = context.NormalizedDescriptions.IgnoreAutoIncludes().AsNoTracking().Where(value => value.Status == NormalizedDescriptionStatus.Active);
		if (!string.IsNullOrWhiteSpace(query))
		{
			string pattern = "%" + query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
			candidates = candidates.Where(value => EF.Functions.ILike(value.CanonicalName, pattern, "\\")
				|| value.DisplayLabel != null && EF.Functions.ILike(value.DisplayLabel, pattern, "\\"));
		}
		int total = await candidates.CountAsync(cancellationToken);
		var page = await candidates.OrderBy(value => value.DisplayLabel ?? value.CanonicalName).ThenBy(value => value.Id).Skip(offset).Take(limit)
			.Select(value => new ReceiptNormalizationCandidate(value.Id, value.NormalizationRevision, value.DisplayLabel ?? value.CanonicalName)).ToListAsync(cancellationToken);
		return new(hash, mapping?.Id, mapping?.Revision, target?.Id, target?.Revision, target?.Name, mapping?.Exclude == true, page, total, offset, limit);
	}
}
