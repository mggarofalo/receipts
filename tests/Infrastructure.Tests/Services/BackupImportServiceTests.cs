using Application.Models;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Services;

public class BackupImportServiceTests : IDisposable
{
	private readonly string _inMemoryDbName;
	private readonly DbContextOptions<ApplicationDbContext> _options;
	private readonly BackupImportService _service;
	private readonly string _tempDir;

	public BackupImportServiceTests()
	{
		_inMemoryDbName = Guid.NewGuid().ToString();
		_options = new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase(databaseName: _inMemoryDbName)
			.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
			.Options;

		Moq.Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
		factoryMock
			.Setup(f => f.CreateDbContextAsync(Moq.It.IsAny<CancellationToken>()))
			.Returns(() => Task.FromResult(new ApplicationDbContext(_options)));

		ILogger<BackupImportService> logger = NullLogger<BackupImportService>.Instance;
		_service = new BackupImportService(factoryMock.Object, logger);

		_tempDir = Path.Combine(Path.GetTempPath(), $"backup-import-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
	}

	/// <summary>
	/// Creates a fresh context for seeding/asserting that shares the same in-memory database.
	/// </summary>
	private ApplicationDbContext CreateAssertionContext() => new(_options);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_tempDir, recursive: true);
		}
		catch
		{
			// Best effort cleanup
		}
		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task ImportFromSqliteAsync_InvalidFile_ThrowsInvalidOperationException()
	{
		// Arrange
		using MemoryStream stream = new([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F]);

		// Act & Assert
		Func<Task> act = () => _service.ImportFromSqliteAsync(stream, CancellationToken.None);
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*not a valid SQLite database*");
	}

	[Fact]
	public async Task ImportFromSqliteAsync_EmptyDatabase_ReturnsZeroCounts()
	{
		// Arrange
		string sqlitePath = CreateEmptySqliteDatabase();
		await using FileStream stream = File.OpenRead(sqlitePath);

		// Act
		BackupImportResult result = await _service.ImportFromSqliteAsync(stream, CancellationToken.None);

		// Assert
		result.TotalCreated.Should().Be(0);
		result.TotalUpdated.Should().Be(0);
	}

	[Fact]
	public async Task ImportFromSqliteAsync_AccountsTable_CreatesNewAccounts()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		string sqlitePath = CreateSqliteDatabaseWithAccounts(
			(accountId, "ACC001", "Test Account", true));

		await using FileStream stream = File.OpenRead(sqlitePath);

		// Act
		BackupImportResult result = await _service.ImportFromSqliteAsync(stream, CancellationToken.None);

		// Assert
		result.CardsCreated.Should().Be(1);
		result.CardsUpdated.Should().Be(0);

		await using ApplicationDbContext assertCtx = CreateAssertionContext();
		CardEntity? account = await assertCtx.Cards.FindAsync(accountId);
		account.Should().NotBeNull();
		account!.CardCode.Should().Be("ACC001");
		account.Name.Should().Be("Test Account");
		account.IsActive.Should().BeTrue();
	}

	[Fact]
	public async Task ImportFromSqliteAsync_ExistingAccount_UpdatesAccount()
	{
		// Arrange - seed existing account
		Guid accountId = Guid.NewGuid();
		await using (ApplicationDbContext seedCtx = CreateAssertionContext())
		{
			seedCtx.Cards.Add(new CardEntity
			{
				Id = accountId,
				CardCode = "OLD001",
				Name = "Old Name",
				IsActive = false,
			});
			await seedCtx.SaveChangesAsync();
		}

		// Create SQLite with updated values
		string sqlitePath = CreateSqliteDatabaseWithAccounts(
			(accountId, "NEW001", "New Name", true));

		await using FileStream stream = File.OpenRead(sqlitePath);

		// Act
		BackupImportResult result = await _service.ImportFromSqliteAsync(stream, CancellationToken.None);

		// Assert
		result.CardsCreated.Should().Be(0);
		result.CardsUpdated.Should().Be(1);

		await using ApplicationDbContext assertCtx = CreateAssertionContext();
		CardEntity? account = await assertCtx.Cards.FindAsync(accountId);
		account.Should().NotBeNull();
		account!.CardCode.Should().Be("NEW001");
		account.Name.Should().Be("New Name");
		account.IsActive.Should().BeTrue();
	}

	[Fact]
	public async Task ImportFromSqliteAsync_CategoriesTable_CreatesNewCategories()
	{
		// Arrange
		Guid categoryId = Guid.NewGuid();
		string sqlitePath = CreateSqliteDatabaseWithCategories(
			(categoryId, "Groceries", "Food and household items", true));

		await using FileStream stream = File.OpenRead(sqlitePath);

		// Act
		BackupImportResult result = await _service.ImportFromSqliteAsync(stream, CancellationToken.None);

		// Assert
		result.CategoriesCreated.Should().Be(1);
		result.CategoriesUpdated.Should().Be(0);
	}

	[Fact]
	public async Task ImportFromSqliteAsync_ReceiptsTable_CreatesNewReceipts()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		string sqlitePath = CreateSqliteDatabaseWithReceipts(
			(receiptId, "Walmart", "2024-06-15", 2.50m, "USD"));

		await using FileStream stream = File.OpenRead(sqlitePath);

		// Act
		BackupImportResult result = await _service.ImportFromSqliteAsync(stream, CancellationToken.None);

		// Assert
		result.ReceiptsCreated.Should().Be(1);
		result.ReceiptsUpdated.Should().Be(0);

		await using ApplicationDbContext assertCtx = CreateAssertionContext();
		ReceiptEntity? receipt = await assertCtx.Receipts.FindAsync(receiptId);
		receipt.Should().NotBeNull();
		receipt!.Location.Should().Be("Walmart");
		receipt.Date.Should().Be(new DateOnly(2024, 6, 15));
		receipt.TaxAmount.Should().Be(2.50m);
		receipt.TaxAmountCurrency.Should().Be(Currency.USD);
	}

	[Fact]
	public async Task ImportFromSqliteAsync_MixedCreateAndUpdate_ReturnsCorrectCounts()
	{
		// Arrange - seed an existing account
		Guid existingId = Guid.NewGuid();
		await using (ApplicationDbContext seedCtx = CreateAssertionContext())
		{
			seedCtx.Cards.Add(new CardEntity
			{
				Id = existingId,
				CardCode = "EXIST",
				Name = "Existing",
				IsActive = true,
			});
			await seedCtx.SaveChangesAsync();
		}

		Guid newId = Guid.NewGuid();
		string sqlitePath = CreateSqliteDatabaseWithAccounts(
			(existingId, "UPDATED", "Updated Name", false),
			(newId, "NEW001", "Brand New", true));

		await using FileStream stream = File.OpenRead(sqlitePath);

		// Act
		BackupImportResult result = await _service.ImportFromSqliteAsync(stream, CancellationToken.None);

		// Assert
		result.CardsCreated.Should().Be(1);
		result.CardsUpdated.Should().Be(1);
	}

	[Fact]
	public async Task ImportFromSqliteAsync_LegacyBackup_InfersAccountIdFromCardId()
	{
		// Legacy (v<3) backups have no account_id. Import must fall back to AccountId = Card.Id
		// so the Card → Account FK invariant is preserved after RECEIPTS-575.
		// Arrange
		Guid cardId = Guid.NewGuid();
		string sqlitePath = CreateSqliteDatabaseWithAccounts(
			(cardId, "ACC001", "Test Card", true));

		await using FileStream stream = File.OpenRead(sqlitePath);

		// Act
		BackupImportResult result = await _service.ImportFromSqliteAsync(stream, CancellationToken.None);

		// Assert
		result.CardsCreated.Should().Be(1);

		await using ApplicationDbContext assertCtx = CreateAssertionContext();
		CardEntity? card = await assertCtx.Cards.FindAsync(cardId);
		card.Should().NotBeNull();
		card!.AccountId.Should().Be(cardId);

		AccountEntity? parent = await assertCtx.Accounts.FindAsync(cardId);
		parent.Should().NotBeNull("legacy import should upsert a parent Account with same Id as the Card");
	}

	[Fact]
	public async Task ImportFromSqliteAsync_V3BackupWithAccountId_ImportsCorrectly()
	{
		// Arrange
		Guid cardId = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();
		string sqlitePath = CreateV3SqliteDatabaseWithCards(
			[(cardId, "ACC001", "Test Card", true, accountId)],
			(accountId, "Primary Checking", true));

		await using FileStream stream = File.OpenRead(sqlitePath);

		// Act
		BackupImportResult result = await _service.ImportFromSqliteAsync(stream, CancellationToken.None);

		// Assert
		result.AccountsCreated.Should().Be(1);
		result.CardsCreated.Should().Be(1);

		await using ApplicationDbContext assertCtx = CreateAssertionContext();
		CardEntity? card = await assertCtx.Cards.FindAsync(cardId);
		card.Should().NotBeNull();
		card!.AccountId.Should().Be(accountId);

		AccountEntity? parent = await assertCtx.Accounts.FindAsync(accountId);
		parent.Should().NotBeNull();
		parent!.Name.Should().Be("Primary Checking", "v3 import must use the exported Account name, not the Card name");
	}

	[Fact]
	public async Task ImportFromSqliteAsync_V3BackupSharedAccount_ImportsSharedParent()
	{
		// Arrange: two cards sharing a single Account — the exact case the bug-finder flagged.
		Guid sharedAccountId = Guid.NewGuid();
		Guid card1Id = Guid.NewGuid();
		Guid card2Id = Guid.NewGuid();
		string sqlitePath = CreateV3SqliteDatabaseWithCards(
			[
				(card1Id, "DEBIT-1", "Personal Debit", true, sharedAccountId),
				(card2Id, "DEBIT-2", "Shared Debit", true, sharedAccountId),
			],
			(sharedAccountId, "Joint Checking", true));

		await using FileStream stream = File.OpenRead(sqlitePath);

		// Act
		BackupImportResult result = await _service.ImportFromSqliteAsync(stream, CancellationToken.None);

		// Assert — one Account upserted, both Cards point at it.
		result.AccountsCreated.Should().Be(1);
		result.CardsCreated.Should().Be(2);

		await using ApplicationDbContext assertCtx = CreateAssertionContext();
		AccountEntity? shared = await assertCtx.Accounts.FindAsync(sharedAccountId);
		shared.Should().NotBeNull();
		shared!.Name.Should().Be("Joint Checking");

		CardEntity? c1 = await assertCtx.Cards.FindAsync(card1Id);
		CardEntity? c2 = await assertCtx.Cards.FindAsync(card2Id);
		c1!.AccountId.Should().Be(sharedAccountId);
		c2!.AccountId.Should().Be(sharedAccountId);
	}

	[Fact]
	public async Task ImportFromSqliteAsync_V3BackupWithNullAccountId_Throws()
	{
		// Arrange: v3 backup where one card row has a NULL account_id — must fail fast.
		Guid cardId = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();
		string sqlitePath = CreateV3SqliteDatabaseWithCards(
			[(cardId, "ACC001", "Orphan Card", true, (Guid?)null)],
			(accountId, "Dummy Account", true));

		await using FileStream stream = File.OpenRead(sqlitePath);

		// Act
		Func<Task> act = () => _service.ImportFromSqliteAsync(stream, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage($"*{cardId}*missing account_id*");

		await using ApplicationDbContext assertCtx = CreateAssertionContext();
		CardEntity? card = await assertCtx.Cards.FindAsync(cardId);
		card.Should().BeNull("transaction must roll back when any card row is missing account_id");
	}

	[Fact]
	public async Task ImportFromSqliteAsync_TransactionsV2_DerivesAccountIdFromCardsParent()
	{
		// RECEIPTS-574 regression guard: v2 exports only carry card_id. The importer must
		// derive Transaction.AccountId from the Card's parent Account, not from card_id
		// itself (which is the bug the PR #454 bug-finder caught).
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		Guid txId = Guid.NewGuid();

		await using (ApplicationDbContext seedCtx = CreateAssertionContext())
		{
			seedCtx.Accounts.Add(new AccountEntity { Id = accountId, Name = "Checking", IsActive = true });
			seedCtx.Cards.Add(new CardEntity
			{
				Id = cardId,
				CardCode = "CARD01",
				Name = "Primary",
				IsActive = true,
				AccountId = accountId,
			});
			seedCtx.Receipts.Add(new ReceiptEntity
			{
				Id = receiptId,
				Location = "Walmart",
				Date = new DateOnly(2025, 6, 1),
				TaxAmount = 0,
				TaxAmountCurrency = Currency.USD,
			});
			await seedCtx.SaveChangesAsync();
		}

		string sqlitePath = CreateSqliteDatabaseWithTransactionsV2(
			(txId, receiptId, cardId, 19.99m, "USD", "2025-06-01"));

		await using FileStream stream = File.OpenRead(sqlitePath);

		BackupImportResult result = await _service.ImportFromSqliteAsync(stream, CancellationToken.None);

		result.TransactionsCreated.Should().Be(1);

		await using ApplicationDbContext assertCtx = CreateAssertionContext();
		TransactionEntity? imported = await assertCtx.Transactions.FindAsync(txId);
		imported.Should().NotBeNull();
		imported!.CardId.Should().Be(cardId);
		imported.Card!.AccountId.Should().Be(accountId, "AccountId must be derived from Card.AccountId, not reused from card_id");
	}

	[Fact]
	public async Task ImportFromSqliteAsync_MissingTables_SkipsGracefully()
	{
		// Arrange - SQLite with only accounts table, no other tables
		string sqlitePath = CreateSqliteDatabaseWithAccounts(
			(Guid.NewGuid(), "ACC001", "Test", true));

		await using FileStream stream = File.OpenRead(sqlitePath);

		// Act
		BackupImportResult result = await _service.ImportFromSqliteAsync(stream, CancellationToken.None);

		// Assert
		result.CardsCreated.Should().Be(1);
		result.CategoriesCreated.Should().Be(0);
		result.ReceiptsCreated.Should().Be(0);
		result.ReceiptItemsCreated.Should().Be(0);
		result.TransactionsCreated.Should().Be(0);
		result.AdjustmentsCreated.Should().Be(0);
	}

	#region SQLite Test Helpers

	private string CreateEmptySqliteDatabase()
	{
		string path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.sqlite");
		using SqliteConnection conn = new($"Data Source={path};Pooling=False");
		conn.Open();
		// Force SQLite to write the file header by creating and dropping a dummy table
		using SqliteCommand cmd = conn.CreateCommand();
		cmd.CommandText = "CREATE TABLE _dummy (id INTEGER); DROP TABLE _dummy;";
		cmd.ExecuteNonQuery();
		return path;
	}

	private string CreateSqliteDatabaseWithAccounts(params (Guid Id, string CardCode, string Name, bool IsActive)[] accounts)
	{
		string path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.sqlite");
		using SqliteConnection conn = new($"Data Source={path};Pooling=False");
		conn.Open();

		using SqliteCommand createCmd = conn.CreateCommand();
		createCmd.CommandText = @"
			CREATE TABLE accounts (
				id TEXT NOT NULL PRIMARY KEY,
				account_code TEXT NOT NULL,
				name TEXT NOT NULL,
				is_active INTEGER NOT NULL
			)";
		createCmd.ExecuteNonQuery();

		foreach ((Guid id, string accountCode, string name, bool isActive) in accounts)
		{
			using SqliteCommand insertCmd = conn.CreateCommand();
			insertCmd.CommandText = "INSERT INTO accounts (id, account_code, name, is_active) VALUES (@id, @code, @name, @active)";
			insertCmd.Parameters.AddWithValue("@id", id.ToString());
			insertCmd.Parameters.AddWithValue("@code", accountCode);
			insertCmd.Parameters.AddWithValue("@name", name);
			insertCmd.Parameters.AddWithValue("@active", isActive);
			insertCmd.ExecuteNonQuery();
		}

		return path;
	}

	private string CreateSqliteDatabaseWithCategories(params (Guid Id, string Name, string? Description, bool IsActive)[] categories)
	{
		string path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.sqlite");
		using SqliteConnection conn = new($"Data Source={path};Pooling=False");
		conn.Open();

		using SqliteCommand createCmd = conn.CreateCommand();
		createCmd.CommandText = @"
			CREATE TABLE categories (
				id TEXT NOT NULL PRIMARY KEY,
				name TEXT NOT NULL,
				description TEXT,
				is_active INTEGER NOT NULL
			)";
		createCmd.ExecuteNonQuery();

		foreach ((Guid id, string name, string? description, bool isActive) in categories)
		{
			using SqliteCommand insertCmd = conn.CreateCommand();
			insertCmd.CommandText = "INSERT INTO categories (id, name, description, is_active) VALUES (@id, @name, @desc, @active)";
			insertCmd.Parameters.AddWithValue("@id", id.ToString());
			insertCmd.Parameters.AddWithValue("@name", name);
			insertCmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
			insertCmd.Parameters.AddWithValue("@active", isActive);
			insertCmd.ExecuteNonQuery();
		}

		return path;
	}

	private string CreateV3SqliteDatabaseWithCards(
		(Guid Id, string CardCode, string Name, bool IsActive, Guid? AccountId)[] cards,
		params (Guid Id, string Name, bool IsActive)[] accounts)
	{
		string path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.sqlite");
		using SqliteConnection conn = new($"Data Source={path};Pooling=False");
		conn.Open();

		using SqliteCommand metaCreate = conn.CreateCommand();
		metaCreate.CommandText = @"
			CREATE TABLE backup_metadata (
				key TEXT NOT NULL PRIMARY KEY,
				value TEXT NOT NULL
			)";
		metaCreate.ExecuteNonQuery();

		using SqliteCommand metaInsert = conn.CreateCommand();
		metaInsert.CommandText = "INSERT INTO backup_metadata (key, value) VALUES ('export_version', '3')";
		metaInsert.ExecuteNonQuery();

		using SqliteCommand accountsCreate = conn.CreateCommand();
		accountsCreate.CommandText = @"
			CREATE TABLE accounts (
				id TEXT NOT NULL PRIMARY KEY,
				name TEXT NOT NULL,
				is_active INTEGER NOT NULL
			)";
		accountsCreate.ExecuteNonQuery();

		foreach ((Guid accountId, string accountName, bool accountActive) in accounts)
		{
			using SqliteCommand insertCmd = conn.CreateCommand();
			insertCmd.CommandText = "INSERT INTO accounts (id, name, is_active) VALUES (@id, @name, @active)";
			insertCmd.Parameters.AddWithValue("@id", accountId.ToString());
			insertCmd.Parameters.AddWithValue("@name", accountName);
			insertCmd.Parameters.AddWithValue("@active", accountActive);
			insertCmd.ExecuteNonQuery();
		}

		using SqliteCommand createCmd = conn.CreateCommand();
		createCmd.CommandText = @"
			CREATE TABLE cards (
				id TEXT NOT NULL PRIMARY KEY,
				card_code TEXT NOT NULL,
				name TEXT NOT NULL,
				is_active INTEGER NOT NULL,
				account_id TEXT
			)";
		createCmd.ExecuteNonQuery();

		foreach ((Guid id, string cardCode, string name, bool isActive, Guid? accountId) in cards)
		{
			using SqliteCommand insertCmd = conn.CreateCommand();
			insertCmd.CommandText = "INSERT INTO cards (id, card_code, name, is_active, account_id) VALUES (@id, @code, @name, @active, @accountId)";
			insertCmd.Parameters.AddWithValue("@id", id.ToString());
			insertCmd.Parameters.AddWithValue("@code", cardCode);
			insertCmd.Parameters.AddWithValue("@name", name);
			insertCmd.Parameters.AddWithValue("@active", isActive);
			insertCmd.Parameters.AddWithValue("@accountId", (object?)accountId?.ToString() ?? DBNull.Value);
			insertCmd.ExecuteNonQuery();
		}

		return path;
	}

	private string CreateSqliteDatabaseWithReceipts(params (Guid Id, string Location, string Date, decimal TaxAmount, string TaxAmountCurrency)[] receipts)
	{
		string path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.sqlite");
		using SqliteConnection conn = new($"Data Source={path};Pooling=False");
		conn.Open();

		using SqliteCommand createCmd = conn.CreateCommand();
		createCmd.CommandText = @"
			CREATE TABLE receipts (
				id TEXT NOT NULL PRIMARY KEY,
				location TEXT NOT NULL,
				date TEXT NOT NULL,
				tax_amount REAL NOT NULL,
				tax_amount_currency TEXT NOT NULL
			)";
		createCmd.ExecuteNonQuery();

		foreach ((Guid id, string location, string date, decimal taxAmount, string taxAmountCurrency) in receipts)
		{
			using SqliteCommand insertCmd = conn.CreateCommand();
			insertCmd.CommandText = "INSERT INTO receipts (id, location, date, tax_amount, tax_amount_currency) VALUES (@id, @loc, @date, @tax, @currency)";
			insertCmd.Parameters.AddWithValue("@id", id.ToString());
			insertCmd.Parameters.AddWithValue("@loc", location);
			insertCmd.Parameters.AddWithValue("@date", date);
			insertCmd.Parameters.AddWithValue("@tax", (double)taxAmount);
			insertCmd.Parameters.AddWithValue("@currency", taxAmountCurrency);
			insertCmd.ExecuteNonQuery();
		}

		return path;
	}

	private string CreateSqliteDatabaseWithTransactionsV2(params (Guid Id, Guid ReceiptId, Guid CardId, decimal Amount, string AmountCurrency, string Date)[] transactions)
	{
		string path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.sqlite");
		using SqliteConnection conn = new($"Data Source={path};Pooling=False");
		conn.Open();

		using SqliteCommand metaCmd = conn.CreateCommand();
		metaCmd.CommandText = """
			CREATE TABLE backup_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
			INSERT INTO backup_metadata (key, value) VALUES ('export_version', '2');
			""";
		metaCmd.ExecuteNonQuery();

		using SqliteCommand createCmd = conn.CreateCommand();
		createCmd.CommandText = @"
			CREATE TABLE transactions (
				id TEXT NOT NULL PRIMARY KEY,
				receipt_id TEXT NOT NULL,
				card_id TEXT NOT NULL,
				amount TEXT NOT NULL,
				amount_currency TEXT NOT NULL,
				date TEXT NOT NULL
			)";
		createCmd.ExecuteNonQuery();

		foreach ((Guid id, Guid receiptId, Guid cardId, decimal amount, string amountCurrency, string date) in transactions)
		{
			using SqliteCommand insertCmd = conn.CreateCommand();
			insertCmd.CommandText = "INSERT INTO transactions (id, receipt_id, card_id, amount, amount_currency, date) VALUES (@id, @rid, @cid, @amt, @cur, @date)";
			insertCmd.Parameters.AddWithValue("@id", id.ToString());
			insertCmd.Parameters.AddWithValue("@rid", receiptId.ToString());
			insertCmd.Parameters.AddWithValue("@cid", cardId.ToString());
			insertCmd.Parameters.AddWithValue("@amt", amount.ToString("G"));
			insertCmd.Parameters.AddWithValue("@cur", amountCurrency);
			insertCmd.Parameters.AddWithValue("@date", date);
			insertCmd.ExecuteNonQuery();
		}

		return path;
	}

	#endregion
}
