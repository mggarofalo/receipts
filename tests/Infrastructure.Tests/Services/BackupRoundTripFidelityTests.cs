using System.Globalization;
using Application.Models;
using Common;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Services;

/// <summary>
/// End-to-end export → import round-trip coverage for RECEIPTS-771/770/789: culture-safe decimals,
/// the v4 YNAB/normalized-description tables, receipt image paths, and backward compatibility with
/// pre-v4 backups. Export runs against one in-memory database and import against a second so the
/// round-trip is genuine.
/// </summary>
public class BackupRoundTripFidelityTests : IDisposable
{
	private readonly DbContextOptions<ApplicationDbContext> _sourceOptions;
	private readonly DbContextOptions<ApplicationDbContext> _targetOptions;
	private readonly BackupService _exportService;
	private readonly BackupImportService _importService;
	private readonly string _tempDir;

	public BackupRoundTripFidelityTests()
	{
		_sourceOptions = BuildOptions($"BackupRtSource_{Guid.NewGuid()}");
		_targetOptions = BuildOptions($"BackupRtTarget_{Guid.NewGuid()}");
		_exportService = new BackupService(new Factory(_sourceOptions), NullLogger<BackupService>.Instance);
		_importService = new BackupImportService(new Factory(_targetOptions), NullLogger<BackupImportService>.Instance);
		_tempDir = Path.Combine(Path.GetTempPath(), $"backup-roundtrip-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
	}

	private static DbContextOptions<ApplicationDbContext> BuildOptions(string name) =>
		new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase(name)
			.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
			.Options;

	private ApplicationDbContext Source() => new(_sourceOptions);
	private ApplicationDbContext Target() => new(_targetOptions);

	private async Task<BackupImportResult> RoundTripAsync()
	{
		string path = await _exportService.ExportToSqliteAsync();
		// The exporter uses a pooled SQLite connection; clear the pool so the underlying file
		// handle is released before we open the file for import (Windows otherwise reports the
		// file as locked). Production streams the file via FileOptions.DeleteOnClose instead.
		SqliteConnection.ClearAllPools();
		try
		{
			await using FileStream stream = File.OpenRead(path);
			return await _importService.ImportFromSqliteAsync(stream, CancellationToken.None);
		}
		finally
		{
			try
			{
				File.Delete(path);
			}
			catch
			{
				// Best effort cleanup
			}
		}
	}

	// RECEIPTS-771: on a comma-decimal host (de-DE) the old exporter wrote "12,34" for 12.34m via
	// CurrentCulture, and the invariant importer read that back as 1234 — a silent 100x corruption.
	// The exporter now writes invariant text, so every amount round-trips regardless of host culture.
	[Fact]
	public async Task RoundTrip_UnderGermanCulture_PreservesDecimalValues()
	{
		CultureInfo previousCulture = CultureInfo.CurrentCulture;
		CultureInfo? previousDefault = CultureInfo.DefaultThreadCurrentCulture;
		CultureInfo german = CultureInfo.GetCultureInfo("de-DE");
		try
		{
			CultureInfo.CurrentCulture = german;
			CultureInfo.DefaultThreadCurrentCulture = german;

			Guid accountId = Guid.NewGuid();
			Guid receiptId = Guid.NewGuid();
			Guid itemId = Guid.NewGuid();
			Guid txId = Guid.NewGuid();
			Guid adjId = Guid.NewGuid();
			Guid templateId = Guid.NewGuid();

			await using (ApplicationDbContext ctx = Source())
			{
				ctx.Accounts.Add(new AccountEntity { Id = accountId, Name = "Checking", IsActive = true });
				ctx.Cards.Add(new CardEntity { Id = accountId, CardCode = "1000", Name = "Checking", IsActive = true, AccountId = accountId });
				ctx.Receipts.Add(new ReceiptEntity
				{
					Id = receiptId,
					Location = "Kaufland",
					Date = new DateOnly(2024, 3, 4),
					TaxAmount = 12.34m,
					TaxAmountCurrency = Currency.USD,
				});
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
				ctx.Transactions.Add(new TransactionEntity
				{
					Id = txId,
					ReceiptId = receiptId,
					AccountId = accountId,
					CardId = accountId,
					Amount = 99.99m,
					AmountCurrency = Currency.USD,
					Date = new DateOnly(2024, 3, 4),
				});
				ctx.Adjustments.Add(new AdjustmentEntity
				{
					Id = adjId,
					ReceiptId = receiptId,
					Type = AdjustmentType.Tip,
					Amount = 7.77m,
					AmountCurrency = Currency.USD,
				});
				ctx.ItemTemplates.Add(new ItemTemplateEntity
				{
					Id = templateId,
					Name = "Butter",
					DefaultUnitPrice = 4.99m,
					DefaultUnitPriceCurrency = Currency.USD,
				});
				await ctx.SaveChangesAsync();
			}

			await RoundTripAsync();

			await using ApplicationDbContext assert = Target();
			(await assert.Receipts.FindAsync(receiptId))!.TaxAmount.Should().Be(12.34m);
			ReceiptItemEntity item = (await assert.ReceiptItems.FindAsync(itemId))!;
			item.Quantity.Should().Be(2.5m);
			item.UnitPrice.Should().Be(1234.56m);
			item.TotalAmount.Should().Be(3086.40m);
			(await assert.Transactions.FindAsync(txId))!.Amount.Should().Be(99.99m);
			(await assert.Adjustments.FindAsync(adjId))!.Amount.Should().Be(7.77m);
			(await assert.ItemTemplates.FindAsync(templateId))!.DefaultUnitPrice.Should().Be(4.99m);
		}
		finally
		{
			CultureInfo.CurrentCulture = previousCulture;
			CultureInfo.DefaultThreadCurrentCulture = previousDefault;
		}
	}

	// RECEIPTS-770 + RECEIPTS-789: the v4 export adds the YNAB config/state tables, normalized
	// descriptions, and receipt image paths. All must round-trip faithfully.
	[Fact]
	public async Task RoundTrip_V4Tables_AndImagePaths_RoundTripFaithfully()
	{
		Guid accountId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		Guid txId = Guid.NewGuid();
		Guid budgetRowId = Guid.NewGuid();
		Guid acctMapId = Guid.NewGuid();
		Guid catMapId = Guid.NewGuid();
		Guid syncId = Guid.NewGuid();
		Guid normId = Guid.NewGuid();
		Guid settingsId = Guid.NewGuid();
		DateTimeOffset ts = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

		await using (ApplicationDbContext ctx = Source())
		{
			ctx.Accounts.Add(new AccountEntity { Id = accountId, Name = "Checking", IsActive = true });
			ctx.Cards.Add(new CardEntity { Id = accountId, CardCode = "1000", Name = "Checking", IsActive = true, AccountId = accountId });
			ctx.Receipts.Add(new ReceiptEntity
			{
				Id = receiptId,
				Location = "Target",
				Date = new DateOnly(2024, 1, 15),
				TaxAmount = 1.23m,
				TaxAmountCurrency = Currency.USD,
				OriginalImagePath = "receipts/orig/abc.jpg",
				ProcessedImagePath = "receipts/proc/abc.png",
			});
			ctx.Transactions.Add(new TransactionEntity
			{
				Id = txId,
				ReceiptId = receiptId,
				AccountId = accountId,
				CardId = accountId,
				Amount = 10m,
				AmountCurrency = Currency.USD,
				Date = new DateOnly(2024, 1, 15),
			});
			ctx.YnabSelectedBudgets.Add(new YnabSelectedBudgetEntity { Id = budgetRowId, BudgetId = "budget-123", UpdatedAt = ts });
			ctx.YnabAccountMappings.Add(new YnabAccountMappingEntity
			{
				Id = acctMapId,
				ReceiptsAccountId = accountId,
				YnabAccountId = "ynab-acct-1",
				YnabAccountName = "YNAB Checking",
				YnabBudgetId = "budget-123",
				CreatedAt = ts,
				UpdatedAt = ts,
			});
			ctx.YnabCategoryMappings.Add(new YnabCategoryMappingEntity
			{
				Id = catMapId,
				ReceiptsCategory = "Food",
				YnabCategoryId = "ynab-cat-1",
				YnabCategoryName = "Groceries",
				YnabCategoryGroupName = "Everyday",
				YnabBudgetId = "budget-123",
				CreatedAt = ts,
				UpdatedAt = ts,
			});
			ctx.YnabSyncRecords.Add(new YnabSyncRecordEntity
			{
				Id = syncId,
				LocalTransactionId = txId,
				YnabTransactionId = "ynab-txn-1",
				YnabBudgetId = "budget-123",
				YnabAccountId = "ynab-acct-1",
				SyncType = YnabSyncType.TransactionPush,
				SyncStatus = YnabSyncStatus.Synced,
				SyncedAtUtc = ts,
				LastError = null,
				CreatedAt = ts,
				UpdatedAt = ts,
			});
			ctx.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normId,
				CanonicalName = "milk",
				Status = NormalizedDescriptionStatus.PendingReview,
				CreatedAt = ts,
			});
			ctx.NormalizedDescriptionSettings.Add(new NormalizedDescriptionSettingsEntity
			{
				Id = settingsId,
				AutoAcceptThreshold = 0.87,
				PendingReviewThreshold = 0.62,
				UpdatedAt = ts,
			});
			await ctx.SaveChangesAsync();
		}

		BackupImportResult result = await RoundTripAsync();

		result.YnabSelectedBudgetsCreated.Should().Be(1);
		result.YnabAccountMappingsCreated.Should().Be(1);
		result.YnabCategoryMappingsCreated.Should().Be(1);
		result.YnabSyncRecordsCreated.Should().Be(1);
		result.NormalizedDescriptionsCreated.Should().Be(1);
		result.NormalizedDescriptionSettingsCreated.Should().Be(1);

		await using ApplicationDbContext assert = Target();

		ReceiptEntity receipt = (await assert.Receipts.FindAsync(receiptId))!;
		receipt.OriginalImagePath.Should().Be("receipts/orig/abc.jpg");
		receipt.ProcessedImagePath.Should().Be("receipts/proc/abc.png");

		YnabSelectedBudgetEntity budget = (await assert.YnabSelectedBudgets.FindAsync(budgetRowId))!;
		budget.BudgetId.Should().Be("budget-123");
		budget.UpdatedAt.Should().Be(ts);

		YnabAccountMappingEntity acctMap = (await assert.YnabAccountMappings.FindAsync(acctMapId))!;
		acctMap.ReceiptsAccountId.Should().Be(accountId);
		acctMap.YnabAccountId.Should().Be("ynab-acct-1");
		acctMap.YnabAccountName.Should().Be("YNAB Checking");
		acctMap.YnabBudgetId.Should().Be("budget-123");

		YnabCategoryMappingEntity catMap = (await assert.YnabCategoryMappings.FindAsync(catMapId))!;
		catMap.ReceiptsCategory.Should().Be("Food");
		catMap.YnabCategoryId.Should().Be("ynab-cat-1");
		catMap.YnabCategoryName.Should().Be("Groceries");
		catMap.YnabCategoryGroupName.Should().Be("Everyday");

		YnabSyncRecordEntity sync = (await assert.YnabSyncRecords.FindAsync(syncId))!;
		sync.LocalTransactionId.Should().Be(txId);
		sync.YnabTransactionId.Should().Be("ynab-txn-1");
		sync.SyncType.Should().Be(YnabSyncType.TransactionPush);
		sync.SyncStatus.Should().Be(YnabSyncStatus.Synced);
		sync.SyncedAtUtc.Should().Be(ts);
		sync.DeletedAt.Should().BeNull();

		NormalizedDescriptionEntity norm = (await assert.NormalizedDescriptions.FindAsync(normId))!;
		norm.CanonicalName.Should().Be("milk");
		norm.Status.Should().Be(NormalizedDescriptionStatus.PendingReview);
		norm.CreatedAt.Should().Be(ts);
		norm.Embedding.Should().BeNull("embeddings are regenerated after restore, not carried in the backup");

		NormalizedDescriptionSettingsEntity settings = (await assert.NormalizedDescriptionSettings.FindAsync(settingsId))!;
		settings.AutoAcceptThreshold.Should().Be(0.87);
		settings.PendingReviewThreshold.Should().Be(0.62);
		settings.UpdatedAt.Should().Be(ts);
	}

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

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
			Task.FromResult(new ApplicationDbContext(options));
	}
}
