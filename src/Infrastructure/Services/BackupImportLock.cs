using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>Serializes portable backup imports locally and across API instances.</summary>
internal static class BackupImportLock
{
	// Arbitrary application-owned key. Keep stable so every deployed version contends on
	// the same lock during rolling upgrades from versions with image reconciliation.
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
