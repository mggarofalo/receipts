using Application.Exceptions;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	private (Guid[] Required, Guid[] Locks) CollectReceiptAdmissions()
	{
		HashSet<Guid> required = [];
		HashSet<Guid> locks = [.. CollectPotentialDuplicateFactChanges()];
		foreach (EntityEntry entry in ChangeTracker.Entries())
		{
			if (entry.Entity is not IOwnedBy<ReceiptEntity> || entry.Entity is not ISoftDeletable child || child.DeletedAt is not null)
			{
				continue;
			}
			PropertyEntry parent = entry.Property("ReceiptId");
			bool admitted = entry.State == EntityState.Added || (entry.State == EntityState.Modified
				&& (parent.IsModified || entry.Property(nameof(ISoftDeletable.DeletedAt)).IsModified));
			if (!admitted)
			{
				continue;
			}
			Guid parentId = (Guid)parent.CurrentValue!;
			required.Add(parentId);
			locks.Add(parentId);
			if (entry.State != EntityState.Added)
			{
				locks.Add((Guid)parent.OriginalValue!);
			}
		}
		return ([.. required], [.. locks.Order()]);
	}

	private void EnsureReceiptAdmissions(Guid[] required, HashSet<Guid> active)
	{
		foreach (EntityEntry<ReceiptEntity> entry in ChangeTracker.Entries<ReceiptEntity>())
		{
			if (entry.State == EntityState.Added || entry.Property(receipt => receipt.DeletedAt).IsModified)
			{
				if (entry.Entity.DeletedAt is null)
				{
					active.Add(entry.Entity.Id);
				}
				else
				{
					active.Remove(entry.Entity.Id);
				}
			}
		}
		if (required.Any(id => !active.Contains(id)))
		{
			throw new ConcurrencyConflictException("The receipt is no longer active. Restore or reload it before adding or restoring its children.");
		}
	}

	private void GuardReceiptAdmissions()
	{
		if (Database.ProviderName != PostgreSQL)
		{
			return;
		}
		(Guid[] required, Guid[] locks) = CollectReceiptAdmissions();
		if (locks.Length == 0)
		{
			return;
		}
		ReceiptFactLock.Acquire(this, locks);
		if (required.Length > 0)
		{
			HashSet<Guid> active = Receipts.IgnoreAutoIncludes().AsNoTracking()
				.Where(receipt => required.Contains(receipt.Id)).Select(receipt => receipt.Id).ToHashSet();
			EnsureReceiptAdmissions(required, active);
		}
	}

	private async Task GuardReceiptAdmissionsAsync(CancellationToken cancellationToken)
	{
		if (Database.ProviderName != PostgreSQL)
		{
			return;
		}
		(Guid[] required, Guid[] locks) = CollectReceiptAdmissions();
		if (locks.Length == 0)
		{
			return;
		}
		await ReceiptFactLock.AcquireAsync(this, locks, cancellationToken).ConfigureAwait(false);
		if (required.Length > 0)
		{
			HashSet<Guid> active = await Receipts.IgnoreAutoIncludes().AsNoTracking()
				.Where(receipt => required.Contains(receipt.Id)).Select(receipt => receipt.Id)
				.ToHashSetAsync(cancellationToken).ConfigureAwait(false);
			EnsureReceiptAdmissions(required, active);
		}
	}
}
