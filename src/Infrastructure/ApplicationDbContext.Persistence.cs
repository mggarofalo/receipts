using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	public override int SaveChanges() => SaveChanges(acceptAllChangesOnSuccess: true);

	public override int SaveChanges(bool acceptAllChangesOnSuccess)
	{
		IDbContextTransaction? callerTransaction = GetSupportedCallerTransaction();
		using IDbContextTransaction? ownedTransaction = Database.IsRelational() && callerTransaction is null
			? Database.BeginTransaction()
			: null;
		string? savepoint = callerTransaction is null ? null : $"receipts_save_{Guid.NewGuid():N}";
		bool savepointCreated = false;
		List<AuditEntry> auditEntries = [];
		int result;
		bool descriptionsChanged;

		try
		{
			if (savepoint is not null)
			{
				callerTransaction!.CreateSavepoint(savepoint);
				savepointCreated = true;
			}

			HandleSoftDelete();
			auditEntries = PrepareAuditEntries();
			HashSet<string> touchedDescriptions = CollectTouchedReceiptItemDescriptions();
			AuditLogs.AddRange(auditEntries.Select(entry => entry.AuditLog));

			// Keep the caller's tracked changes pending until audit AND reconciliation succeed.
			result = base.SaveChanges(acceptAllChangesOnSuccess: false) - auditEntries.Count;
			descriptionsChanged = ReconcileDistinctDescriptions(touchedDescriptions);
			ownedTransaction?.Commit();
			if (savepointCreated)
			{
				callerTransaction!.ReleaseSavepoint(savepoint!);
			}
		}
		catch (Exception saveException)
		{
			DetachAutomaticAudits(auditEntries);
			try
			{
				ownedTransaction?.Rollback();
				if (savepointCreated)
				{
					callerTransaction!.RollbackToSavepoint(savepoint!);
				}
			}
			catch (Exception rollbackException)
			{
				throw new AggregateException("Save and rollback failed; discard the context and transaction.", saveException, rollbackException);
			}
			throw;
		}

		CompleteSave(acceptAllChangesOnSuccess, auditEntries, descriptionsChanged);
		return result;
	}

	public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
		=> SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);

	public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		IDbContextTransaction? callerTransaction = GetSupportedCallerTransaction();
		await using IDbContextTransaction? ownedTransaction = Database.IsRelational() && callerTransaction is null
			? await Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
			: null;
		string? savepoint = callerTransaction is null ? null : $"receipts_save_{Guid.NewGuid():N}";
		bool savepointCreated = false;
		List<AuditEntry> auditEntries = [];
		int result;
		bool descriptionsChanged;

		try
		{
			if (savepoint is not null)
			{
				await callerTransaction!.CreateSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
				savepointCreated = true;
			}

			HandleSoftDelete();
			auditEntries = PrepareAuditEntries();
			HashSet<string> touchedDescriptions = CollectTouchedReceiptItemDescriptions();
			AuditLogs.AddRange(auditEntries.Select(entry => entry.AuditLog));

			// Explicitly call the base Boolean overload; the token-only overload dispatches virtually.
			result = await base.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken).ConfigureAwait(false) - auditEntries.Count;
			descriptionsChanged = await ReconcileDistinctDescriptionsAsync(touchedDescriptions, cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			if (ownedTransaction is not null)
			{
				await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			}
			if (savepointCreated)
			{
				await callerTransaction!.ReleaseSavepointAsync(savepoint!, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (Exception saveException)
		{
			DetachAutomaticAudits(auditEntries);
			try
			{
				// The write may have been cancelled. Cleanup must still reach the database.
				if (ownedTransaction is not null)
				{
					await ownedTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
				}
				if (savepointCreated)
				{
					await callerTransaction!.RollbackToSavepointAsync(savepoint!, CancellationToken.None).ConfigureAwait(false);
				}
			}
			catch (Exception rollbackException)
			{
				throw new AggregateException("Save and rollback failed; discard the context and transaction.", saveException, rollbackException);
			}
			throw;
		}

		CompleteSave(acceptAllChangesOnSuccess, auditEntries, descriptionsChanged);
		return result;
	}

	private IDbContextTransaction? GetSupportedCallerTransaction()
	{
		if (!Database.IsRelational())
		{
			return null;
		}
		if (System.Transactions.Transaction.Current is not null || Database.GetEnlistedTransaction() is not null)
		{
			throw new NotSupportedException("Use an explicit DbContext transaction; ambient and enlisted transactions are not supported by the audited save pipeline.");
		}
		IDbContextTransaction? transaction = Database.CurrentTransaction;
		if (transaction is { SupportsSavepoints: false })
		{
			throw new NotSupportedException("An audited save inside a caller-owned transaction requires savepoint support.");
		}
		return transaction;
	}

	private List<AuditEntry> PrepareAuditEntries()
	{
		List<AuditEntry> entries = AuditingEnabled ? CollectAuditEntries() : [];
		foreach (AuditEntry entry in entries)
		{
			if (entry.TrackedEntry is not null)
			{
				PropertyEntry id = entry.TrackedEntry.Property("Id");
				if (id.IsTemporary || id.CurrentValue is null)
				{
					throw new NotSupportedException("Audited entities require an assigned, non-temporary ID before saving.");
				}
				entry.AuditLog.EntityId = id.CurrentValue.ToString()!;
			}
		}
		return entries;
	}

	private void DetachAutomaticAudits(List<AuditEntry> entries)
	{
		foreach (AuditEntry entry in entries)
		{
			Entry(entry.AuditLog).State = EntityState.Detached;
		}
	}

	private void CompleteSave(bool acceptAllChangesOnSuccess, List<AuditEntry> auditEntries, bool descriptionsChanged)
	{
		if (acceptAllChangesOnSuccess)
		{
			ChangeTracker.AcceptAllChanges();
		}
		else
		{
			// These are internal rows, not changes the caller requested to keep pending.
			DetachAutomaticAudits(auditEntries);
		}
		if (descriptionsChanged)
		{
			// A caller-owned outer transaction may still be pending; polling remains the backstop.
			_descriptionChangeSignal?.NotifyDirty();
		}
	}

	private const string InsertDistinctDescriptionsSql = """
		INSERT INTO "matching"."DistinctDescriptions" ("Description")
		SELECT d
		FROM unnest({0}::text[]) AS d
		WHERE EXISTS (SELECT 1 FROM "receipts"."ReceiptItems" AS ri WHERE ri."Description" = d AND ri."DeletedAt" IS NULL)
		ON CONFLICT ("Description") DO NOTHING;
		""";

	private const string DeleteDistinctDescriptionsSql = """
		DELETE FROM "matching"."DistinctDescriptions" AS dd
		WHERE dd."Description" = ANY({0}::text[])
		  AND NOT EXISTS (SELECT 1 FROM "receipts"."ReceiptItems" AS ri WHERE ri."Description" = dd."Description" AND ri."DeletedAt" IS NULL);
		""";

	private bool ReconcileDistinctDescriptions(HashSet<string> descriptions)
	{
		if (descriptions.Count == 0 || Database.ProviderName != PostgreSQL)
		{
			return false;
		}
		string[] descriptionArray = [.. descriptions];
		int inserted = Database.ExecuteSqlRaw(InsertDistinctDescriptionsSql, [descriptionArray]);
		int deleted = Database.ExecuteSqlRaw(DeleteDistinctDescriptionsSql, [descriptionArray]);
		return inserted > 0 || deleted > 0;
	}

	private async Task<bool> ReconcileDistinctDescriptionsAsync(HashSet<string> descriptions, CancellationToken cancellationToken)
	{
		if (descriptions.Count == 0 || Database.ProviderName != PostgreSQL)
		{
			return false;
		}
		string[] descriptionArray = [.. descriptions];
		int inserted = await Database.ExecuteSqlRawAsync(InsertDistinctDescriptionsSql, [descriptionArray], cancellationToken).ConfigureAwait(false);
		int deleted = await Database.ExecuteSqlRawAsync(DeleteDistinctDescriptionsSql, [descriptionArray], cancellationToken).ConfigureAwait(false);
		return inserted > 0 || deleted > 0;
	}
}
