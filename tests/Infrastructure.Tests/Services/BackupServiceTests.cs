using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Services;

public class BackupServiceTests : IDisposable
{
	private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
	private readonly BackupService _service;
	private readonly List<string> _tempFiles = [];

	public BackupServiceTests()
	{
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase(databaseName: $"BackupTest_{Guid.NewGuid()}")
			.Options;

		_dbContextFactory = new TestDbContextFactory(options);
		_service = new BackupService(_dbContextFactory, NullLogger<BackupService>.Instance);
	}

	[Fact]
	public async Task ExportToSqliteAsync_EmptyDatabase_CreatesFileWithSchemaAndMetadata()
	{
		// Act
		string path = await _service.ExportToSqliteAsync();
		_tempFiles.Add(path);

		// Assert
		File.Exists(path).Should().BeTrue();

		await using SqliteConnection conn = new($"Data Source={path}");
		await conn.OpenAsync();

		// Verify all tables exist
		string[] expectedTables = ["backup_metadata", "cards", "categories", "subcategories",
			"item_templates", "receipts", "receipt_items", "transactions", "adjustments"];

		foreach (string table in expectedTables)
		{
			await using SqliteCommand cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name";
			cmd.Parameters.AddWithValue("$name", table);
			object? result = await cmd.ExecuteScalarAsync();
			result.Should().NotBeNull($"table '{table}' should exist");
		}

		// Verify metadata
		await using SqliteCommand metaCmd = conn.CreateCommand();
		metaCmd.CommandText = "SELECT value FROM backup_metadata WHERE key='format'";
		string? format = (string?)await metaCmd.ExecuteScalarAsync();
		format.Should().Be("receipts-sqlite-backup");

		await using SqliteCommand versionCmd = conn.CreateCommand();
		versionCmd.CommandText = "SELECT value FROM backup_metadata WHERE key='export_version'";
		string? version = (string?)await versionCmd.ExecuteScalarAsync();
		version.Should().Be("4");
	}

	[Fact]
	public async Task ExportToSqliteAsync_WithAccounts_ExportsAllAccounts()
	{
		// Arrange
		Guid account1Id = Guid.NewGuid();
		Guid account2Id = Guid.NewGuid();
		await using (ApplicationDbContext ctx = await _dbContextFactory.CreateDbContextAsync())
		{
			ctx.Accounts.AddRange(
				new AccountEntity { Id = account1Id, Name = "Checking", IsActive = true },
				new AccountEntity { Id = account2Id, Name = "Savings", IsActive = false });
			ctx.Cards.AddRange(
				new CardEntity { Id = Guid.NewGuid(), CardCode = "1000", Name = "Checking", IsActive = true, AccountId = account1Id },
				new CardEntity { Id = Guid.NewGuid(), CardCode = "2000", Name = "Savings", IsActive = false, AccountId = account2Id });
			await ctx.SaveChangesAsync();
		}

		// Act
		string path = await _service.ExportToSqliteAsync();
		_tempFiles.Add(path);

		// Assert
		await using SqliteConnection conn = new($"Data Source={path}");
		await conn.OpenAsync();

		await using SqliteCommand cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT COUNT(*) FROM cards";
		long count = (long)(await cmd.ExecuteScalarAsync())!;
		count.Should().Be(2);

		cmd.CommandText = "SELECT name FROM cards WHERE card_code='1000'";
		string? name = (string?)await cmd.ExecuteScalarAsync();
		name.Should().Be("Checking");

		cmd.CommandText = "SELECT is_active FROM cards WHERE card_code='2000'";
		long isActive = (long)(await cmd.ExecuteScalarAsync())!;
		isActive.Should().Be(0);

		cmd.CommandText = "SELECT COUNT(*) FROM accounts";
		long accountCount = (long)(await cmd.ExecuteScalarAsync())!;
		accountCount.Should().Be(2);
	}

	[Fact]
	public async Task ExportToSqliteAsync_WithCategories_ExcludesSoftDeleted()
	{
		// Arrange
		await using (ApplicationDbContext ctx = await _dbContextFactory.CreateDbContextAsync())
		{
			ctx.Categories.AddRange(
				new CategoryEntity { Id = Guid.NewGuid(), Name = "Active", IsActive = true },
				new CategoryEntity { Id = Guid.NewGuid(), Name = "Deleted", IsActive = true, DeletedAt = DateTimeOffset.UtcNow });
			await ctx.SaveChangesAsync();
		}

		// Act
		string path = await _service.ExportToSqliteAsync();
		_tempFiles.Add(path);

		// Assert
		await using SqliteConnection conn = new($"Data Source={path}");
		await conn.OpenAsync();

		await using SqliteCommand cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT COUNT(*) FROM categories";
		long count = (long)(await cmd.ExecuteScalarAsync())!;
		count.Should().Be(1);

		cmd.CommandText = "SELECT name FROM categories";
		string? name = (string?)await cmd.ExecuteScalarAsync();
		name.Should().Be("Active");
	}

	[Fact]
	public async Task ExportToSqliteAsync_WithReceipts_ExportsReceiptData()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();

		await using (ApplicationDbContext ctx = await _dbContextFactory.CreateDbContextAsync())
		{
			ctx.Accounts.Add(new AccountEntity { Id = accountId, Name = "Checking", IsActive = true });
			ctx.Cards.Add(new CardEntity { Id = accountId, CardCode = "1000", Name = "Checking", IsActive = true, AccountId = accountId });

			ctx.Receipts.Add(new ReceiptEntity
			{
				Id = receiptId,
				Location = "Test Store",
				Date = new DateOnly(2024, 1, 15),
				TaxAmount = 5.50m,
				TaxAmountCurrency = Currency.USD,
			});

			ctx.ReceiptItems.Add(new ReceiptItemEntity
			{
				Id = Guid.NewGuid(),
				ReceiptId = receiptId,
				Description = "Test Item",
				Quantity = 2,
				UnitPrice = 10.00m,
				UnitPriceCurrency = Currency.USD,
				TotalAmount = 20.00m,
				TotalAmountCurrency = Currency.USD,
				Category = "Food",
			});

			ctx.Transactions.Add(new TransactionEntity
			{
				Id = Guid.NewGuid(),
				ReceiptId = receiptId,
				AccountId = accountId,
				CardId = accountId,
				Amount = 25.50m,
				AmountCurrency = Currency.USD,
				Date = new DateOnly(2024, 1, 15),
			});

			ctx.Adjustments.Add(new AdjustmentEntity
			{
				Id = Guid.NewGuid(),
				ReceiptId = receiptId,
				Type = AdjustmentType.Tip,
				Amount = 3.00m,
				AmountCurrency = Currency.USD,
				Description = "Tip",
			});

			await ctx.SaveChangesAsync();
		}

		// Act
		string path = await _service.ExportToSqliteAsync();
		_tempFiles.Add(path);

		// Assert
		await using SqliteConnection conn = new($"Data Source={path}");
		await conn.OpenAsync();

		await using SqliteCommand cmd = conn.CreateCommand();

		cmd.CommandText = "SELECT COUNT(*) FROM receipts";
		((long)(await cmd.ExecuteScalarAsync())!).Should().Be(1);

		cmd.CommandText = "SELECT location FROM receipts";
		((string?)(await cmd.ExecuteScalarAsync())).Should().Be("Test Store");

		cmd.CommandText = "SELECT COUNT(*) FROM receipt_items";
		((long)(await cmd.ExecuteScalarAsync())!).Should().Be(1);

		cmd.CommandText = "SELECT description FROM receipt_items";
		((string?)(await cmd.ExecuteScalarAsync())).Should().Be("Test Item");

		cmd.CommandText = "SELECT COUNT(*) FROM transactions";
		((long)(await cmd.ExecuteScalarAsync())!).Should().Be(1);

		cmd.CommandText = "SELECT COUNT(*) FROM adjustments";
		((long)(await cmd.ExecuteScalarAsync())!).Should().Be(1);

		cmd.CommandText = "SELECT type FROM adjustments";
		((string?)(await cmd.ExecuteScalarAsync())).Should().Be("Tip");
	}

	[Fact]
	public async Task ExportToSqliteAsync_WithSubcategories_ExportsWithForeignKeys()
	{
		// Arrange
		Guid categoryId = Guid.NewGuid();

		await using (ApplicationDbContext ctx = await _dbContextFactory.CreateDbContextAsync())
		{
			ctx.Categories.Add(new CategoryEntity
			{
				Id = categoryId,
				Name = "Groceries",
				IsActive = true,
			});
			ctx.Subcategories.Add(new SubcategoryEntity
			{
				Id = Guid.NewGuid(),
				Name = "Produce",
				CategoryId = categoryId,
				IsActive = true,
			});
			await ctx.SaveChangesAsync();
		}

		// Act
		string path = await _service.ExportToSqliteAsync();
		_tempFiles.Add(path);

		// Assert
		await using SqliteConnection conn = new($"Data Source={path}");
		await conn.OpenAsync();

		await using SqliteCommand cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT COUNT(*) FROM subcategories";
		long count = (long)(await cmd.ExecuteScalarAsync())!;
		count.Should().Be(1);

		cmd.CommandText = "SELECT category_id FROM subcategories";
		string? catId = (string?)await cmd.ExecuteScalarAsync();
		catId.Should().Be(categoryId.ToString());
	}

	[Fact]
	public async Task ExportToSqliteAsync_WithItemTemplates_ExportsTemplateData()
	{
		// Arrange
		await using (ApplicationDbContext ctx = await _dbContextFactory.CreateDbContextAsync())
		{
			ctx.ItemTemplates.Add(new ItemTemplateEntity
			{
				Id = Guid.NewGuid(),
				Name = "Milk",
				DefaultCategory = "Groceries",
				DefaultSubcategory = "Dairy",
				DefaultUnitPrice = 4.99m,
				DefaultUnitPriceCurrency = Currency.USD,
				DefaultItemCode = "MILK001",
				Description = "Whole milk",
			});
			await ctx.SaveChangesAsync();
		}

		// Act
		string path = await _service.ExportToSqliteAsync();
		_tempFiles.Add(path);

		// Assert
		await using SqliteConnection conn = new($"Data Source={path}");
		await conn.OpenAsync();

		await using SqliteCommand cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT COUNT(*) FROM item_templates";
		long count = (long)(await cmd.ExecuteScalarAsync())!;
		count.Should().Be(1);

		cmd.CommandText = "SELECT name FROM item_templates";
		string? name = (string?)await cmd.ExecuteScalarAsync();
		name.Should().Be("Milk");
	}

	[Fact]
	public async Task CreateSchemaAsync_CreatesAllExpectedTables()
	{
		// Arrange
		string path = Path.GetTempFileName();
		_tempFiles.Add(path);

		await using SqliteConnection conn = new($"Data Source={path}");
		await conn.OpenAsync();

		// Act
		await BackupService.CreateSchemaAsync(conn, CancellationToken.None);

		// Assert
		await using SqliteCommand cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();

		List<string> tables = [];
		while (await reader.ReadAsync())
		{
			tables.Add(reader.GetString(0));
		}

		tables.Should().Contain("backup_metadata");
		tables.Should().Contain("cards");
		tables.Should().Contain("categories");
		tables.Should().Contain("subcategories");
		tables.Should().Contain("item_templates");
		tables.Should().Contain("receipts");
		tables.Should().Contain("receipt_items");
		tables.Should().Contain("transactions");
		tables.Should().Contain("adjustments");
	}

	[Fact]
	public async Task CreateSchemaAsync_DoesNotCreateExcludedLogOrCursorTables()
	{
		// Documents the deliberate exclusions (RECEIPTS-802): append-only audit/history logs
		// and the re-fetchable YNAB delta cursor are intentionally NOT part of the backup schema.
		// Arrange
		string path = Path.GetTempFileName();
		_tempFiles.Add(path);

		await using SqliteConnection conn = new($"Data Source={path}");
		await conn.OpenAsync();

		// Act
		await BackupService.CreateSchemaAsync(conn, CancellationToken.None);

		// Assert
		await using SqliteCommand cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();

		List<string> tables = [];
		while (await reader.ReadAsync())
		{
			tables.Add(reader.GetString(0));
		}

		tables.Should().NotContain("audit_logs");
		tables.Should().NotContain("auth_audit_logs");
		tables.Should().NotContain("ynab_sync_events");
		tables.Should().NotContain("ynab_server_knowledge");
	}

	public void Dispose()
	{
		foreach (string path in _tempFiles)
		{
			try
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
			catch
			{
				// Best effort cleanup
			}
		}

		GC.SuppressFinalize(this);
	}

	private sealed class TestDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ApplicationDbContext(options));
	}
}
