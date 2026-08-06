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

		(bool needsWinner, Guid? winnerAccountId) =
			ResolveMappingWinner(targetAccountId, mappings, ynabMappingWinnerAccountId);
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
			await using IDbContextTransaction dbTransaction = await context.Database.BeginTransactionAsync(cancellationToken);

			// Phase 1: repoint dependents + repoint/replace mapping + write audit. No account deletes yet.
			// IgnoreQueryFilters so soft-deleted (trashed) transactions repoint too — otherwise the
			// Phase-2 account delete would strand them and (pre-fix) cascade-destroy them.
			List<TransactionEntity> transactionsToMove = await context.Transactions
				.IgnoreQueryFilters()
				.IgnoreAutoIncludes()
				.Where(t => sourceAccountIds.Contains(t.AccountId))
				.ToListAsync(cancellationToken);
			Dictionary<Guid, int> transactionCountBySource = transactionsToMove
				.GroupBy(t => t.AccountId)
				.ToDictionary(g => g.Key, g => g.Count());
			foreach (TransactionEntity transaction in transactionsToMove)
			{
				transaction.AccountId = targetAccountId;
			}
			movedTransactionCount = transactionsToMove.Count;

			List<CardEntity> sourceCards = await context.Cards
				.Where(c => distinctCardIds.Contains(c.Id))
				.ToListAsync(cancellationToken);
			foreach (CardEntity card in sourceCards)
			{
				card.AccountId = targetAccountId;
			}

			// Remove any source mappings that are NOT the winner. We explicitly delete them
			// rather than relying on cascade from account deletion (EF's InMemory provider
			// does not replay cascades for store-resident rows the change tracker never saw).
			Guid? keptMappingId = winnerAccountId;
			List<YnabAccountMappingEntity> mappingsToDelete = await context.YnabAccountMappings
				.Where(m => sourceAccountIds.Contains(m.ReceiptsAccountId)
					&& (!keptMappingId.HasValue || m.ReceiptsAccountId != keptMappingId.Value))
				.ToListAsync(cancellationToken);
			context.YnabAccountMappings.RemoveRange(mappingsToDelete);

			if (winnerAccountId.HasValue && winnerAccountId.Value != targetAccountId)
			{
				YnabAccountMappingEntity? existingTarget = await context.YnabAccountMappings
					.FirstOrDefaultAsync(m => m.ReceiptsAccountId == targetAccountId, cancellationToken);
				if (existingTarget is not null)
				{
					context.YnabAccountMappings.Remove(existingTarget);
				}

				YnabAccountMappingEntity winner = await context.YnabAccountMappings
					.FirstAsync(m => m.ReceiptsAccountId == winnerAccountId.Value, cancellationToken);
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
			// With Transactions.AccountId now Restrict, this DELETE succeeds only because every
			// transaction (active and soft-deleted) was repointed to the target above.
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
		Guid targetAccountId,
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

		(bool needsWinner, Guid? winnerAccountId) =
			ResolveMappingWinner(targetAccountId, mappings, ynabMappingWinnerAccountId);
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
			.CountAsync(t => sourceAccountIds.Contains(t.AccountId) && t.DeletedAt == null, cancellationToken);
		int trashedTransactionsToRepoint = await context.Transactions
			.IgnoreQueryFilters()
			.IgnoreAutoIncludes()
			.CountAsync(t => sourceAccountIds.Contains(t.AccountId) && t.DeletedAt != null, cancellationToken);

		List<MergeCardsPreviewAccount> accountsToRemove =
		[
			.. sourceAccountIds.Select(id =>
				new MergeCardsPreviewAccount(id, accountNamesById.GetValueOrDefault(id, "")))
		];

		// Only worth reporting when a mapping actually changes hands. One already sitting on
		// the target survives by staying put, which is not news.
		MergeCardsPreviewMapping? survivingMapping = null;
		if (winnerAccountId.HasValue && winnerAccountId.Value != targetAccountId)
		{
			YnabAccountMappingEntity winner = mappings.First(m => m.ReceiptsAccountId == winnerAccountId.Value);
			survivingMapping = new MergeCardsPreviewMapping(
				winnerAccountId.Value,
				accountNamesById.GetValueOrDefault(winnerAccountId.Value, ""),
				winner.YnabAccountName);
		}

		return new MergeCardsPreview(
			accountsToRemove,
			originalCardAccountIds.Count(kvp => kvp.Value != targetAccountId),
			transactionsToRepoint,
			trashedTransactionsToRepoint,
			survivingMapping,
			null);
	}

	/// <summary>
	/// Decides which YNAB mapping survives, or reports that the caller must choose.
	/// Shared by the merge and its preview so the two can never disagree about which
	/// selections need a decision.
	/// </summary>
	private static (bool NeedsWinner, Guid? WinnerAccountId) ResolveMappingWinner(
		Guid targetAccountId,
		List<YnabAccountMappingEntity> mappings,
		Guid? ynabMappingWinnerAccountId)
	{
		int distinctMappingTuples = mappings
			.Select(m => (m.YnabBudgetId, m.YnabAccountId))
			.Distinct()
			.Count();

		if (distinctMappingTuples > 1)
		{
			if (!ynabMappingWinnerAccountId.HasValue)
			{
				return (true, null);
			}

			if (!mappings.Any(m => m.ReceiptsAccountId == ynabMappingWinnerAccountId.Value))
			{
				throw new ArgumentException(InvalidWinnerAccount, nameof(ynabMappingWinnerAccountId));
			}

			return (false, ynabMappingWinnerAccountId.Value);
		}

		if (mappings.Count > 0)
		{
			// No conflict; keep the target mapping if it exists, else promote the sole source mapping.
			return (false, mappings.Any(m => m.ReceiptsAccountId == targetAccountId)
				? targetAccountId
				: mappings[0].ReceiptsAccountId);
		}

		return (false, null);
	}

	private async Task<(List<Guid> SourceAccountIds, List<YnabAccountMappingEntity> Mappings, Dictionary<Guid, string> AccountNamesById, Dictionary<Guid, Guid> OriginalCardAccountIds)> LoadStateAsync(
		Guid targetAccountId,
		List<Guid> distinctCardIds,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();

		bool targetExists = await context.Accounts
			.AsNoTracking()
			.AnyAsync(a => a.Id == targetAccountId, cancellationToken);
		if (!targetExists)
		{
			throw new KeyNotFoundException(TargetAccountNotFound);
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
			.Where(id => id != targetAccountId)
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

		List<Guid> allAccountIds = [.. sourceAccountIds, targetAccountId];

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
		[.. mappings.Select(m => new YnabMappingConflict(
			m.ReceiptsAccountId,
			accountNamesById.GetValueOrDefault(m.ReceiptsAccountId, ""),
			m.YnabBudgetId,
			m.YnabAccountId,
			m.YnabAccountName))];
}
