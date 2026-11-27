using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Services;

/// <summary>Serializes portable backup imports into one database, locally and across API instances.</summary>
internal static class BackupImportLock
{
	// Arbitrary application-owned key. Keep stable so every deployed version contends on
	// the same lock during rolling upgrades from versions with image reconciliation.
	private const long AdvisoryLockKey = 0x52454345495054; // "RECEIPT"

	// The local gate queues imports before they hold a connection and transaction. It is keyed by
	// target database like the advisory lock it fronts, so an import waiting on one database never
	// holds back an import into another one sharing the process.
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> LocalWaiters = new(StringComparer.Ordinal);

	public static Task<IAsyncDisposable> AcquireLocalAsync(ApplicationDbContext context, CancellationToken cancellationToken) =>
		AcquireLocalAsync(DatabaseKey(context), cancellationToken);

	internal static async Task<IAsyncDisposable> AcquireLocalAsync(string database, CancellationToken cancellationToken)
	{
		SemaphoreSlim waiter = LocalWaiters.GetOrAdd(database, _ => new SemaphoreSlim(1, 1));
		await waiter.WaitAsync(cancellationToken);
		return new LocalLease(waiter);
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

	// Server and database identify the advisory lock's scope; credentials do not.
	// Other providers keep one process-wide gate.
	private static string DatabaseKey(ApplicationDbContext context)
	{
		if (!context.Database.IsNpgsql())
		{
			return "";
		}

		NpgsqlConnectionStringBuilder connection = new(context.Database.GetDbConnection().ConnectionString);
		return $"{connection.Host}:{connection.Port}/{connection.Database}";
	}

	private sealed class LocalLease(SemaphoreSlim waiter) : IAsyncDisposable
	{
		private int _released;

		public ValueTask DisposeAsync()
		{
			if (Interlocked.Exchange(ref _released, 1) == 0)
			{
				waiter.Release();
			}
			return ValueTask.CompletedTask;
		}
	}
}
