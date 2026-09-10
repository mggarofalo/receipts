using System.Data;
using Application.Interfaces.Services;
using Application.Models.Merge;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Services;

public class AccountMergeService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	ICurrentUserAccessor currentUserAccessor) : IAccountMergeService
{
	public const string AtLeastOneCardRequired = "Merge requires at least one source card.";
	public const string TargetAccountNotFound = "Target account not found.";
	public const string SourceCardNotFound = "One or more source cards not found.";
	public const string InvalidWinnerAccount = "Winner account id must match one of the accounts involved in the merge.";
	public const string PartialSourceAccountMerge = "Source account would be partially merged: all of its cards must be included in the merge, or none.";
	public const string MultipleYnabBudgetConflicts = "Merge cannot resolve conflicting mappings in multiple YNAB budgets at once. Delete obsolete mappings until only one budget has a conflict, then retry.";

	public async Task<MergeCardsResult> MergeCardsAsync(
		Guid targetAccountId,
		IReadOnlyList<Guid> sourceCardIds,
		Guid? ynabMappingWinnerAccountId,
		CancellationToken cancellationToken)
	{
		// One card is enough. The old rule demanded two, which made the most ordinary merge
		// of all — "account B has a single card, fold it into account A" — impossible: the
		// user had to also tick unrelated cards that already sat on the target just to get
		// past a count check (RECEIPTS-887). The count was never what mattered. The merge
		// only ever moves cards whose account differs from the target, and LoadStateAsync
		// already rejects a selection that would leave siblings behind on a source account.
		if (sourceCardIds is null || sourceCardIds.Count == 0)
		{
			throw new ArgumentException(AtLeastOneCardRequired, nameof(sourceCardIds));
		}

		List<Guid> distinctCardIds = [.. sourceCardIds.Distinct()];

		// Phase 0: validate + detect conflicts using read-only snapshot.
		(List<Guid> sourceAccountIds, List<YnabAccountMappingEntity> mappings, Dictionary<Guid, string> accountNamesById, Dictionary<Guid, Guid> originalCardAccountIds) =
			await LoadStateAsync(targetAccountId, distinctCardIds, cancellationToken);

		if (sourceAccountIds.Count == 0)
		{
			// No-op: all cards already belong to the target account. Idempotent and correct,
			// but the caller must be able to tell it apart from a merge that moved data —
			// hence a zeroed result rather than a bare success (RECEIPTS-893).
			return MergeCardsResult.NoOp();
		}

		(bool needsWinner, HashSet<Guid> winnerMappingIds) =
			ResolveMappingWinners(targetAccountId, mappings, ynabMappingWinnerAccountId);
		if (needsWinner)
		{
			return BuildConflictResult(mappings, accountNamesById);
		}

		// Counted from the pre-merge snapshot, not from the cards we assign below: Phase 1
		// assigns the target account to every listed card, including ones already sitting on
		// it, so the assignment count would overstate what actually moved.
		int cardsMoved = originalCardAccountIds.Count(kvp => kvp.Value != targetAccountId);

		// Phases 1 and 2 run inside ONE transaction so a Phase-2 failure (e.g. the Restrict
		// AccountId FK rejecting an account delete) rolls back the Phase-1 repointing —
		// there is no half-applied merge.
		int movedTransactionCount;
		int removedAccountCount;
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await using IDbContextTransaction dbTransaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

			// Refresh mapping ownership inside the serializable transaction. The preview
			// snapshot above is intentionally read-only; it must not authorize deletion of
			// a target mapping created concurrently before this transaction began.
			List<Guid> involvedAccountIds = [.. sourceAccountIds, targetAccountId];
			List<YnabAccountMappingEntity> currentMappings = await context.YnabAccountMappings
				.AsNoTracking()
				.Where(m => involvedAccountIds.Contains(m.ReceiptsAccountId))
				.ToListAsync(cancellationToken);
			(needsWinner, winnerMappingIds) = ResolveMappingWinners(
				targetAccountId,
				currentMappings,
				ynabMappingWinnerAccountId);
			if (needsWinner)
			{
				return BuildConflictResult(currentMappings, accountNamesById);
			}

			// Phase 1: repoint dependents + repoint/replace mapping + write audit. No account deletes yet.
			// History follows the cards, including trashed transactions. Capture semantic
			// audit counts before moving the cards, without rewriting transaction rows.
			Dictionary<Guid, int> transactionCountBySource = await context.Transactions
				.IgnoreQueryFilters()
				.IgnoreAutoIncludes()
				.Where(transaction => sourceAccountIds.Contains(transaction.Card!.AccountId))
				.GroupBy(transaction => transaction.Card!.AccountId)
				.Select(group => new { AccountId = group.Key, Count = group.Count() })
				.ToDictionaryAsync(group => group.AccountId, group => group.Count, cancellationToken);
			movedTransactionCount = transactionCountBySource.Values.Sum();

			List<CardEntity> sourceCards = await context.Cards
				.Where(c => distinctCardIds.Contains(c.Id))
				.ToListAsync(cancellationToken);
			foreach (CardEntity card in sourceCards)
			{
				card.AccountId = targetAccountId;
			}

			// Reconcile mappings independently per budget. A merged account may own one
			// mapping for every destination, so a winner in budget A must never discard an
			// unrelated mapping in budget B.
			//
			// Remove mappings that are not their budget's winner explicitly rather than
			// relying on cascade from account deletion (EF's InMemory provider
			// does not replay cascades for store-resident rows the change tracker never saw).
			HashSet<Guid> loserMappingIds = [.. currentMappings
				.Where(m => !winnerMappingIds.Contains(m.Id))
				.Select(m => m.Id)];
			List<YnabAccountMappingEntity> mappingsToDelete = await context.YnabAccountMappings
				.Where(m => loserMappingIds.Contains(m.Id))
				.ToListAsync(cancellationToken);
			context.YnabAccountMappings.RemoveRange(mappingsToDelete);

			List<YnabAccountMappingEntity> sourceWinners = await context.YnabAccountMappings
				.Where(m => winnerMappingIds.Contains(m.Id) && m.ReceiptsAccountId != targetAccountId)
				.ToListAsync(cancellationToken);
			foreach (YnabAccountMappingEntity winner in sourceWinners)
			{
				winner.ReceiptsAccountId = targetAccountId;
				winner.UpdatedAt = DateTimeOffset.UtcNow;
			}

			DateTimeOffset now = DateTimeOffset.UtcNow;
			List<AuditLogEntity> mergeEntries = [];
			foreach (Guid sourceAccountId in sourceAccountIds)
			{
				List<Guid> cardsMovedFromThisAccount = [.. originalCardAccountIds
					.Where(kvp => kvp.Value == sourceAccountId)
					.Select(kvp => kvp.Key)];
				int movedFromThisSource = transactionCountBySource.GetValueOrDefault(sourceAccountId, 0);
				mergeEntries.Add(CreateMergeAuditEntry(
					sourceAccountId,
					[
						new FieldChange { FieldName = "mergedIntoAccountId", OldValue = null, NewValue = targetAccountId.ToString() },
						new FieldChange { FieldName = "mergedCardIds", OldValue = null, NewValue = string.Join(", ", cardsMovedFromThisAccount) },
						new FieldChange { FieldName = "movedTransactionCount", OldValue = null, NewValue = movedFromThisSource.ToString() },
					],
					now));
			}

			mergeEntries.Add(CreateMergeAuditEntry(
				targetAccountId,
				[
					new FieldChange { FieldName = "mergedFromAccountIds", OldValue = null, NewValue = string.Join(", ", sourceAccountIds) },
					new FieldChange { FieldName = "mergedCardIds", OldValue = null, NewValue = string.Join(", ", distinctCardIds) },
					new FieldChange { FieldName = "movedTransactionCount", OldValue = null, NewValue = movedTransactionCount.ToString() },
				],
				now));

			context.AuditLogs.AddRange(mergeEntries);
			await context.SaveChangesAsync(cancellationToken);

			// Phase 2: delete the now-orphaned source accounts in the SAME context/transaction.
			// Every source card now belongs to the target, so active and trashed
			// transaction history follows without retaining a source-account FK.
			List<AccountEntity> orphanedAccounts = await context.Accounts
				.Where(a => sourceAccountIds.Contains(a.Id))
				.ToListAsync(cancellationToken);
			context.Accounts.RemoveRange(orphanedAccounts);
			removedAccountCount = orphanedAccounts.Count;
			await context.SaveChangesAsync(cancellationToken);

			await dbTransaction.CommitAsync(cancellationToken);
		}

		return new MergeCardsResult(removedAccountCount, cardsMoved, movedTransactionCount, null);
	}

	public async Task<MergeCardsPreview> PreviewMergeCardsAsync(
		Guid? targetAccountId,
		IReadOnlyList<Guid> sourceCardIds,
		Guid? ynabMappingWinnerAccountId,
		CancellationToken cancellationToken)
	{
		if (sourceCardIds is null || sourceCardIds.Count == 0)
		{
			throw new ArgumentException(AtLeastOneCardRequired, nameof(sourceCardIds));
		}

		List<Guid> distinctCardIds = [.. sourceCardIds.Distinct()];

		// The same Phase 0 the merge runs, so a request the merge would reject is rejected
		// here too. A preview that answered where the merge would throw would be worse than
		// no preview: it would promise an outcome that cannot be delivered.
		(List<Guid> sourceAccountIds, List<YnabAccountMappingEntity> mappings, Dictionary<Guid, string> accountNamesById, Dictionary<Guid, Guid> originalCardAccountIds) =
			await LoadStateAsync(targetAccountId, distinctCardIds, cancellationToken);

		if (sourceAccountIds.Count == 0)
		{
			return MergeCardsPreview.NoOp();
		}

		(bool needsWinner, HashSet<Guid> winnerMappingIds) =
			ResolveMappingWinners(targetAccountId, mappings, ynabMappingWinnerAccountId);
		if (needsWinner)
		{
			return MergeCardsPreview.Conflicted(BuildConflicts(mappings, accountNamesById));
		}

		using ApplicationDbContext context = contextFactory.CreateDbContext();

		// Counted separately because the trashed ones are the whole point: the merge repoints
		// soft-deleted transactions too, and a preview that quietly folded them into one total
		// would understate what a supposedly reversible-looking bin still holds (RECEIPTS-889).
		int transactionsToRepoint = await context.Transactions
			.IgnoreQueryFilters()
			.IgnoreAutoIncludes()
			.CountAsync(t => sourceAccountIds.Contains(t.Card!.AccountId) && t.DeletedAt == null, cancellationToken);
		int trashedTransactionsToRepoint = await context.Transactions
			.IgnoreQueryFilters()
			.IgnoreAutoIncludes()
			.CountAsync(t => sourceAccountIds.Contains(t.Card!.AccountId) && t.DeletedAt != null, cancellationToken);

		List<MergeCardsPreviewAccount> accountsToRemove =
		[
			.. sourceAccountIds.Select(id =>
				new MergeCardsPreviewAccount(id, accountNamesById.GetValueOrDefault(id, "")))
		];

		// Only worth reporting when a mapping actually changes hands. One already sitting on
		// the target survives by staying put, which is not news.
		MergeCardsPreviewMapping? survivingMapping = null;
		List<YnabAccountMappingEntity> movedWinners =
		[
			.. mappings.Where(m => winnerMappingIds.Contains(m.Id)
				&& (!targetAccountId.HasValue || m.ReceiptsAccountId != targetAccountId.Value)),
		];
		if (movedWinners.Count == 1)
		{
			YnabAccountMappingEntity winner = movedWinners[0];
			survivingMapping = new MergeCardsPreviewMapping(
				winner.ReceiptsAccountId,
				accountNamesById.GetValueOrDefault(winner.ReceiptsAccountId, ""),
				winner.YnabAccountName);
		}

		return new MergeCardsPreview(
			accountsToRemove,
			originalCardAccountIds.Count(kvp => kvp.Value != targetAccountId),
			transactionsToRepoint,
			trashedTransactionsToRepoint,
			movedWinners.Count,
			survivingMapping,
			null);
	}

	/// <summary>
	/// Decides which YNAB mapping survives, or reports that the caller must choose.
	/// Shared by the merge and its preview so the two can never disagree about which
	/// selections need a decision.
	/// </summary>
	private static (bool NeedsWinner, HashSet<Guid> WinnerMappingIds) ResolveMappingWinners(
		Guid? targetAccountId,
		List<YnabAccountMappingEntity> mappings,
		Guid? ynabMappingWinnerAccountId)
	{
		HashSet<Guid> winners = [];
		List<IGrouping<string, YnabAccountMappingEntity>> budgetGroups =
			[.. mappings.GroupBy(m => m.YnabBudgetId, StringComparer.Ordinal)];
		int conflictingBudgetCount = budgetGroups.Count(group =>
			group.Select(m => m.YnabAccountId).Distinct(StringComparer.Ordinal).Count() > 1);
		if (conflictingBudgetCount > 1)
		{
			throw new ArgumentException(MultipleYnabBudgetConflicts, nameof(ynabMappingWinnerAccountId));
		}

		foreach (IGrouping<string, YnabAccountMappingEntity> budgetMappings in budgetGroups)
		{
			List<YnabAccountMappingEntity> candidates = [.. budgetMappings];
			if (candidates.Select(m => m.YnabAccountId).Distinct(StringComparer.Ordinal).Count() > 1)
			{
				if (!ynabMappingWinnerAccountId.HasValue)
				{
					return (true, []);
				}

				YnabAccountMappingEntity? selected = candidates.FirstOrDefault(
					m => m.ReceiptsAccountId == ynabMappingWinnerAccountId.Value);
				if (selected is null)
				{
					throw new ArgumentException(InvalidWinnerAccount, nameof(ynabMappingWinnerAccountId));
				}

				winners.Add(selected.Id);
				continue;
			}

			YnabAccountMappingEntity winner = targetAccountId.HasValue
				? candidates.FirstOrDefault(m => m.ReceiptsAccountId == targetAccountId.Value) ?? candidates[0]
				: candidates[0];
			winners.Add(winner.Id);
		}

		return (false, winners);
	}

	/// <summary>
	/// Phase 0: read-only validation and the state both the merge and its preview need.
	/// </summary>
	/// <param name="targetAccountId">
	/// Null means "an account that does not exist yet", which only the preview passes. The
	/// merge dialog's "New account" mode has to validate a selection *before* creating the
	/// account, or a rejected merge strands an empty one nobody can find (RECEIPTS-902). A
	/// hypothetical target holds no cards, so every selected card counts as a source — which
	/// is what an empty new account would mean anyway.
	/// </param>
	private async Task<(List<Guid> SourceAccountIds, List<YnabAccountMappingEntity> Mappings, Dictionary<Guid, string> AccountNamesById, Dictionary<Guid, Guid> OriginalCardAccountIds)> LoadStateAsync(
		Guid? targetAccountId,
		List<Guid> distinctCardIds,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();

		if (targetAccountId.HasValue)
		{
			bool targetExists = await context.Accounts
				.AsNoTracking()
				.AnyAsync(a => a.Id == targetAccountId.Value, cancellationToken);
			if (!targetExists)
			{
				throw new KeyNotFoundException(TargetAccountNotFound);
			}
		}

		List<CardEntity> sourceCards = await context.Cards
			.AsNoTracking()
			.Where(c => distinctCardIds.Contains(c.Id))
			.ToListAsync(cancellationToken);

		if (sourceCards.Count != distinctCardIds.Count)
		{
			throw new KeyNotFoundException(SourceCardNotFound);
		}

		Dictionary<Guid, Guid> originalCardAccountIds = sourceCards
			.ToDictionary(c => c.Id, c => c.AccountId);

		List<Guid> sourceAccountIds = [.. originalCardAccountIds.Values
			.Where(id => !targetAccountId.HasValue || id != targetAccountId.Value)
			.Distinct()];

		// Ensure each source account is being fully merged. Partial merges (leaving behind
		// cards on a source account that is about to be deleted) would silently orphan the
		// remaining cards and reassign their unrelated transactions — reject instead.
		if (sourceAccountIds.Count > 0)
		{
			HashSet<Guid> mergedCardIdSet = [.. distinctCardIds];
			int cardsLeftBehindCount = await context.Cards
				.AsNoTracking()
				.CountAsync(c => sourceAccountIds.Contains(c.AccountId)
					&& !mergedCardIdSet.Contains(c.Id),
					cancellationToken);
			if (cardsLeftBehindCount > 0)
			{
				throw new ArgumentException(PartialSourceAccountMerge, nameof(distinctCardIds));
			}
		}

		List<Guid> allAccountIds = targetAccountId.HasValue
			? [.. sourceAccountIds, targetAccountId.Value]
			: [.. sourceAccountIds];

		List<YnabAccountMappingEntity> mappings = await context.YnabAccountMappings
			.AsNoTracking()
			.Where(m => allAccountIds.Contains(m.ReceiptsAccountId))
			.ToListAsync(cancellationToken);

		Dictionary<Guid, string> accountNamesById = await context.Accounts
			.AsNoTracking()
			.Where(a => allAccountIds.Contains(a.Id))
			.ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);

		return (sourceAccountIds, mappings, accountNamesById, originalCardAccountIds);
	}

	// RECEIPTS-890: these entries previously serialized an anonymous object into ChangesJson. The
	// audit page parses ChangesJson as a FieldChange array and renders nothing for any other shape,
	// so every account-merge entry showed a row with an empty detail panel — the information was
	// recorded but unreadable anywhere except a raw CSV export. Emitting the same FieldChange shape
	// the automatic auditor uses makes the detail render with no client change.
	private AuditLogEntity CreateMergeAuditEntry(Guid accountId, List<FieldChange> changes, DateTimeOffset now)
	{
		AuditLogEntity auditLog = new()
		{
			Id = Guid.NewGuid(),
			EntityType = "Account",
			EntityId = accountId.ToString(),
			Action = AuditAction.Merge,
			ChangedByUserId = currentUserAccessor.UserId,
			ChangedByApiKeyId = currentUserAccessor.ApiKeyId,
			ChangedAt = now,
			IpAddress = currentUserAccessor.IpAddress,
		};
		auditLog.SetChanges(changes);
		return auditLog;
	}

	private static MergeCardsResult BuildConflictResult(
		List<YnabAccountMappingEntity> mappings,
		Dictionary<Guid, string> accountNamesById) =>
		MergeCardsResult.Conflicted(BuildConflicts(mappings, accountNamesById));

	private static List<YnabMappingConflict> BuildConflicts(
		List<YnabAccountMappingEntity> mappings,
		Dictionary<Guid, string> accountNamesById) =>
		[.. mappings
			.GroupBy(m => m.YnabBudgetId, StringComparer.Ordinal)
			.Where(group => group.Select(m => m.YnabAccountId).Distinct(StringComparer.Ordinal).Count() > 1)
			.SelectMany(group => group)
			.Select(m => new YnabMappingConflict(
			m.ReceiptsAccountId,
			accountNamesById.GetValueOrDefault(m.ReceiptsAccountId, ""),
			m.YnabBudgetId,
			m.YnabAccountId,
			m.YnabAccountName))];
}
