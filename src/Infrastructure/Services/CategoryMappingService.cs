using System.Security.Cryptography;
using System.Text.Json;
using Application.Normalization;
using Application.Taxonomy;
using Domain.Taxonomy;
using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public sealed class CategoryMappingService(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock) : ICategoryMappingService
{
	public async Task<CategoryMappingHistory> ReadHistoryAsync(NormalizationSourceKey source, Guid? beforeRevisionId, int limit, CancellationToken ct = default)
	{
		if (limit is < 1 or > 100 || beforeRevisionId == Guid.Empty)
		{ throw new TaxonomyException(400, "History limit must be between 1 and 100 and cursor must be a valid revision."); }
		source = NormalizationCatalogPolicy.Normalize(source);
		string fingerprint = NormalizationCatalogPolicy.Hash(source);
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		var rows = db.CategoryMappingRevisions.AsNoTracking().Where(row => row.SourceFingerprint == fingerprint
			&& row.Location == source.Location && row.Description == source.Description && row.ItemCode == source.ItemCode);
		Guid? next;
		if (beforeRevisionId is Guid cursor)
		{
			CategoryMappingRevisionEntity? before = await rows.SingleOrDefaultAsync(row => row.Id == cursor, ct);
			if (before == null) { throw new TaxonomyException(400, "History cursor does not belong to this exact source."); }
			next = before.PreviousRevisionId;
		}
		else { next = await db.CategoryMappingHeads.Where(row => row.SourceFingerprint == fingerprint).Select(row => (Guid?)row.RevisionId).SingleOrDefaultAsync(ct); }
		List<CategoryMappingRevision> result = [];
		while (next is Guid id && result.Count < limit)
		{
			CategoryMappingRevisionEntity? row = await rows.SingleOrDefaultAsync(row => row.Id == id, ct);
			if (row == null) { break; }
			result.Add(Read(row)); next = row.PreviousRevisionId;
		}
		return new(result, next == null || result.Count == 0 ? null : result[^1].Id);
	}

	public async Task<CategoryMappingRevision> ChangeAsync(string actor, CategoryMappingCommand command, CancellationToken ct = default)
	{
		NormalizationSourceKey source = NormalizationCatalogPolicy.Normalize(command.Source);
		string fingerprint = NormalizationCatalogPolicy.Hash(source);
		if (string.IsNullOrWhiteSpace(actor) || actor.Length > 450 || command.OperationId == Guid.Empty
			|| command.ExpectedTaxonomyRevisionId == Guid.Empty || command.CategoryId == Guid.Empty
			|| command.ExpectedMappingRevisionId == Guid.Empty || string.IsNullOrWhiteSpace(command.Reason)
			|| command.Reason.Length > 4000 || command.Reason.Contains('\0') || actor.Contains('\0')
			|| source.Location.Contains('\0') || source.Description.Contains('\0') || source.ItemCode?.Contains('\0') == true)
		{ throw new TaxonomyException(400, "Valid actor, operation, revisions, source and reason are required."); }
		if (fingerprint != command.SourceFingerprint) { throw new TaxonomyException(409, "Source changed. Review the current source before approving.", new Dictionary<string, object?> { ["sourceFingerprint"] = fingerprint, ["expectedSourceFingerprint"] = command.SourceFingerprint }); }
		string requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command with { Source = source })));
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
		// Same first lock as taxonomy edits. Serializes absent-source creation, revocation, and approvals
		// against hierarchy changes, including concurrent operations with different request IDs.
		TaxonomyHeadEntity taxonomy = db.Database.IsNpgsql()
			? await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE").SingleAsync(ct)
			: await db.TaxonomyHeads.SingleAsync(ct);
		DateTimeOffset now = clock.GetUtcNow();
		if (!await db.Users.AnyAsync(user => user.Id == actor && !(user.LockoutEnabled && user.LockoutEnd > now), ct))
		{ throw new TaxonomyException(403, "An enabled user is required to approve category mappings."); }
		CategoryMappingRevisionEntity? replay = await db.CategoryMappingRevisions.AsNoTracking()
			.SingleOrDefaultAsync(row => row.Actor == actor && row.OperationId == command.OperationId, ct);
		if (replay != null)
		{
			if (replay.RequestHash != requestHash) { throw new TaxonomyException(409, "Operation ID was already used with a different request."); }
			return Read(replay);
		}
		CategoryMappingHeadEntity? head = await db.CategoryMappingHeads.SingleOrDefaultAsync(row => row.SourceFingerprint == fingerprint, ct);
		TaxonomyException Conflict(string detail) => new(409, detail, new Dictionary<string, object?>
		{
			["sourceFingerprint"] = fingerprint,
			["currentMappingRevisionId"] = head?.RevisionId,
			["expectedMappingRevisionId"] = command.ExpectedMappingRevisionId,
			["currentTaxonomyRevisionId"] = taxonomy.RevisionId,
			["expectedTaxonomyRevisionId"] = command.ExpectedTaxonomyRevisionId
		});
		if (head?.RevisionId != command.ExpectedMappingRevisionId) { throw Conflict("Category mapping changed. Refresh before retrying."); }
		if (taxonomy.RevisionId != command.ExpectedTaxonomyRevisionId) { throw Conflict("Taxonomy changed. Refresh before approving."); }
		string? semantics = command.CategoryId is Guid categoryId
			? CategoryMappingPolicy.SemanticFingerprint(await NodesAsync(db, taxonomy.RevisionId, ct), categoryId) : null;
		if (command.CategoryId != null && semantics == null) { throw new TaxonomyException(409, "Select an active category with active ancestors."); }
		if (command.CategoryId == null && head == null) { throw new TaxonomyException(409, "There is no approved mapping to revoke."); }
		now = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
		CategoryMappingRevisionEntity revision = new()
		{
			Id = Guid.NewGuid(),
			PreviousRevisionId = head?.RevisionId,
			SourceFingerprint = fingerprint,
			Location = source.Location,
			Description = source.Description,
			ItemCode = source.ItemCode,
			CategoryId = command.CategoryId,
			TaxonomyRevisionId = taxonomy.RevisionId,
			SemanticFingerprint = semantics,
			Actor = actor,
			OperationId = command.OperationId,
			RequestHash = requestHash,
			Reason = command.Reason,
			RecordedAt = now
		};
		db.CategoryMappingRevisions.Add(revision);
		if (head == null) { db.CategoryMappingHeads.Add(new() { SourceFingerprint = fingerprint, RevisionId = revision.Id }); }
		else { head.RevisionId = revision.Id; }
		await db.SaveChangesAsync(ct);
		if (db.Database.IsNpgsql())
		{
			// The baseline is the explicit activation marker. Do not query the tip here:
			// a legacy baseline without an anchor must reach the database guard and fail closed.
			await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO taxonomy."MappingAuthorityEvents" ("Id", "BaselineId", "Kind", "MappingRevisionId")
                SELECT {Guid.NewGuid()}, "Id", 1, {revision.Id} FROM taxonomy."MappingAuthorityBaseline"
                """, ct);
		}
		if (transaction != null) { await transaction.CommitAsync(ct); }
		return Read(revision);
	}

	public async Task<CategoryMappingResolution> ResolveAsync(NormalizationSourceKey source, string sourceFingerprint,
		CategoryMappingExplicitChoice? explicitChoice = null, CancellationToken ct = default)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		Guid taxonomyRevision = await db.TaxonomyHeads.Select(row => row.RevisionId).SingleAsync(ct);
		return await ResolveInTransactionAsync(db, source, sourceFingerprint, taxonomyRevision, explicitChoice, ct);
	}

	// Purchase writers must already hold taxonomy.Head in their receipt transaction. Approval,
	// revocation and taxonomy changes take that same lock, so preview evidence cannot race a save.
	internal static async Task<CategoryMappingResolution> ResolveInTransactionAsync(ApplicationDbContext db,
		NormalizationSourceKey source, string sourceFingerprint, Guid taxonomyRevision,
		CategoryMappingExplicitChoice? explicitChoice, CancellationToken ct)
	{
		source = NormalizationCatalogPolicy.Normalize(source);
		if (explicitChoice != null)
		{
			if (explicitChoice.TaxonomyRevisionId != taxonomyRevision) { return new("stale_explicit_choice", null, taxonomyRevision); }
			if (explicitChoice.CategoryId is Guid category && CategoryMappingPolicy.SemanticFingerprint(await NodesAsync(db, taxonomyRevision, ct), category) == null)
			{ return new("unavailable_explicit_choice", null, taxonomyRevision); }
			return new("explicit", explicitChoice.CategoryId, taxonomyRevision);
		}
		string fingerprint = NormalizationCatalogPolicy.Hash(source);
		if (sourceFingerprint != fingerprint) { return new("stale_source", null, taxonomyRevision); }
		CategoryMappingRevisionEntity? mapping = await (from head in db.CategoryMappingHeads.AsNoTracking()
														join revision in db.CategoryMappingRevisions.AsNoTracking() on head.RevisionId equals revision.Id
														where head.SourceFingerprint == fingerprint
														select revision).SingleOrDefaultAsync(ct);
		if (mapping == null) { return new("unmapped", null, taxonomyRevision); }
		// Check source values too: a fingerprint alone is never approval authority.
		if (mapping.Location != source.Location || mapping.Description != source.Description || mapping.ItemCode != source.ItemCode)
		{ return new("stale_source", null, taxonomyRevision, mapping.Id, mapping.TaxonomyRevisionId); }
		if (mapping.CategoryId == null) { return new("revoked", null, taxonomyRevision, mapping.Id, mapping.TaxonomyRevisionId); }
		string? semantics = CategoryMappingPolicy.SemanticFingerprint(await NodesAsync(db, taxonomyRevision, ct), mapping.CategoryId.Value);
		string disposition = semantics == null ? "unavailable_category" : semantics != mapping.SemanticFingerprint ? "stale_category" : "approved";
		return new(disposition, disposition == "approved" ? mapping.CategoryId : null, taxonomyRevision, mapping.Id, mapping.TaxonomyRevisionId);
	}

	private static Task<List<TaxonomyNode>> NodesAsync(ApplicationDbContext db, Guid revisionId, CancellationToken ct)
		=> db.TaxonomyNodes.AsNoTracking().Where(row => row.RevisionId == revisionId)
			.Select(row => new TaxonomyNode(row.CategoryId, row.ParentId, row.Name, row.Definition, row.Archived)).ToListAsync(ct);
	private static CategoryMappingRevision Read(CategoryMappingRevisionEntity row)
		=> new(row.Id, row.PreviousRevisionId, new(row.Location, row.Description, row.ItemCode), row.SourceFingerprint,
			row.CategoryId, row.TaxonomyRevisionId, row.SemanticFingerprint, row.Actor, row.OperationId, row.Reason, row.RecordedAt);
}
