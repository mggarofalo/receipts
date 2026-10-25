using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
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
		using CancellationTokenSource cancellation = new();
		await using (IAsyncDisposable first = await BackupImportLock.AcquireLocalAsync(CancellationToken.None))
		{
			Task<IAsyncDisposable> waiting = BackupImportLock.AcquireLocalAsync(cancellation.Token);
			waiting.IsCompleted.Should().BeFalse();
			cancellation.Cancel();
			await FluentActions.Awaiting(async () => await waiting).Should().ThrowAsync<OperationCanceledException>();
			using CancellationTokenSource stillBlocked = new();
			Task<IAsyncDisposable> secondWait = BackupImportLock.AcquireLocalAsync(stillBlocked.Token);
			secondWait.IsCompleted.Should().BeFalse("cancelling a waiter must not release the owner's lease");
			stillBlocked.Cancel();
			await FluentActions.Awaiting(async () => await secondWait).Should().ThrowAsync<OperationCanceledException>();
		}
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
		await using IAsyncDisposable next = await BackupImportLock.AcquireLocalAsync(timeout.Token);
	}

	private sealed class ContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
