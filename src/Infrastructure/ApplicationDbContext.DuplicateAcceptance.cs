using Application.Duplicates;
using Application.Exceptions;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	private Guid[] CollectPotentialDuplicateFactChanges()
	{
		HashSet<Guid> receiptIds = [];
		foreach (EntityEntry<ReceiptEntity> entry in ChangeTracker.Entries<ReceiptEntity>())
		{
			if (entry.State == EntityState.Modified &&
				(entry.Property(receipt => receipt.Date).IsModified
				|| entry.Property(receipt => receipt.Location).IsModified
				|| entry.Property(receipt => receipt.DeletedAt).IsModified))
			{
				receiptIds.Add(entry.Entity.Id);
			}
		}
		foreach (EntityEntry<TransactionEntity> entry in ChangeTracker.Entries<TransactionEntity>())
		{
			if (entry.State is EntityState.Added or EntityState.Deleted ||
				(entry.State == EntityState.Modified &&
				(entry.Property(payment => payment.Amount).IsModified
				|| entry.Property(payment => payment.ReceiptId).IsModified
				|| entry.Property(payment => payment.DeletedAt).IsModified
				|| entry.Property(payment => payment.CascadeDeletedByParentId).IsModified)))
			{
				receiptIds.Add(entry.Entity.ReceiptId);
				if (entry.State != EntityState.Added)
				{
					receiptIds.Add(entry.Property(payment => payment.ReceiptId).OriginalValue);
				}
			}
		}
		return [.. receiptIds.Order()];
	}

	private IQueryable<DuplicateReceiptFacts> QueryDuplicateFacts(Guid[] receiptIds)
		=> Receipts.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
			.Where(receipt => receiptIds.Contains(receipt.Id))
			.Select(receipt => new DuplicateReceiptFacts(receipt.Id, receipt.Date, receipt.Location,
				Transactions.IgnoreQueryFilters().Where(payment => payment.ReceiptId == receipt.Id
					&& (payment.DeletedAt == null
						|| (receipt.DeletedAt != null && payment.CascadeDeletedByParentId == receipt.Id)))
					.Sum(payment => (decimal?)payment.Amount) ?? 0m));

	private List<DuplicateReceiptFacts> CaptureDuplicateFacts()
	{
		if (Database.ProviderName != PostgreSQL)
		{
			return [];
		}
		Guid[] ids = CollectPotentialDuplicateFactChanges();
		if (ids.Length == 0)
		{
			return [];
		}
		Dictionary<Guid, (Guid Original, Guid Current)> expectedParents = ExpectedDuplicatePaymentParents();
		if (expectedParents.Count > 0)
		{
			Guid[] paymentIds = [.. expectedParents.Keys];
			Dictionary<Guid, Guid> storedParents = Transactions.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
				.Where(payment => paymentIds.Contains(payment.Id))
				.Select(payment => new { payment.Id, payment.ReceiptId })
				.ToDictionary(payment => payment.Id, payment => payment.ReceiptId);
			EnsureDuplicatePaymentParentsUnchanged(expectedParents, storedParents);
		}
		return QueryDuplicateFacts(ids).ToList();
	}

	private async Task<List<DuplicateReceiptFacts>> CaptureDuplicateFactsAsync(CancellationToken cancellationToken)
	{
		if (Database.ProviderName != PostgreSQL)
		{
			return [];
		}
		Guid[] ids = CollectPotentialDuplicateFactChanges();
		if (ids.Length == 0)
		{
			return [];
		}
		Dictionary<Guid, (Guid Original, Guid Current)> expectedParents = ExpectedDuplicatePaymentParents();
		if (expectedParents.Count > 0)
		{
			Guid[] paymentIds = [.. expectedParents.Keys];
			Dictionary<Guid, Guid> storedParents = await Transactions.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
				.Where(payment => paymentIds.Contains(payment.Id))
				.Select(payment => new { payment.Id, payment.ReceiptId })
				.ToDictionaryAsync(payment => payment.Id, payment => payment.ReceiptId, cancellationToken).ConfigureAwait(false);
			EnsureDuplicatePaymentParentsUnchanged(expectedParents, storedParents);
		}
		return await QueryDuplicateFacts(ids).ToListAsync(cancellationToken).ConfigureAwait(false);
	}

	private Dictionary<Guid, (Guid Original, Guid Current)> ExpectedDuplicatePaymentParents()
		=> ChangeTracker.Entries<TransactionEntity>()
			.Where(entry => entry.State is EntityState.Modified or EntityState.Deleted)
			.ToDictionary(entry => entry.Entity.Id, entry => (entry.Property(payment => payment.ReceiptId).OriginalValue, entry.Entity.ReceiptId));

	private static void EnsureDuplicatePaymentParentsUnchanged(Dictionary<Guid, (Guid Original, Guid Current)> expected, Dictionary<Guid, Guid> stored)
	{
		if (stored.Any(parent => expected[parent.Key].Original != parent.Value && expected[parent.Key].Current != parent.Value))
		{
			// The actual parent is outside the locked snapshot. Reject instead of changing its
			// total without invalidating its acceptances. Tracked, current reparenting is valid.
			throw new ConcurrencyConflictException("A payment moved to another receipt. Reload it before saving.");
		}
	}

	private IQueryable<AcceptedDuplicatePairEntity> ChangedDuplicateAcceptances(
		List<DuplicateReceiptFacts> before, List<DuplicateReceiptFacts> after)
	{
		Dictionary<Guid, DuplicateReceiptFacts> prior = before.ToDictionary(facts => facts.ReceiptId);
		Guid[] changedIds = [.. after.Where(facts => prior[facts.ReceiptId] != facts).Select(facts => facts.ReceiptId)];
		return AcceptedDuplicatePairs.Where(pair => changedIds.Contains(pair.ReceiptIdA) || changedIds.Contains(pair.ReceiptIdB));
	}

	private void InvalidateChangedDuplicateAcceptances(List<DuplicateReceiptFacts> before)
	{
		if (before.Count == 0)
		{
			return;
		}
		List<DuplicateReceiptFacts> after = QueryDuplicateFacts([.. before.Select(facts => facts.ReceiptId)]).ToList();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		ChangedDuplicateAcceptances(before, after).ExecuteUpdate(setters => setters
			.SetProperty(pair => pair.DeletedAt, now)
			.SetProperty(pair => pair.DeletedByUserId, _currentUserAccessor == null ? null : _currentUserAccessor.UserId)
			.SetProperty(pair => pair.DeletedByApiKeyId, _currentUserAccessor == null ? null : _currentUserAccessor.ApiKeyId));
	}

	private async Task InvalidateChangedDuplicateAcceptancesAsync(List<DuplicateReceiptFacts> before, CancellationToken cancellationToken)
	{
		if (before.Count == 0)
		{
			return;
		}
		List<DuplicateReceiptFacts> after = await QueryDuplicateFacts([.. before.Select(facts => facts.ReceiptId)])
			.ToListAsync(cancellationToken).ConfigureAwait(false);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		await ChangedDuplicateAcceptances(before, after).ExecuteUpdateAsync(setters => setters
			.SetProperty(pair => pair.DeletedAt, now)
			.SetProperty(pair => pair.DeletedByUserId, _currentUserAccessor == null ? null : _currentUserAccessor.UserId)
			.SetProperty(pair => pair.DeletedByApiKeyId, _currentUserAccessor == null ? null : _currentUserAccessor.ApiKeyId), cancellationToken)
			.ConfigureAwait(false);
	}
}
