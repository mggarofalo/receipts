using Application.Models;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class BackupHistoricalFidelityTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture = new();
	private BackupImportService _importService = null!;
	private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"historical-fidelity-{Guid.NewGuid()}");
	public async Task InitializeAsync()
	{
		await _fixture.InitializeAsync(); Directory.CreateDirectory(_tempDir);
		_importService = new(new Factory(_fixture), NullLogger<BackupImportService>.Instance);
	}
	public async Task DisposeAsync() { Directory.Delete(_tempDir, true); await _fixture.DisposeAsync(); }
	private ApplicationDbContext Target() => _fixture.CreateDbContext();
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }
	// RECEIPTS-770/789 backward compatibility: a v3-style backup (no v4 tables, no image-path
	// columns) must still import cleanly, with the new fields treated as null/absent.
	[Fact]
	public async Task Import_LegacyV3Backup_SucceedsWithNewFieldsAbsent()
	{
		Guid accountId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		string path = CreateV3Backup(accountId, receiptId);

		await using FileStream stream = File.OpenRead(path);
		BackupImportResult result = await _importService.ImportFromSqliteAsync(stream, CancellationToken.None);

		// New v4 tables are absent → nothing created for them, and no exception thrown.
		result.YnabSelectedBudgetsCreated.Should().Be(0);
		result.YnabAccountMappingsCreated.Should().Be(0);
		result.YnabCategoryMappingsCreated.Should().Be(0);
		result.YnabSyncRecordsCreated.Should().Be(0);
		result.NormalizedDescriptionsCreated.Should().Be(0);
		result.ReceiptsCreated.Should().Be(1);

		await using ApplicationDbContext assert = Target();
		ReceiptEntity receipt = (await assert.Receipts.FindAsync(receiptId))!;
		receipt.Location.Should().Be("Legacy Store");
		receipt.OriginalImagePath.Should().BeNull("v3 backups never carried image paths");
		receipt.ProcessedImagePath.Should().BeNull();
		(await assert.YnabSelectedBudgets.CountAsync()).Should().Be(0);
		(await assert.NormalizedDescriptions.CountAsync()).Should().Be(0);
	}

	[Theory]
	[InlineData("Pending")]
	[InlineData("Failed")]
	public async Task Import_LegacyV5NonTerminalPushWithoutSnapshot_IsConservativelyUnknown(string status)
	{
		Guid accountId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		Guid transactionId = Guid.NewGuid();
		Guid syncId = Guid.NewGuid();
		string path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.sqlite");
		await using (SqliteConnection connection = new($"Data Source={path};Pooling=False"))
		{
			await connection.OpenAsync();
			await BackupService.CreateSchemaAsync(connection, CancellationToken.None);
			Exec(connection, "INSERT INTO backup_metadata VALUES ('export_version', '5')");
			Exec(connection, $"INSERT INTO accounts VALUES ('{accountId}', 'Checking', 1)");
			Exec(connection, $"INSERT INTO cards VALUES ('{accountId}', '1000', 'Checking', 1, '{accountId}')");
			Exec(connection, $"INSERT INTO receipts VALUES ('{receiptId}', 'Legacy Store', '2024-01-15', '0', 'USD', NULL, NULL)");
			Exec(connection, $"INSERT INTO transactions VALUES ('{transactionId}', '{receiptId}', '{accountId}', '10', 'USD', '2024-01-15')");
			Exec(connection, $"""
				INSERT INTO ynab_sync_records
					(id, local_transaction_id, ynab_transaction_id, ynab_budget_id, ynab_account_id,
					 import_id, request_payload_json, payload_hash, source_version, attempt_count,
					 claim_token, claimed_at_utc, last_attempt_at_utc, sync_type, sync_status,
					 synced_at_utc, last_error, created_at, updated_at)
				VALUES ('{syncId}', '{transactionId}', NULL, 'legacy-budget', 'legacy-account',
					NULL, NULL, NULL, NULL, 0, NULL, NULL, NULL, 'TransactionPush', '{status}',
					NULL, 'legacy error', '2024-01-15T00:00:00Z', '2024-01-15T00:00:00Z')
				""");
		}

		await using FileStream stream = File.OpenRead(path);
		await _importService.ImportFromSqliteAsync(stream, CancellationToken.None);

		await using ApplicationDbContext assert = Target();
		YnabSyncRecordEntity restored = (await assert.YnabSyncRecords.FindAsync(syncId))!;
		restored.SyncStatus.Should().Be(YnabSyncStatus.Unknown);
		restored.LastError.Should().Contain("no immutable operation snapshot");
		restored.ImportId.Should().BeNull();
		restored.RequestPayloadJson.Should().BeNull();
	}

	// RECEIPTS-789 regression guard: importing a v3 backup (no image-path columns) over an
	// EXISTING receipt must update the row but must NOT null out its already-present image paths.
	[Fact]
	public async Task Import_LegacyV3Backup_DoesNotNullExistingReceiptImagePaths()
	{
		Guid accountId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();

		// Seed the target DB with a receipt (same Id) that already has non-null image paths.
		await using (ApplicationDbContext seed = Target())
		{
			seed.Receipts.Add(new ReceiptEntity
			{
				Id = receiptId,
				Location = "Original Store",
				Date = new DateOnly(2020, 1, 1),
				TaxAmount = 9.99m,
				TaxAmountCurrency = Currency.USD,
				OriginalImagePath = "receipts/orig/keep-me.jpg",
				ProcessedImagePath = "receipts/proc/keep-me.png",
			});
			await seed.SaveChangesAsync();
		}

		// v3 backup contains the same receipt Id (no image-path columns) → hits the UPDATE path.
		string path = CreateV3Backup(accountId, receiptId);
		await using FileStream stream = File.OpenRead(path);
		BackupImportResult result = await _importService.ImportFromSqliteAsync(stream, CancellationToken.None);

		result.ReceiptsUpdated.Should().Be(1, "the receipt already exists, so import must take the update path");

		await using ApplicationDbContext assert = Target();
		ReceiptEntity receipt = (await assert.Receipts.FindAsync(receiptId))!;
		receipt.Location.Should().Be("Legacy Store", "the v3 import must still update the row");
		receipt.OriginalImagePath.Should().Be("receipts/orig/keep-me.jpg", "a v3 restore must not null out existing image paths");
		receipt.ProcessedImagePath.Should().Be("receipts/proc/keep-me.png");
	}

	private string CreateV3Backup(Guid accountId, Guid receiptId)
	{
		string path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.sqlite");
		using SqliteConnection conn = new($"Data Source={path};Pooling=False");
		conn.Open();

		Exec(conn, "CREATE TABLE backup_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL)");
		Exec(conn, "INSERT INTO backup_metadata (key, value) VALUES ('export_version', '3')");

		Exec(conn, "CREATE TABLE accounts (id TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL, is_active INTEGER NOT NULL)");
		Exec(conn, $"INSERT INTO accounts (id, name, is_active) VALUES ('{accountId}', 'Checking', 1)");

		Exec(conn, "CREATE TABLE cards (id TEXT NOT NULL PRIMARY KEY, card_code TEXT NOT NULL, name TEXT NOT NULL, is_active INTEGER NOT NULL, account_id TEXT NOT NULL)");
		Exec(conn, $"INSERT INTO cards (id, card_code, name, is_active, account_id) VALUES ('{accountId}', '1000', 'Checking', 1, '{accountId}')");

		// v3 receipts table: no image-path columns.
		Exec(conn, "CREATE TABLE receipts (id TEXT NOT NULL PRIMARY KEY, location TEXT NOT NULL, date TEXT NOT NULL, tax_amount TEXT NOT NULL, tax_amount_currency TEXT NOT NULL)");
		Exec(conn, $"INSERT INTO receipts (id, location, date, tax_amount, tax_amount_currency) VALUES ('{receiptId}', 'Legacy Store', '2024-01-15', '1.50', 'USD')");

		return path;
	}

	private static void Exec(SqliteConnection conn, string sql)
	{
		using SqliteCommand cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}

}
