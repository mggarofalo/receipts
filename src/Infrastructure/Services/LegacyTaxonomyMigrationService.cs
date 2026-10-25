using System.Text.Json;
using Application.Taxonomy;
using Infrastructure.Configurations;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Interfaces;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>Bounded initial migration; failed transactions restart from frozen evidence.</summary>
public sealed class LegacyTaxonomyMigrationService(IDbContextFactory<ApplicationDbContext> factory) : ILegacyTaxonomyMigrationService
{
	public const string InitialOperationKind = "initial_legacy_import";
	public const int MaximumSourceRows = 100_000;

	public async Task<LegacyMigrationPlan> PreflightAsync(LegacyMigrationCapture capture, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
		await LockAsync(db, cancellationToken);
		LegacyMigrationRequest source = await CaptureAsync(db, capture, cancellationToken);
		LegacyMigrationPlan plan = LegacyTaxonomyMigrationPlanner.Plan(source);
		LegacyMigrationEntity? prior = await PriorAsync(db, source, cancellationToken);
		if (prior is not null) { return Replay(prior, plan); }
		await RequireGenesisAsync(db, cancellationToken);
		return plan;
	}

	public async Task<LegacyMigrationPlan> ApplyAsync(LegacyMigrationRequest frozenSource, CancellationToken cancellationToken = default)
	{
		if (frozenSource.OperationKind != InitialOperationKind)
		{ throw new InvalidOperationException("This entry point only supports initial legacy migration."); }
		if ((long)frozenSource.Categories.Count + frozenSource.Subcategories.Count + frozenSource.Items.Count > MaximumSourceRows)
		{ throw new InvalidOperationException($"Initial migration is limited to {MaximumSourceRows} combined source rows, including trash."); }
		LegacyMigrationPlan plan = LegacyTaxonomyMigrationPlanner.Plan(frozenSource);
		if (!plan.CanCommit) { throw new InvalidOperationException("Preflight has blocking exceptions. Resolve the reported source data before applying."); }
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
		TaxonomyHeadEntity head = await LockAsync(db, cancellationToken);
		LegacyMigrationEntity? previous = await PriorAsync(db, frozenSource, cancellationToken);
		if (previous is not null) { return Replay(previous, plan); }
		await RequireGenesisAsync(db, cancellationToken);
		LegacyMigrationRequest current = await CaptureAsync(db, new(plan.SourceDataset, plan.OperationId, plan.Actor, plan.ObservedAt), cancellationToken);
		if (LegacyTaxonomyMigrationPlanner.Plan(current).RequestHash != plan.RequestHash)
		{ throw new LegacyMigrationReplayException(); }

		await AppendPlanAsync(db, head, plan, cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		return plan;
	}

	/// <summary>Appends frozen file evidence inside the caller's existing import transaction.</summary>
	internal static async Task AppendInTransactionAsync(ApplicationDbContext db, LegacyMigrationRequest source,
		CancellationToken cancellationToken)
	{
		if (db.Database.CurrentTransaction is null)
		{ throw new InvalidOperationException("Legacy import requires an active transaction."); }
		if (source.OperationKind != InitialOperationKind)
		{ throw new InvalidOperationException("Unsupported legacy migration operation kind."); }
		LegacyMigrationPlan plan = LegacyTaxonomyMigrationPlanner.Plan(source);
		if (!plan.CanCommit) { throw new InvalidOperationException("Legacy backup taxonomy preflight has blocking exceptions."); }
		TaxonomyHeadEntity head = await LockAsync(db, cancellationToken);
		await RequireGenesisAsync(db, cancellationToken);
		await AppendPlanAsync(db, head, plan, cancellationToken);
	}

	private static async Task AppendPlanAsync(ApplicationDbContext db, TaxonomyHeadEntity head,
		LegacyMigrationPlan plan, CancellationToken cancellationToken)
	{
		db.TaxonomyRevisions.Add(new()
		{
			Id = plan.RevisionId,
			Sequence = 1,
			PreviousRevisionId = TaxonomyConfiguration.GenesisId,
			RecordedAt = plan.ObservedAt,
			Actor = plan.Actor,
			Reason = "Observed legacy classification at migration; earlier history is unknown.",
			OperationId = plan.OperationId,
			AffectedCount = plan.Nodes.Count
		});
		db.TaxonomyIdentities.AddRange(plan.Nodes.Select(row => new TaxonomyIdentityEntity { Id = row.Node.Id }));
		db.TaxonomyNodes.AddRange(plan.Nodes.Select(row => new TaxonomyNodeEntity
		{
			RevisionId = plan.RevisionId,
			CategoryId = row.Node.Id,
			ParentId = row.Node.ParentId,
			Name = row.Node.Name,
			Definition = row.Node.Definition,
			Archived = row.Node.Archived
		}));
		// The revision and complete snapshot are inserted in this transaction; sealed-history triggers remain enabled.
		await db.SaveChangesAsync(cancellationToken);
		db.Classifications.AddRange(plan.Items.Select(row => new PurchaseClassificationEntity
		{
			Id = row.AssignmentId,
			ReceiptItemId = row.Evidence.Id,
			Sequence = 1,
			CategoryId = row.CategoryId,
			TaxonomyRevisionId = plan.RevisionId,
			RecordedAt = plan.ObservedAt,
			Actor = plan.Actor,
			Source = "legacy_import",
			Reason = row.Exception ?? row.Kind.ToString(),
			OperationId = plan.OperationId
		}));
		await db.SaveChangesAsync(cancellationToken);
		db.CurrentClassifications.AddRange(plan.Items.Select(row => new CurrentClassificationEntity
		{ ReceiptItemId = row.Evidence.Id, AssignmentId = row.AssignmentId }));
		db.LegacyMigrationCrosswalks.AddRange(plan.Crosswalks.Select(row => new LegacyMigrationCrosswalkEntity
		{
			Actor = plan.Actor,
			OperationId = plan.OperationId,
			SourceTable = row.Source.ToString(),
			SourceId = row.SourceId,
			TargetId = row.TargetId,
			EvidenceJson = JsonSerializer.Serialize(row)
		}));
		db.LegacyMigrationItems.AddRange(plan.Items.Select(row => new LegacyMigrationItemEntity
		{
			Actor = plan.Actor,
			OperationId = plan.OperationId,
			ReceiptItemId = row.Evidence.Id,
			AssignmentId = row.AssignmentId,
			Resolution = row.Kind.ToString(),
			EvidenceJson = JsonSerializer.Serialize(row)
		}));
		db.LegacyMigrations.Add(new()
		{
			Actor = plan.Actor,
			OperationId = plan.OperationId,
			OperationKind = plan.OperationKind,
			SourceDataset = plan.SourceDataset,
			RequestHash = plan.RequestHash,
			ObservedAt = plan.ObservedAt,
			RevisionId = plan.RevisionId,
			PlanJson = JsonSerializer.Serialize(plan)
		});
		head.RevisionId = plan.RevisionId;
		await db.SaveChangesAsync(cancellationToken);
	}

	public async Task<LegacyMigrationStatus> ReadStatusAsync(CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(cancellationToken);
		LegacyMigrationEntity? completed = await db.LegacyMigrations.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
		if (completed is null) { return new(false, null, null); }
		// Imports/new rows cannot inherit a stale completed marker. Historical rows and trash count too.
		bool missing = await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes()
			.AnyAsync(item => !db.CurrentClassifications.Any(current => current.ReceiptItemId == item.Id), cancellationToken);
		return new(!missing, completed.RevisionId, completed.ObservedAt);
	}

	private static Task<LegacyMigrationEntity?> PriorAsync(ApplicationDbContext db, LegacyMigrationRequest source, CancellationToken ct) =>
		db.LegacyMigrations.AsNoTracking().SingleOrDefaultAsync(row => row.Actor == source.Actor
			&& row.OperationId == source.OperationId && row.OperationKind == source.OperationKind, ct);

	private static LegacyMigrationPlan Replay(LegacyMigrationEntity prior, LegacyMigrationPlan plan)
	{
		if (prior.RequestHash != plan.RequestHash) { throw new LegacyMigrationReplayException(); }
		return JsonSerializer.Deserialize<LegacyMigrationPlan>(prior.PlanJson)
			?? throw new InvalidOperationException("Stored migration result is invalid.");
	}

	internal static async Task<TaxonomyHeadEntity> LockAsync(ApplicationDbContext db, CancellationToken ct)
	{
		await BackupImportLock.AcquireDatabaseAsync(db, ct);
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		// Table locks also fence source writers predating the shared application gate,
		// including suggestion CRUD, inserts, trash and import; no triggers are disabled.
		await db.Database.ExecuteSqlRawAsync("LOCK TABLE library.\"Categories\", library.\"Subcategories\", receipts.\"Receipts\", receipts.\"ReceiptItems\", matching.\"NormalizedDescriptions\" IN SHARE MODE", ct);
		return await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE").SingleAsync(ct);
	}

	internal static async Task RequireGenesisAsync(ApplicationDbContext db, CancellationToken ct)
	{
		bool untouched = await db.TaxonomyHeads.AllAsync(row => row.RevisionId == TaxonomyConfiguration.GenesisId, ct)
			&& await db.TaxonomyRevisions.CountAsync(ct) == 1
			&& !await db.TaxonomyIdentities.AnyAsync(ct) && !await db.TaxonomyNodes.AnyAsync(ct)
			&& !await db.TaxonomyOperations.AnyAsync(ct) && !await db.Classifications.AnyAsync(ct)
			&& !await db.ClassificationOperations.AnyAsync(ct) && !await db.CurrentClassifications.AnyAsync(ct)
			&& !await db.LegacyMigrations.AnyAsync(ct);
		if (!untouched)
		{ throw new InvalidOperationException("Initial migration requires the untouched empty genesis taxonomy and no classification history. Existing taxonomy is preserved. Restore into a fresh database or arrange explicit reviewed reconciliation; names are never matched to an unrelated tree."); }
	}

	private static LegacyDeletionState Deletion(ISoftDeletable row) => new(row.DeletedAt,
		row.DeletedByUserId, row.CascadeDeletedByParentId, row.DeletedByApiKeyId);

	private static async Task<LegacyMigrationRequest> CaptureAsync(ApplicationDbContext db, LegacyMigrationCapture capture, CancellationToken ct)
	{
		var categories = db.Categories.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking();
		var subcategories = db.Set<Infrastructure.Entities.Core.SubcategoryEntity>().IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking();
		var items = db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking();
		long count = (long)await categories.CountAsync(ct) + await subcategories.CountAsync(ct) + await items.CountAsync(ct);
		if (count > MaximumSourceRows)
		{ throw new InvalidOperationException($"Initial migration is limited to {MaximumSourceRows} combined category, subcategory and receipt-item rows, including trash. Use a reviewed batched migration for this dataset."); }
		var categoryRows = await categories.ToListAsync(ct);
		var subcategoryRows = await subcategories.ToListAsync(ct);
		var itemRows = await items.ToListAsync(ct);
		Guid[] receiptIds = itemRows.Select(row => row.ReceiptId).Distinct().ToArray();
		Guid[] productIds = itemRows.Where(row => row.NormalizedDescriptionId.HasValue).Select(row => row.NormalizedDescriptionId!.Value).Distinct().ToArray();
		var receiptRows = await db.Receipts.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
			.Where(row => receiptIds.Contains(row.Id)).ToDictionaryAsync(row => row.Id, ct);
		var products = await db.NormalizedDescriptions.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
			.Where(row => productIds.Contains(row.Id)).Select(row => row.Id).ToHashSetAsync(ct);
		return new(capture.SourceDataset, capture.OperationId, InitialOperationKind, capture.Actor, capture.ObservedAt,
			categoryRows.Select(row => new LegacyCategoryEvidence(row.Id, row.Name, row.Description, row.IsActive, Deletion(row))).ToArray(),
			subcategoryRows.Select(row => new LegacySubcategoryEvidence(row.Id, row.CategoryId, row.Name, row.Description, row.IsActive, Deletion(row))).ToArray(),
			itemRows.Select(row => new LegacyItemEvidence(row.Id, row.ReceiptId, row.Category, row.Subcategory,
				Deletion(row), Deletion(receiptRows[row.ReceiptId]), row.TotalAmount, row.TotalAmountCurrency.ToString(),
				row.NormalizedDescriptionId, row.NormalizedDescriptionId is not Guid id ? null
					: !products.Contains(id) ? "missing_product" : null)).ToArray());
	}
}
