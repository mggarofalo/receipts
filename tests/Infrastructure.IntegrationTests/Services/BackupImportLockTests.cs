using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class BackupImportLockTests
{
	[Fact]
	public async Task Import_WaitsForHistoricalCrossInstanceLock_CancelsWithoutWriting_AndRetriesAfterRelease()
	{
		PostgresFixture isolated = new();
		await isolated.InitializeAsync();
		Guid receiptId = Guid.NewGuid();
		string path = Path.Combine(Path.GetTempPath(), $"backup-lock-{Guid.NewGuid():N}.db");
		try
		{
			await using (SqliteConnection backup = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
			{
				await backup.OpenAsync();
				await BackupService.CreateSchemaAsync(backup, CancellationToken.None);
				await using SqliteCommand seed = backup.CreateCommand();
				seed.CommandText = "INSERT INTO backup_metadata VALUES ('export_version', '6'); INSERT INTO receipts VALUES ($id, 'Archived store', '2024-01-01', '0', 'USD', 'historical/original.jpg', 'historical/processed.png');";
				seed.Parameters.AddWithValue("$id", receiptId.ToString());
				await seed.ExecuteNonQueryAsync();
			}
			await using NpgsqlConnection legacyInstance = new(isolated.ConnectionString);
			await legacyInstance.OpenAsync();
			await using NpgsqlTransaction legacyTransaction = await legacyInstance.BeginTransactionAsync();
			// Historical literal, independent of the renamed helper: upgrades must retain this cross-instance lock.
			await using NpgsqlCommand acquire = new("SELECT pg_advisory_xact_lock(23157103317831764)", legacyInstance, legacyTransaction);
			await acquire.ExecuteNonQueryAsync();
			BackupImportService service = new(new ContextFactory(isolated), NullLogger<BackupImportService>.Instance);
			using CancellationTokenSource cancellation = new();
			await using FileStream firstStream = File.OpenRead(path);
			Task import = service.ImportFromSqliteAsync(firstStream, cancellation.Token);
			await using NpgsqlCommand waiting = new("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND pg_backend_pid() = ANY(pg_blocking_pids(pid)))", legacyInstance, legacyTransaction);
			bool observedWait = false;
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
			while (!import.IsCompleted && !observedWait)
			{
				observedWait = (bool)(await waiting.ExecuteScalarAsync(timeout.Token))!;
				if (!observedWait)
				{
					await Task.Delay(20, timeout.Token);
				}
			}
			observedWait.Should().BeTrue("the actual import must wait on the legacy advisory lock before applying rows");
			cancellation.Cancel();
			await FluentActions.Awaiting(async () => await import).Should().ThrowAsync<OperationCanceledException>();
			await using (ApplicationDbContext verify = isolated.CreateDbContext())
			{
				(await verify.Receipts.AnyAsync(x => x.Id == receiptId)).Should().BeFalse();
			}
			await legacyTransaction.RollbackAsync();
			await using FileStream retryStream = File.OpenRead(path);
			using CancellationTokenSource retryTimeout = new(TimeSpan.FromSeconds(30));
			await service.ImportFromSqliteAsync(retryStream, retryTimeout.Token);
			await using ApplicationDbContext restored = isolated.CreateDbContext();
			var receipt = await restored.Receipts.SingleAsync(x => x.Id == receiptId);
			receipt.Location.Should().Be("Archived store");
			receipt.OriginalImagePath.Should().Be("historical/original.jpg");
			receipt.ProcessedImagePath.Should().Be("historical/processed.png");
		}
		finally
		{
			File.Delete(path);
			await isolated.DisposeAsync();
		}
	}

	[Fact]
	public async Task LocalLease_CancelledWaitDoesNotReleaseOrLeakAnotherImportsLease()
	{
		// A private key: this test must not hold the gate that other tests' imports use.
		string database = $"lease-test-{Guid.NewGuid():N}";
		using CancellationTokenSource cancellation = new();
		await using (IAsyncDisposable first = await BackupImportLock.AcquireLocalAsync(database, CancellationToken.None))
		{
			Task<IAsyncDisposable> waiting = BackupImportLock.AcquireLocalAsync(database, cancellation.Token);
			waiting.IsCompleted.Should().BeFalse();
			cancellation.Cancel();
			await FluentActions.Awaiting(async () => await waiting).Should().ThrowAsync<OperationCanceledException>();
			using CancellationTokenSource stillBlocked = new();
			Task<IAsyncDisposable> secondWait = BackupImportLock.AcquireLocalAsync(database, stillBlocked.Token);
			secondWait.IsCompleted.Should().BeFalse("cancelling a waiter must not release the owner's lease");
			stillBlocked.Cancel();
			await FluentActions.Awaiting(async () => await secondWait).Should().ThrowAsync<OperationCanceledException>();
		}
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
		await using IAsyncDisposable next = await BackupImportLock.AcquireLocalAsync(database, timeout.Token);
	}

	[Fact]
	public async Task Import_IntoAnotherDatabase_IsNotHeldBehindAnImportWaitingOnItsOwnDatabase()
	{
		PostgresFixture a = new();
		PostgresFixture b = new();
		await a.InitializeAsync();
		await b.InitializeAsync();
		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		string pathA = Path.Combine(Path.GetTempPath(), $"backup-lock-a-{Guid.NewGuid():N}.db");
		string pathB = Path.Combine(Path.GetTempPath(), $"backup-lock-b-{Guid.NewGuid():N}.db");
		Task? importA = null;
		NpgsqlConnection holderA = new(a.ConnectionString);
		NpgsqlTransaction? lockA = null;
		try
		{
			await WriteBackupAsync(pathA, receiptA, "Store A");
			await WriteBackupAsync(pathB, receiptB, "Store B");
			await holderA.OpenAsync();
			lockA = await holderA.BeginTransactionAsync();
			await using (NpgsqlCommand acquire = new("SELECT pg_advisory_xact_lock(23157103317831764)", holderA, lockA))
			{
				await acquire.ExecuteNonQueryAsync();
			}
			BackupImportService serviceA = new(new ContextFactory(a), NullLogger<BackupImportService>.Instance);
			BackupImportService serviceB = new(new ContextFactory(b), NullLogger<BackupImportService>.Instance);
			await using FileStream streamA = File.OpenRead(pathA);
			importA = serviceA.ImportFromSqliteAsync(streamA, CancellationToken.None);
			await using (NpgsqlCommand waiting = new("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND pg_backend_pid() = ANY(pg_blocking_pids(pid)))", holderA, lockA))
			{
				bool observedWait = false;
				using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
				while (!importA.IsCompleted && !observedWait)
				{
					observedWait = (bool)(await waiting.ExecuteScalarAsync(timeout.Token))!;
					if (!observedWait)
					{
						await Task.Delay(20, timeout.Token);
					}
				}
				observedWait.Should().BeTrue("the import into A must be waiting on A's advisory lock before B imports");
			}

			await using (FileStream streamB = File.OpenRead(pathB))
			{
				using CancellationTokenSource timeoutB = new(TimeSpan.FromSeconds(30));
				await FluentActions.Awaiting(() => serviceB.ImportFromSqliteAsync(streamB, timeoutB.Token))
					.Should().NotThrowAsync("an import into database B must not be held behind an import waiting on database A's lock");
			}
			importA.IsCompleted.Should().BeFalse("A's import is still blocked on A's advisory lock");
			await using (ApplicationDbContext verifyB = b.CreateDbContext())
			{
				(await verifyB.Receipts.SingleAsync(x => x.Id == receiptB)).Location.Should().Be("Store B");
			}

			await lockA.RollbackAsync();
			await importA.WaitAsync(TimeSpan.FromSeconds(30));
			await using ApplicationDbContext verifyA = a.CreateDbContext();
			(await verifyA.Receipts.SingleAsync(x => x.Id == receiptA)).Location.Should().Be("Store A");
		}
		finally
		{
			if (lockA is not null)
			{
				await lockA.DisposeAsync();
			}
			await holderA.DisposeAsync();
			if (importA is not null)
			{
				try { await importA.WaitAsync(TimeSpan.FromSeconds(30)); } catch (Exception) { /* primary assertion already reported */ }
			}
			File.Delete(pathA);
			File.Delete(pathB);
			await a.DisposeAsync();
			await b.DisposeAsync();
		}
	}

	private static async Task WriteBackupAsync(string path, Guid receiptId, string location)
	{
		await using SqliteConnection backup = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
		await backup.OpenAsync();
		await BackupService.CreateSchemaAsync(backup, CancellationToken.None);
		await using SqliteCommand seed = backup.CreateCommand();
		seed.CommandText = "INSERT INTO backup_metadata VALUES ('export_version', '6'); INSERT INTO receipts VALUES ($id, $location, '2024-01-01', '0', 'USD', NULL, NULL);";
		seed.Parameters.AddWithValue("$id", receiptId.ToString());
		seed.Parameters.AddWithValue("$location", location);
		await seed.ExecuteNonQueryAsync();
	}

	private sealed class ContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
