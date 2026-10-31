using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Normalization;
using Application.Taxonomy;
using Application.Taxonomy.Restructure;
using Domain.Taxonomy;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Envelopes;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Applies a restructure plan: one taxonomy revision, the defaults, envelope merges and approved-mapping
/// re-pointing it implies, and the purchase reassignments, which run in chunks through the historical
/// reclassification path as a <see cref="HistoricalOperationKind.RestructureV1"/> operation with conditional undo.
/// </summary>
public sealed class RestructurePlanService(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock,
	IOptions<ClassificationOptions> options, HistoricalReclassificationService historical,
	ICommittedChangePublisher? publisher = null) : IRestructurePlanService
{
	internal const string UndoReason = "Undid a category restructure.";

	public async Task<RestructurePreview> PreviewAsync(RestructurePlan plan, CancellationToken ct = default)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
		Guid head = await db.TaxonomyHeads.AsNoTracking().Select(row => row.RevisionId).SingleAsync(ct);
		RestructureOutcome outcome = RestructurePlanner.Plan(plan, await LoadStateAsync(db, head, plan.BaseTaxonomyRevisionId, ct));
		return new(outcome.IsValid, outcome.Errors, outcome.Warnings, outcome.Report);
	}

	public async Task<RestructureApplicationSummary> BeginAsync(string actor, Guid applicationId, RestructurePlan plan,
		CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(actor) || applicationId == Guid.Empty)
		{ throw new TaxonomyException(400, "Actor and application ID are required."); }
		string planJson = JsonSerializer.Serialize(plan);
		string planHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planJson)));
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		await using var transaction = await db.Database.BeginTransactionAsync(ct);
		// The lock order of every classification writer: write gate, then the taxonomy head.
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		TaxonomyHeadEntity head = await LockHeadAsync(db, ct);
		RestructureApplicationEntity? existing = await db.RestructureApplications.AsNoTracking().SingleOrDefaultAsync(row => row.Id == applicationId, ct);
		if (existing is not null)
		{
			if (existing.Actor != actor || existing.PlanHash != planHash)
			{ throw new TaxonomyException(409, "Application ID was already used with a different plan."); }
			return await SummaryAsync(db, existing, ct);
		}
		DateTimeOffset now = Truncate(clock.GetUtcNow());
		if (!await TaxonomyService.IsEnabledAdminAsync(db, actor, now, ct))
		{ throw new TaxonomyException(403, "An enabled administrator is required to restructure categories."); }
		if (await (from application in db.RestructureApplications
				   join operation in db.HistoricalOperations on application.Id equals operation.Id
				   where operation.State != HistoricalOperationState.Undone
					   && (application.PlanId == plan.PlanId || operation.State != HistoricalOperationState.Completed)
				   select application.Id).AnyAsync(ct))
		{ throw new TaxonomyException(409, "This plan is already applied, or another restructure is still running. Undo or finish it first."); }

		RestructureOutcome outcome = RestructurePlanner.Plan(plan, await LoadStateAsync(db, head.RevisionId, plan.BaseTaxonomyRevisionId, ct));
		if (!outcome.IsValid)
		{ throw new TaxonomyException(400, outcome.Errors[0], new Dictionary<string, object?> { ["errors"] = outcome.Errors }); }

		Guid baseRevision = head.RevisionId;
		if (plan.TaxonomyChanges.Count > 0)
		{
			TaxonomyChangeCommand command = new(head.RevisionId, Derived(applicationId, "taxonomy"), plan.Reason,
				[.. plan.TaxonomyChanges.Select(change => change.ToEdit())]);
			await TaxonomyService.AppendInTransactionAsync(db, head, actor, command, TaxonomyService.RequestHash(command), now, ct);
			// Mapping approvals read the new revision's nodes from the database.
			await db.SaveChangesAsync(ct);
		}
		List<RestructureChangeEntity> changes = [];
		void Record(RestructureChangeKind kind, Guid subject, Guid? before, Guid? after) => changes.Add(new()
		{ ApplicationId = applicationId, Ordinal = changes.Count, Kind = kind, SubjectId = subject, Before = before, After = after });

		Dictionary<string, Guid> occasions = await db.Occasions.AsNoTracking().Where(row => row.IsActive)
			.ToDictionaryAsync(row => row.Name, row => row.Id, StringComparer.Ordinal, ct);
		foreach (string name in outcome.OccasionsToCreate)
		{
			OccasionEntity occasion = new() { Id = Guid.NewGuid(), Name = name, IsActive = true };
			db.Occasions.Add(occasion);
			occasions[name] = occasion.Id;
			Record(RestructureChangeKind.OccasionCreated, occasion.Id, null, null);
		}
		await ApplyDefaultsAsync(db, outcome.DefaultChanges, now, Record, ct);
		await ApplyMergesAsync(db, outcome.EnvelopeMerges, Record, ct);
		foreach (RestructureMappingChange change in outcome.MappingChanges)
		{
			CategoryMappingRevisionEntity revision = await ChangeMappingAsync(db, head, actor, change.Mapping, change.Mapping.RevisionId,
				change.CategoryId, Derived(applicationId, "mapping:" + change.Mapping.SourceFingerprint), plan.Reason, now, ct);
			Record(RestructureChangeKind.ApprovedMapping, revision.Id, change.Mapping.CategoryId, change.CategoryId);
		}
		db.RestructureChanges.AddRange(changes);

		db.HistoricalOperations.Add(new()
		{
			Id = applicationId,
			Kind = HistoricalOperationKind.RestructureV1,
			Actor = actor,
			RequestHash = planHash,
			SelectionJson = "[]",
			TaxonomyRevisionId = head.RevisionId,
			CapturedAt = now,
			Reason = plan.Reason,
			State = HistoricalOperationState.Preview
		});
		db.RestructureApplications.Add(new()
		{
			Id = applicationId,
			PlanId = plan.PlanId,
			Actor = actor,
			PlanHash = planHash,
			PlanJson = planJson,
			ReportJson = JsonSerializer.Serialize(outcome.Report),
			Reason = plan.Reason,
			BaseRevisionId = baseRevision,
			AppliedRevisionId = head.RevisionId,
			AppliedAt = now
		});
		await db.SaveChangesAsync(ct);
		await CaptureItemsAsync(db, applicationId, outcome.ItemChanges, occasions, ct);
		await transaction.CommitAsync(ct);
		if (publisher is not null)
		{ await publisher.PublishAsync(new(CommittedEntityType.Taxonomy, CommittedChangeType.Updated)); }
		return await ReadAsync(applicationId, ct);
	}

	public async Task<RestructureApplicationSummary> ApplyChunkAsync(string actor, Guid applicationId, int limit, CancellationToken ct = default)
	{
		HistoricalProgress progress = await historical.RunRestructureChunkAsync(actor, applicationId, null, limit,
			Writer(await PlanIdAsync(applicationId, ct)), ct);
		await PublishItemsAsync(progress, ct);
		return await ReadAsync(applicationId, ct);
	}

	public async Task<RestructureApplicationSummary> CancelAsync(string actor, Guid applicationId, CancellationToken ct = default)
	{
		await historical.CancelAsync(actor, applicationId, HistoricalOperationKind.RestructureV1, ct);
		return await ReadAsync(applicationId, ct);
	}

	public async Task<RestructureApplicationSummary> UndoChunkAsync(string actor, Guid applicationId, int limit, CancellationToken ct = default)
	{
		if (limit is < 1 or > HistoricalReclassificationPolicy.MaximumChunkSize) { throw new ArgumentOutOfRangeException(nameof(limit)); }
		Guid undoRevision = await BeginUndoAsync(actor, applicationId, ct);
		HistoricalProgress progress = await historical.RunRestructureChunkAsync(actor, applicationId,
			new(undoRevision, UndoReason), limit, Writer(await PlanIdAsync(applicationId, ct)), ct);
		if (progress.State == HistoricalOperationState.Undone) { await FinishUndoAsync(applicationId, ct); }
		await PublishItemsAsync(progress, ct);
		return await ReadAsync(applicationId, ct);
	}

	public async Task<RestructureApplicationPage> ListAsync(int offset, int limit, CancellationToken ct = default)
	{
		if (offset < 0 || limit is < 1 or > 25) { throw new TaxonomyException(400, "Use a nonnegative offset and a limit of 1 to 25."); }
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		List<RestructureApplicationEntity> rows = await db.RestructureApplications.AsNoTracking()
			.OrderByDescending(row => row.AppliedAt).ThenByDescending(row => row.Id).Skip(offset).Take(limit + 1).ToListAsync(ct);
		List<RestructureApplicationSummary> items = [];
		foreach (RestructureApplicationEntity row in rows.Take(limit)) { items.Add(await SummaryAsync(db, row, ct)); }
		return new(items, rows.Count > limit ? offset + limit : null);
	}

	public async Task<RestructureApplicationSummary> ReadAsync(Guid applicationId, CancellationToken ct = default)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		RestructureApplicationEntity row = await db.RestructureApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == applicationId, ct)
			?? throw new TaxonomyException(404, "Restructure does not exist.");
		return await SummaryAsync(db, row, ct);
	}

	public async Task<RestructureItemPage> ReadItemsAsync(Guid applicationId, RestructureItemFilter filter, int offset, int limit,
		CancellationToken ct = default)
	{
		if (offset < 0 || limit is < 1 or > 100) { throw new TaxonomyException(400, "Use a nonnegative offset and a limit of 1 to 100."); }
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		RestructureApplicationEntity application = await db.RestructureApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == applicationId, ct)
			?? throw new TaxonomyException(404, "Restructure does not exist.");
		IQueryable<HistoricalOperationItemEntity> query = db.HistoricalOperationItems.AsNoTracking().Where(x => x.OperationId == applicationId);
		query = filter switch
		{
			RestructureItemFilter.Conflicted => query.Where(x => x.State == HistoricalItemState.Conflicted),
			RestructureItemFilter.Cleared => query.Where(x => x.TargetCategoryId == null && x.State != HistoricalItemState.Conflicted),
			_ => query
		};
		var rows = await (from item in query
						  join extra in db.RestructureItems.AsNoTracking() on new { ApplicationId = item.OperationId, item.ItemId }
							  equals new { extra.ApplicationId, extra.ItemId }
						  orderby item.ItemId
						  select new { item, extra.Rule }).Skip(offset).Take(limit + 1).ToListAsync(ct);
		Dictionary<Guid, TaxonomyNode> before = await NodesAsync(db, application.BaseRevisionId, ct);
		Dictionary<Guid, TaxonomyNode> after = await NodesAsync(db, application.AppliedRevisionId, ct);
		return new(rows.Take(limit).Select(row => new RestructureItemRow(row.item.ItemId, row.item.ReceiptId, row.item.Store,
				row.item.RawDescription, RestructurePlanner.PathOf(before, row.item.PreviousCategoryId),
				RestructurePlanner.PathOf(after, row.item.TargetCategoryId), row.Rule, row.item.State, row.item.Reason,
				row.item.UndoState, row.item.UndoReason)).ToList(),
			rows.Count > limit ? offset + limit : null);
	}

	private async Task<Guid> PlanIdAsync(Guid applicationId, CancellationToken ct)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		return await db.RestructureApplications.Where(x => x.Id == applicationId).Select(x => (Guid?)x.PlanId).SingleOrDefaultAsync(ct)
			?? throw new TaxonomyException(404, "Restructure does not exist.");
	}

	/// <summary>
	/// Writes a chunk's <c>restructure</c> assignments with their evidence in one save, and sets or puts back each occasion
	/// tag. On undo a tag goes back only while nobody has changed it since.
	/// </summary>
	private HistoricalChunkWriter Writer(Guid planId) => async (db, actor, writes, reverting, ct) =>
	{
		Guid applicationId = writes[0].Row.OperationId;
		Guid[] itemIds = writes.Select(x => x.Row.ItemId).ToArray();
		Dictionary<Guid, RestructureItemEntity> extras = await db.RestructureItems.AsNoTracking()
			.Where(x => x.ApplicationId == applicationId && itemIds.Contains(x.ItemId)).ToDictionaryAsync(x => x.ItemId, ct);
		IReadOnlyDictionary<Guid, Guid> written = await ClassificationService.AssignRestructureInTransactionAsync(db, actor,
			writes.Select(write => (write.Row.ItemId, write.Command with
			{
				Reason = reverting
					? JsonSerializer.Serialize(new RestructureEvidence(planId, "undo", write.Row.TargetCategoryId))
					: extras[write.Row.ItemId].Evidence
			})).ToList(), options.Value.WritesEnabled, clock, ct);
		foreach (HistoricalWrite write in writes)
		{
			if (extras[write.Row.ItemId].TargetOccasionId is not Guid tagged) { continue; }
			if (!reverting) { write.Item.OccasionId = tagged; }
			else if (write.Item.OccasionId == tagged) { write.Item.OccasionId = extras[write.Row.ItemId].PreviousOccasionId; }
		}
		return written;
	};

	/// <summary>
	/// Puts the categories, defaults, envelopes and approved mappings back, once. Returns the revision the purchase
	/// undo runs against.
	/// </summary>
	private async Task<Guid> BeginUndoAsync(string actor, Guid applicationId, CancellationToken ct)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		await using var transaction = await db.Database.BeginTransactionAsync(ct);
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		TaxonomyHeadEntity head = await LockHeadAsync(db, ct);
		HistoricalOperationEntity operation = await db.HistoricalOperations
			.FromSqlInterpolated($"SELECT * FROM taxonomy.\"HistoricalOperations\" WHERE \"Id\" = {applicationId} FOR UPDATE")
			.SingleOrDefaultAsync(x => x.Actor == actor && x.Kind == HistoricalOperationKind.RestructureV1, ct)
			?? throw new TaxonomyException(404, "Restructure does not exist.");
		RestructureApplicationEntity application = await db.RestructureApplications.SingleAsync(x => x.Id == applicationId, ct);
		if (application.UndoRevisionId is Guid begun)
		{
			await transaction.CommitAsync(ct);
			return begun;
		}
		if (head.RevisionId != application.AppliedRevisionId)
		{ throw new TaxonomyException(409, "Categories changed after this restructure. Undo those changes first, or keep the restructure."); }
		DateTimeOffset now = Truncate(clock.GetUtcNow());
		if (!await TaxonomyService.IsEnabledAdminAsync(db, actor, now, ct))
		{ throw new TaxonomyException(403, "An enabled administrator is required to restructure categories."); }
		// An interrupted apply stops here; purchases it already reassigned are undone like the rest.
		if (operation.State is HistoricalOperationState.Preview or HistoricalOperationState.Applying)
		{ operation.State = HistoricalReclassificationPolicy.Cancel(operation.State); }

		List<TaxonomyNode> current = [.. (await NodesAsync(db, head.RevisionId, ct)).Values];
		List<TaxonomyNode> original = [.. (await NodesAsync(db, application.BaseRevisionId, ct)).Values];
		IReadOnlyList<TaxonomyEdit> revert = RestructurePlanner.RevertChanges(current, original);
		if (revert.Count > 0)
		{
			TaxonomyChangeCommand command = new(head.RevisionId, Derived(applicationId, "undo-taxonomy"), UndoReason, revert);
			await TaxonomyService.AppendInTransactionAsync(db, head, actor, command, TaxonomyService.RequestHash(command), now, ct);
			await db.SaveChangesAsync(ct);
		}
		List<RestructureChangeEntity> changes = await db.RestructureChanges.Where(x => x.ApplicationId == applicationId)
			.OrderByDescending(x => x.Ordinal).ToListAsync(ct);
		foreach (RestructureChangeEntity change in changes)
		{
			change.UndoOutcome = await RevertAsync(db, head, actor, applicationId, change, now, ct);
		}
		application.UndoRevisionId = head.RevisionId;
		await db.SaveChangesAsync(ct);
		await transaction.CommitAsync(ct);
		if (publisher is not null)
		{ await publisher.PublishAsync(new(CommittedEntityType.Taxonomy, CommittedChangeType.Updated)); }
		return head.RevisionId;
	}

	private static async Task<string> RevertAsync(ApplicationDbContext db, TaxonomyHeadEntity head, string actor, Guid applicationId,
		RestructureChangeEntity change, DateTimeOffset now, CancellationToken ct)
	{
		switch (change.Kind)
		{
			case RestructureChangeKind.EnvelopeDefault:
				ItemTypeEnvelopeDefaultEntity? row = await db.ItemTypeEnvelopeDefaults.SingleOrDefaultAsync(x => x.CategoryId == change.SubjectId, ct);
				if (row?.EnvelopeId != change.After) { return "changed-since"; }
				SetDefault(db, row, change.SubjectId, change.Before, now);
				return "restored";
			case RestructureChangeKind.ItemEnvelope:
				ReceiptItemEntity? item = await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().SingleOrDefaultAsync(x => x.Id == change.SubjectId, ct);
				if (item is null || item.EnvelopeId != change.After) { return "changed-since"; }
				item.EnvelopeId = change.Before;
				return "restored";
			case RestructureChangeKind.ReceiptPaidFrom:
				ReceiptEntity? receipt = await db.Receipts.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == change.SubjectId, ct);
				if (receipt is null || receipt.PaidFromEnvelopeId != change.After) { return "changed-since"; }
				receipt.PaidFromEnvelopeId = change.Before;
				return "restored";
			case RestructureChangeKind.EnvelopeArchived:
				EnvelopeEntity? envelope = await db.Envelopes.SingleOrDefaultAsync(x => x.Id == change.SubjectId, ct);
				if (envelope is null || envelope.IsActive) { return "changed-since"; }
				envelope.IsActive = true;
				return "restored";
			case RestructureChangeKind.ApprovedMapping:
				CategoryMappingHeadEntity? mappingHead = await db.CategoryMappingHeads.AsNoTracking().SingleOrDefaultAsync(x => x.RevisionId == change.SubjectId, ct);
				if (mappingHead is null || change.Before is not Guid previous) { return "changed-since"; }
				CategoryMappingRevisionEntity written = await db.CategoryMappingRevisions.AsNoTracking().SingleAsync(x => x.Id == change.SubjectId, ct);
				if (CategoryMappingPolicy.SemanticFingerprint([.. (await NodesAsync(db, head.RevisionId, ct)).Values], previous) is null)
				{ return "category-unavailable"; }
				RestructureMappingState mapping = new(new(written.Location, written.Description, written.ItemCode), written.SourceFingerprint,
					written.Id, previous, null);
				await ChangeMappingAsync(db, head, actor, mapping, written.Id, previous, Derived(applicationId, "undo-mapping:" + written.SourceFingerprint),
					UndoReason, now, ct);
				return "restored";
			default:
				// Created occasions are removed once no purchase carries them any more (FinishUndoAsync).
				return "pending";
		}
	}

	private async Task FinishUndoAsync(Guid applicationId, CancellationToken ct)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		await using var transaction = await db.Database.BeginTransactionAsync(ct);
		RestructureApplicationEntity application = await db.RestructureApplications.SingleAsync(x => x.Id == applicationId, ct);
		if (application.UndoneAt is not null) { return; }
		foreach (RestructureChangeEntity change in await db.RestructureChanges
			.Where(x => x.ApplicationId == applicationId && x.Kind == RestructureChangeKind.OccasionCreated).ToListAsync(ct))
		{
			bool used = await db.ReceiptItems.IgnoreQueryFilters().AnyAsync(x => x.OccasionId == change.SubjectId, ct)
				|| await db.Receipts.IgnoreQueryFilters().AnyAsync(x => x.OccasionId == change.SubjectId, ct);
			OccasionEntity? occasion = await db.Occasions.SingleOrDefaultAsync(x => x.Id == change.SubjectId, ct);
			if (!used && occasion is not null) { db.Occasions.Remove(occasion); }
			change.UndoOutcome = used ? "in-use" : "removed";
		}
		application.UndoneAt = Truncate(clock.GetUtcNow());
		await db.SaveChangesAsync(ct);
		await transaction.CommitAsync(ct);
	}

	private static async Task ApplyDefaultsAsync(ApplicationDbContext db, IReadOnlyList<RestructureDefaultChange> defaults, DateTimeOffset now,
		Action<RestructureChangeKind, Guid, Guid?, Guid?> record, CancellationToken ct)
	{
		Dictionary<Guid, ItemTypeEnvelopeDefaultEntity> rows = await db.ItemTypeEnvelopeDefaults.ToDictionaryAsync(x => x.CategoryId, ct);
		foreach (RestructureDefaultChange change in defaults)
		{
			SetDefault(db, rows.GetValueOrDefault(change.CategoryId), change.CategoryId, change.EnvelopeId, now);
			record(RestructureChangeKind.EnvelopeDefault, change.CategoryId, change.PreviousEnvelopeId, change.EnvelopeId);
		}
	}

	private static void SetDefault(ApplicationDbContext db, ItemTypeEnvelopeDefaultEntity? row, Guid category, Guid? envelope, DateTimeOffset now)
	{
		if (envelope is not Guid id)
		{
			if (row is not null) { db.ItemTypeEnvelopeDefaults.Remove(row); }
			return;
		}
		if (row is null)
		{ db.ItemTypeEnvelopeDefaults.Add(new() { Id = Guid.NewGuid(), CategoryId = category, EnvelopeId = id, CreatedAt = now, UpdatedAt = now }); }
		else { row.EnvelopeId = id; row.UpdatedAt = now; }
	}

	/// <summary>Moves purchases, receipts and defaults (already re-pointed by the planner) and archives each source.</summary>
	private static async Task ApplyMergesAsync(ApplicationDbContext db, IReadOnlyList<RestructureResolvedMerge> merges,
		Action<RestructureChangeKind, Guid, Guid?, Guid?> record, CancellationToken ct)
	{
		foreach (RestructureResolvedMerge merge in merges)
		{
			foreach (ReceiptItemEntity item in await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes()
				.Where(x => x.EnvelopeId == merge.SourceEnvelopeId).OrderBy(x => x.Id).ToListAsync(ct))
			{
				item.EnvelopeId = merge.TargetEnvelopeId;
				record(RestructureChangeKind.ItemEnvelope, item.Id, merge.SourceEnvelopeId, merge.TargetEnvelopeId);
			}
			foreach (ReceiptEntity receipt in await db.Receipts.IgnoreQueryFilters()
				.Where(x => x.PaidFromEnvelopeId == merge.SourceEnvelopeId).OrderBy(x => x.Id).ToListAsync(ct))
			{
				receipt.PaidFromEnvelopeId = merge.TargetEnvelopeId;
				record(RestructureChangeKind.ReceiptPaidFrom, receipt.Id, merge.SourceEnvelopeId, merge.TargetEnvelopeId);
			}
			EnvelopeEntity source = await db.Envelopes.SingleAsync(x => x.Id == merge.SourceEnvelopeId, ct);
			if (source.IsActive)
			{
				source.IsActive = false;
				record(RestructureChangeKind.EnvelopeArchived, source.Id, null, null);
			}
		}
	}

	private static async Task<CategoryMappingRevisionEntity> ChangeMappingAsync(ApplicationDbContext db, TaxonomyHeadEntity head, string actor,
		RestructureMappingState mapping, Guid expectedRevision, Guid? category, Guid operationId, string reason, DateTimeOffset now,
		CancellationToken ct)
	{
		NormalizationSourceKey source = NormalizationCatalogPolicy.Normalize(mapping.Source);
		CategoryMappingCommand command = new(source, mapping.SourceFingerprint, expectedRevision, head.RevisionId, category, operationId, reason);
		return await CategoryMappingService.ChangeInTransactionAsync(db, head, actor, command, mapping.SourceFingerprint,
			CategoryMappingService.RequestHash(command), now, ct);
	}

	/// <summary>
	/// Freezes each reassignment as a historical row (with source revisions and the expected assignment) plus its
	/// rule, evidence and occasion. Purchases classified after the plan's snapshot are captured as conflicts.
	/// </summary>
	private static async Task CaptureItemsAsync(ApplicationDbContext db, Guid applicationId, IReadOnlyList<RestructureItemChange> changes,
		Dictionary<string, Guid> occasions, CancellationToken ct)
	{
		const int batch = 2000;
		for (int offset = 0; offset < changes.Count; offset += batch)
		{
			List<RestructureItemChange> page = changes.Skip(offset).Take(batch).ToList();
			Guid[] ids = page.Select(change => change.ItemId).ToArray();
			var sources = await (from item in db.ReceiptItems.AsNoTracking().IgnoreAutoIncludes()
								 join receipt in db.Receipts.AsNoTracking() on item.ReceiptId equals receipt.Id
								 where ids.Contains(item.Id)
								 select new
								 {
									 item.Id,
									 item.ReceiptId,
									 receipt.Location,
									 item.Description,
									 item.ReceiptItemCode,
									 ItemRevision = item.NormalizationRevision,
									 ReceiptRevision = receipt.NormalizationRevision
								 })
				.ToDictionaryAsync(row => row.Id, ct);
			foreach (RestructureItemChange change in page)
			{
				var source = sources[change.ItemId];
				db.HistoricalOperationItems.Add(new()
				{
					OperationId = applicationId,
					ItemId = change.ItemId,
					ReceiptId = source.ReceiptId,
					Store = source.Location,
					RawDescription = source.Description,
					ItemCode = source.ReceiptItemCode,
					ItemRevision = source.ItemRevision,
					ReceiptRevision = source.ReceiptRevision,
					ExpectedAssignmentId = change.ExpectedAssignmentId,
					PreviousCategoryId = change.PreviousCategoryId,
					TargetCategoryId = change.TargetCategoryId,
					ApplyOperationId = Guid.NewGuid(),
					UndoOperationId = Guid.NewGuid(),
					State = change.Conflict ? HistoricalItemState.Conflicted : HistoricalItemState.Pending,
					Reason = change.Conflict ? "classified-after-plan" : null,
					UndoState = HistoricalItemState.Pending
				});
				db.RestructureItems.Add(new()
				{
					ApplicationId = applicationId,
					ItemId = change.ItemId,
					Rule = change.Rule,
					Evidence = change.Evidence,
					PreviousOccasionId = change.PreviousOccasionId,
					TargetOccasionId = change.TargetOccasion is null ? null : occasions[change.TargetOccasion]
				});
			}
			await db.SaveChangesAsync(ct);
			db.ChangeTracker.Clear();
		}
	}

	private static async Task<RestructureState> LoadStateAsync(ApplicationDbContext db, Guid headRevision, Guid baseRevision, CancellationToken ct)
	{
		Dictionary<Guid, TaxonomyNode> current = await NodesAsync(db, headRevision, ct);
		Dictionary<Guid, TaxonomyNode>? original = await db.TaxonomyRevisions.AnyAsync(x => x.Id == baseRevision, ct)
			? await NodesAsync(db, baseRevision, ct) : null;
		List<RestructureItemState> items = await (from item in db.ReceiptItems.AsNoTracking().IgnoreAutoIncludes()
												  join receipt in db.Receipts.AsNoTracking() on item.ReceiptId equals receipt.Id
												  join pointer in db.CurrentClassifications.AsNoTracking() on item.Id equals pointer.ReceiptItemId
												  join assignment in db.Classifications.AsNoTracking() on pointer.AssignmentId equals assignment.Id
												  select new RestructureItemState(item.Id, assignment.Id, assignment.CategoryId, assignment.RecordedAt,
													  assignment.Source, item.OccasionId, item.EnvelopeId)).ToListAsync(ct);
		var links = await db.YnabEnvelopeMappings.AsNoTracking().Select(x => new { x.EnvelopeId, x.YnabBudgetId, x.YnabCategoryId }).ToListAsync(ct);
		List<RestructureEnvelopeState> envelopes = (await db.Envelopes.AsNoTracking().ToListAsync(ct))
			.Select(envelope => new RestructureEnvelopeState(envelope.Id, envelope.Name, envelope.IsActive,
				links.Where(link => link.EnvelopeId == envelope.Id).Select(link => new RestructureEnvelopeLink(link.YnabBudgetId, link.YnabCategoryId)).ToList()))
			.ToList();
		List<RestructureOccasionState> occasions = await db.Occasions.AsNoTracking()
			.Select(x => new RestructureOccasionState(x.Id, x.Name, x.IsActive)).ToListAsync(ct);
		Dictionary<Guid, Guid> defaults = await db.ItemTypeEnvelopeDefaults.AsNoTracking().ToDictionaryAsync(x => x.CategoryId, x => x.EnvelopeId, ct);
		List<RestructureMappingState> mappings = (await (from head in db.CategoryMappingHeads.AsNoTracking()
														 join revision in db.CategoryMappingRevisions.AsNoTracking() on head.RevisionId equals revision.Id
														 where revision.CategoryId != null
														 select revision).ToListAsync(ct))
			.Select(revision => new RestructureMappingState(new(revision.Location, revision.Description, revision.ItemCode),
				revision.SourceFingerprint, revision.Id, revision.CategoryId!.Value, revision.SemanticFingerprint)).ToList();
		List<RestructureReceiptState> paidFrom = await db.Receipts.AsNoTracking().Where(x => x.PaidFromEnvelopeId != null)
			.Select(x => new RestructureReceiptState(x.Id, x.PaidFromEnvelopeId!.Value)).ToListAsync(ct);
		return new(headRevision, [.. current.Values], original is null ? null : [.. original.Values], items, envelopes, occasions,
			defaults, mappings, paidFrom);
	}

	private static async Task<RestructureApplicationSummary> SummaryAsync(ApplicationDbContext db, RestructureApplicationEntity row, CancellationToken ct)
	{
		HistoricalOperationEntity operation = await db.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == row.Id, ct);
		return new(row.Id, row.PlanId, row.Reason, row.AppliedAt, row.UndoneAt, operation.State,
			await HistoricalReclassificationService.ProgressAsync(db, operation, ct),
			JsonSerializer.Deserialize<RestructureReport>(row.ReportJson)!);
	}

	private async Task PublishItemsAsync(HistoricalProgress progress, CancellationToken ct)
	{
		if (publisher is not null && progress.State is HistoricalOperationState.Completed or HistoricalOperationState.Undone)
		{ await publisher.PublishAsync(new(CommittedEntityType.ReceiptItem, CommittedChangeType.Updated, SuppressToast: true)); }
	}

	private static Task<TaxonomyHeadEntity> LockHeadAsync(ApplicationDbContext db, CancellationToken ct) =>
		db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE").SingleAsync(ct);

	private static Task<Dictionary<Guid, TaxonomyNode>> NodesAsync(ApplicationDbContext db, Guid revisionId, CancellationToken ct) =>
		db.TaxonomyNodes.AsNoTracking().Where(x => x.RevisionId == revisionId)
			.Select(x => new TaxonomyNode(x.CategoryId, x.ParentId, x.Name, x.Definition, x.Archived))
			.ToDictionaryAsync(x => x.Id, ct);

	private static Guid Derived(Guid applicationId, string purpose) =>
		new(SHA256.HashData(Encoding.UTF8.GetBytes($"restructure:{applicationId:D}:{purpose}")).AsSpan(0, 16));

	private static DateTimeOffset Truncate(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);
}
