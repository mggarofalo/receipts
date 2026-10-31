using System.IO.Compression;
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

/// <summary>
/// RECEIPTS-1087: the gzip-wrapped download restores exactly what the raw SQLite export restores,
/// and raw files from older exports still import. Each restore uses a separate, migrated PostgreSQL database.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class BackupCompressedRoundTripTests(PostgresFixture server) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
	private readonly List<PostgresFixture> _databases = [];
	private readonly List<string> _files = [];

	public Task InitializeAsync() => Task.CompletedTask;

	[Fact]
	public async Task CompressedExport_RestoresSameBusinessDataAsRawExport()
	{
		// Arrange
		PostgresFixture source = await DatabaseAsync();
		PostgresFixture rawTarget = await DatabaseAsync();
		PostgresFixture compressedTarget = await DatabaseAsync();
		Guid accountId = Guid.NewGuid(), receiptId = Guid.NewGuid(), itemId = Guid.NewGuid();
		Guid txId = Guid.NewGuid(), adjId = Guid.NewGuid(), templateId = Guid.NewGuid();
		await using (ApplicationDbContext ctx = source.CreateDbContext())
		{
			ctx.Accounts.Add(new AccountEntity { Id = accountId, Name = "Checking", IsActive = true });
			ctx.Cards.Add(new CardEntity { Id = accountId, CardCode = "1000", Name = "Checking", IsActive = true, AccountId = accountId });
			ctx.Receipts.Add(new ReceiptEntity { Id = receiptId, Location = "Kaufland", Date = new DateOnly(2024, 3, 4), TaxAmount = 12.34m, TaxAmountCurrency = Currency.USD });
			ctx.ReceiptItems.Add(new ReceiptItemEntity
			{
				Id = itemId,
				ReceiptId = receiptId,
				Description = "Butter",
				Quantity = 2.5m,
				UnitPrice = 1234.56m,
				UnitPriceCurrency = Currency.USD,
				TotalAmount = 3086.40m,
				TotalAmountCurrency = Currency.USD,
				Category = "Food",
			});
			ctx.Transactions.Add(new TransactionEntity { Id = txId, ReceiptId = receiptId, CardId = accountId, Amount = 99.99m, AmountCurrency = Currency.USD, Date = new DateOnly(2024, 3, 4) });
			ctx.Adjustments.Add(new AdjustmentEntity { Id = adjId, ReceiptId = receiptId, Type = AdjustmentType.Tip, Amount = 7.77m, AmountCurrency = Currency.USD });
			ctx.ItemTemplates.Add(new ItemTemplateEntity { Id = templateId, Name = "Butter", DefaultUnitPrice = 4.99m, DefaultUnitPriceCurrency = Currency.USD });
			await ctx.SaveChangesAsync();
		}
		BackupService exporter = new(new Factory(source.CreateOptions()), NullLogger<BackupService>.Instance);
		string rawPath = Track(await exporter.ExportToSqliteAsync());
		string compressedPath = Track(await exporter.ExportCompressedAsync());
		SqliteConnection.ClearAllPools();

		// Act
		BackupImportResult rawResult = await ImportAsync(rawTarget, rawPath);
		BackupImportResult compressedResult = await ImportAsync(compressedTarget, compressedPath);

		// Assert
		compressedPath.Should().EndWith(".db.gz");
		(await IsGzipAsync(compressedPath)).Should().BeTrue();
		compressedResult.Should().BeEquivalentTo(rawResult);
		compressedResult.ReceiptsCreated.Should().Be(1);
		compressedResult.ReceiptItemsCreated.Should().Be(1);
		BusinessSnapshot expected = await SnapshotAsync(source);
		BusinessSnapshot fromRaw = await SnapshotAsync(rawTarget);
		BusinessSnapshot fromCompressed = await SnapshotAsync(compressedTarget);
		fromRaw.Should().BeEquivalentTo(expected);
		fromCompressed.Should().BeEquivalentTo(fromRaw);
		fromCompressed.ReceiptItems.Should().ContainSingle().Which.UnitPrice.Should().Be(1234.56m);
	}

	[Fact]
	public async Task RawOlderFormatFile_StillImports_AndGzippedCopyImportsTheSame()
	{
		// Arrange
		PostgresFixture rawTarget = await DatabaseAsync();
		PostgresFixture compressedTarget = await DatabaseAsync();
		Guid cardId = Guid.NewGuid(), accountId = Guid.NewGuid();
		string rawPath = Track(CreateV3Backup(cardId, accountId));
		string compressedPath = Track(rawPath + ".gz");
		await using (FileStream input = File.OpenRead(rawPath))
		await using (FileStream output = File.Create(compressedPath))
		await using (GZipStream gzip = new(output, CompressionLevel.Optimal))
		{
			await input.CopyToAsync(gzip);
		}

		// Act
		BackupImportResult rawResult = await ImportAsync(rawTarget, rawPath);
		BackupImportResult compressedResult = await ImportAsync(compressedTarget, compressedPath);

		// Assert
		rawResult.AccountsCreated.Should().Be(1);
		rawResult.CardsCreated.Should().Be(1);
		compressedResult.Should().BeEquivalentTo(rawResult);
		BusinessSnapshot fromRaw = await SnapshotAsync(rawTarget);
		fromRaw.Accounts.Should().ContainSingle().Which.Should().Be(new AccountRow(accountId, "Primary Checking", true));
		fromRaw.Cards.Should().ContainSingle().Which.Should().Be(new CardRow(cardId, "ACC001", "Test Card", true, accountId));
		(await SnapshotAsync(compressedTarget)).Should().BeEquivalentTo(fromRaw);
	}

	private async Task<PostgresFixture> DatabaseAsync()
	{
		PostgresFixture database = await server.CreateIsolatedAsync();
		_databases.Add(database);
		return database;
	}

	private string Track(string path)
	{
		_files.Add(path);
		return path;
	}

	private static async Task<BackupImportResult> ImportAsync(PostgresFixture target, string path)
	{
		BackupImportService importer = new(new Factory(target.CreateOptions()), NullLogger<BackupImportService>.Instance);
		await using FileStream stream = File.OpenRead(path);
		return await importer.ImportFromSqliteAsync(stream, CancellationToken.None);
	}

	private static async Task<bool> IsGzipAsync(string path)
	{
		byte[] header = new byte[2];
		await using FileStream stream = File.OpenRead(path);
		await stream.ReadExactlyAsync(header);
		return header[0] == 0x1F && header[1] == 0x8B;
	}

	// A v3 backup predates every table except accounts/cards; mirrors BackupImportServiceTests' legacy fixture.
	private static string CreateV3Backup(Guid cardId, Guid accountId)
	{
		string path = Path.Combine(Path.GetTempPath(), $"compressed-roundtrip-v3-{Guid.NewGuid():N}.db");
		using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
		connection.Open();
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
			CREATE TABLE backup_metadata (key TEXT PRIMARY KEY, value TEXT);
			INSERT INTO backup_metadata VALUES ('export_version', '3');
			CREATE TABLE accounts (id TEXT PRIMARY KEY, name TEXT NOT NULL, is_active INTEGER NOT NULL);
			CREATE TABLE cards (id TEXT PRIMARY KEY, card_code TEXT NOT NULL, name TEXT NOT NULL, is_active INTEGER NOT NULL, account_id TEXT);
			INSERT INTO accounts VALUES ($account, 'Primary Checking', 1);
			INSERT INTO cards VALUES ($card, 'ACC001', 'Test Card', 1, $account);
			""";
		command.Parameters.AddWithValue("$account", accountId.ToString());
		command.Parameters.AddWithValue("$card", cardId.ToString());
		command.ExecuteNonQuery();
		return path;
	}

	private static async Task<BusinessSnapshot> SnapshotAsync(PostgresFixture database)
	{
		await using ApplicationDbContext db = database.CreateDbContext();
		return new BusinessSnapshot(
			await db.Accounts.AsNoTracking().OrderBy(row => row.Id).Select(row => new AccountRow(row.Id, row.Name, row.IsActive)).ToListAsync(),
			await db.Cards.AsNoTracking().OrderBy(row => row.Id).Select(row => new CardRow(row.Id, row.CardCode, row.Name, row.IsActive, row.AccountId)).ToListAsync(),
			await db.Receipts.AsNoTracking().OrderBy(row => row.Id).Select(row => new ReceiptRow(row.Id, row.Location, row.Date, row.TaxAmount, row.TaxAmountCurrency)).ToListAsync(),
			await db.ReceiptItems.IgnoreAutoIncludes().AsNoTracking().OrderBy(row => row.Id).Select(row => new ReceiptItemRow(row.Id, row.ReceiptId, row.Description, row.Quantity, row.UnitPrice, row.TotalAmount, row.Category)).ToListAsync(),
			await db.Transactions.AsNoTracking().OrderBy(row => row.Id).Select(row => new TransactionRow(row.Id, row.ReceiptId, row.CardId, row.Amount, row.Date)).ToListAsync(),
			await db.Adjustments.AsNoTracking().OrderBy(row => row.Id).Select(row => new AdjustmentRow(row.Id, row.ReceiptId, row.Type, row.Amount)).ToListAsync(),
			await db.ItemTemplates.AsNoTracking().OrderBy(row => row.Id).Select(row => new TemplateRow(row.Id, row.Name, row.DefaultUnitPrice)).ToListAsync());
	}

	public async Task DisposeAsync()
	{
		SqliteConnection.ClearAllPools();
		foreach (string path in _files)
		{
			try { File.Delete(path); }
			catch { /* Best effort cleanup */ }
		}
		foreach (PostgresFixture database in _databases)
		{
			await database.DisposeAsync();
		}
	}

	private sealed record BusinessSnapshot(
		List<AccountRow> Accounts,
		List<CardRow> Cards,
		List<ReceiptRow> Receipts,
		List<ReceiptItemRow> ReceiptItems,
		List<TransactionRow> Transactions,
		List<AdjustmentRow> Adjustments,
		List<TemplateRow> Templates);
	private sealed record AccountRow(Guid Id, string Name, bool IsActive);
	private sealed record CardRow(Guid Id, string CardCode, string Name, bool IsActive, Guid AccountId);
	private sealed record ReceiptRow(Guid Id, string Location, DateOnly Date, decimal TaxAmount, Currency TaxAmountCurrency);
	private sealed record ReceiptItemRow(Guid Id, Guid ReceiptId, string Description, decimal Quantity, decimal UnitPrice, decimal TotalAmount, string Category);
	private sealed record TransactionRow(Guid Id, Guid ReceiptId, Guid CardId, decimal Amount, DateOnly Date);
	private sealed record AdjustmentRow(Guid Id, Guid ReceiptId, AdjustmentType Type, decimal Amount);
	private sealed record TemplateRow(Guid Id, string Name, decimal? DefaultUnitPrice);

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
			Task.FromResult(new ApplicationDbContext(options));
	}
}
