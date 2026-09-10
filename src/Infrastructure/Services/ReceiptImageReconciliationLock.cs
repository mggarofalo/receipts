using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>
/// Serializes operations that can restore historical image paths with the filesystem
/// reconciler. The transaction-scoped PostgreSQL advisory lock also coordinates across
/// API instances and is released automatically on commit, rollback, or connection loss.
/// </summary>
internal static class ReceiptImageReconciliationLock
{
	// Arbitrary application-owned key. Keep stable so every deployed version contends on
	// the same lock while backup imports and image reconciliation overlap.
	private const long AdvisoryLockKey = 0x52454345495054; // "RECEIPT"
	private static readonly SemaphoreSlim LocalWaiter = new(1, 1);

	public static async Task<IAsyncDisposable> AcquireLocalAsync(CancellationToken cancellationToken)
	{
		await LocalWaiter.WaitAsync(cancellationToken);
		return LocalLease.Instance;
	}

	public static async Task AcquireDatabaseAsync(
		ApplicationDbContext context,
		CancellationToken cancellationToken)
	{
		if (context.Database.IsNpgsql())
		{
			await context.Database.ExecuteSqlInterpolatedAsync(
				$"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", cancellationToken);
		}
	}

	private sealed class LocalLease : IAsyncDisposable
	{
		public static readonly LocalLease Instance = new();

		public ValueTask DisposeAsync()
		{
			LocalWaiter.Release();
			return ValueTask.CompletedTask;
		}
	}
}
