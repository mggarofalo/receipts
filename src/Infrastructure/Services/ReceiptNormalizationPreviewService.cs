using System.Text.RegularExpressions;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
		var active = context.NormalizedDescriptions.IgnoreAutoIncludes().AsNoTracking().Where(value => value.Status == NormalizedDescriptionStatus.Active);
		int total;
		List<ReceiptNormalizationCandidate> page;
		if (string.IsNullOrWhiteSpace(query))
		{
			total = await active.CountAsync(cancellationToken);
			page = await active.OrderBy(value => value.DisplayLabel ?? value.CanonicalName).ThenBy(value => value.Id).Skip(offset).Take(limit)
				.Select(value => new ReceiptNormalizationCandidate(value.Id, value.NormalizationRevision, value.DisplayLabel ?? value.CanonicalName)).ToListAsync(cancellationToken);
		}
		else
		{
			// The chooser's own lexical relevance (RECEIPTS-1094, RECEIPTS-1100): a name holding more of the
			// typed words outranks one that only contains the phrase, with name then Id as a stable tie-break.
			var matches = Matches(context, query.Trim());
			total = await matches.CountAsync(cancellationToken);
			Guid[] ids = await matches.OrderByDescending(match => match.Score).ThenByDescending(match => match.Similarity)
				.ThenBy(match => match.CanonicalName).ThenBy(match => match.Id).Skip(offset).Take(limit)
				.Select(match => match.Id).ToArrayAsync(cancellationToken);
			var rows = await active.Where(value => ids.Contains(value.Id))
				.Select(value => new ReceiptNormalizationCandidate(value.Id, value.NormalizationRevision, value.DisplayLabel ?? value.CanonicalName))
				.ToDictionaryAsync(value => value.Id, cancellationToken);
			page = [.. ids.Where(rows.ContainsKey).Select(id => rows[id])];
		}
		Guid?[] sourceTargets = await NormalizationCatalogService.SourceItems(context, key)
			.Where(item => item.NormalizedDescriptionId != null).Select(item => item.NormalizedDescriptionId)
			.Distinct().Take(2).ToArrayAsync(cancellationToken);
		if (sourceTargets.Length > 1) { mapping = null; target = null; }
		return new(hash, mapping?.Id, mapping?.Revision, target?.Id, target?.Revision, target?.Name, mapping?.Exclude == true, page, total, offset, limit, sourceTargets.Length > 1);
	}

	/// <summary>Active names containing the phrase, literally and in any case, with their relevance to it.</summary>
	private static IQueryable<PreviewMatch> Matches(ApplicationDbContext context, string phrase)
	{
		List<NpgsqlParameter> parameters = [];
		string Parameter(object value)
		{
			parameters.Add(new NpgsqlParameter($"p{parameters.Count}", value));
			return $"@p{parameters.Count - 1}";
		}
		(string score, string similarity) = NormalizationCandidateRetrieval.Relevance(
			DescriptionText.LexicalTokens(phrase, DescriptionTokenMode.Catalog), phrase, Parameter);
		string pattern = Parameter("%" + Regex.Replace(phrase, @"[\\%_]", @"\$0") + "%");
		// Only parameter placeholders are interpolated; every value is bound.
		string sql = $"""
			SELECT "Id", "CanonicalName", {score} AS "Score", {similarity} AS "Similarity"
			FROM matching."NormalizedDescriptions"
			WHERE "Status" = 'Active' AND ("CanonicalName" ILIKE {pattern} ESCAPE '\' OR "DisplayLabel" ILIKE {pattern} ESCAPE '\')
			""";
		return context.Database.SqlQueryRaw<PreviewMatch>(sql, [.. parameters]);
	}

	private sealed class PreviewMatch
	{
		public Guid Id { get; set; }
		public string CanonicalName { get; set; } = string.Empty;
		public int Score { get; set; }
		public double Similarity { get; set; }
	}
}
