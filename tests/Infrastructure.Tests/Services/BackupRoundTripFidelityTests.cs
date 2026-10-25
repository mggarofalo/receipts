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
		// Production migrations seed taxonomy; avoid unrelated business seeds here.
		foreach (var options in new[] { _sourceOptions, _targetOptions })
		{
			await using var db = new ApplicationDbContext(options);
			if (!await db.TaxonomyHeads.AnyAsync())
			{
				var genesis = Infrastructure.Configurations.TaxonomyConfiguration.GenesisId;
				db.TaxonomyRevisions.Add(new()
				{
					Id = genesis,
					Actor = "system",
					OperationId = genesis,
					Reason = "Empty taxonomy foundation; legacy import is separate.",
					RecordedAt = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero)
				});
				db.TaxonomyHeads.Add(new() { Id = 1, RevisionId = genesis });
				await db.SaveChangesAsync();
			}
		}
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
		CultureInfo german = CultureInfo.GetCultureInfo("de-DE");
		try
		{
			// CurrentCulture flows across awaits; changing the process default would leak
			// German formatting into unrelated tests running in parallel (RECEIPTS-980).
			CultureInfo.CurrentCulture = german;

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
		}
	}

	// RECEIPTS-770 + RECEIPTS-789: the v4 export adds the YNAB config/state tables, normalized
	// descriptions, and receipt image paths. All must round-trip faithfully.
	[Fact]
	public async Task RoundTrip_V4Tables_AndImagePaths_RoundTripFaithfully()
	{
		const string canonicalBudgetId = "11111111-1111-1111-1111-111111111111";
		const string legacyBudgetId = "legacy-budget-name";
		string nonCanonicalBudgetId = $"  {{{canonicalBudgetId.ToUpperInvariant()}}}  ";
		Guid accountId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		Guid txId = Guid.NewGuid();
		Guid budgetRowId = Guid.NewGuid();
		Guid acctMapId = Guid.NewGuid();
		Guid catMapId = Guid.NewGuid();
		Guid syncId = Guid.NewGuid();
		Guid legacyBudgetRowId = Guid.NewGuid();
		Guid legacyAcctMapId = Guid.NewGuid();
		Guid legacyCatMapId = Guid.NewGuid();
		Guid legacySyncId = Guid.NewGuid();
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

				CardId = accountId,
				Amount = 10m,
				AmountCurrency = Currency.USD,
				Date = new DateOnly(2024, 1, 15),
			});
			ctx.YnabSelectedBudgets.AddRange(
				new YnabSelectedBudgetEntity { Id = budgetRowId, BudgetId = nonCanonicalBudgetId, UpdatedAt = ts },
				new YnabSelectedBudgetEntity { Id = legacyBudgetRowId, BudgetId = legacyBudgetId, UpdatedAt = ts });
			ctx.YnabAccountMappings.Add(new YnabAccountMappingEntity
			{
				Id = acctMapId,
				ReceiptsAccountId = accountId,
				YnabAccountId = "ynab-acct-1",
				YnabAccountName = "YNAB Checking",
				YnabBudgetId = nonCanonicalBudgetId,
				CreatedAt = ts,
				UpdatedAt = ts,
			});
			ctx.YnabAccountMappings.Add(new YnabAccountMappingEntity
			{
				Id = legacyAcctMapId,
				ReceiptsAccountId = accountId,
				YnabAccountId = "legacy-ynab-acct",
				YnabAccountName = "Legacy YNAB Checking",
				YnabBudgetId = legacyBudgetId,
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
				YnabBudgetId = nonCanonicalBudgetId,
				CreatedAt = ts,
				UpdatedAt = ts,
			});
			ctx.YnabCategoryMappings.Add(new YnabCategoryMappingEntity
			{
				Id = legacyCatMapId,
				ReceiptsCategory = "Legacy Food",
				YnabCategoryId = "legacy-ynab-cat",
				YnabCategoryName = "Legacy Groceries",
				YnabCategoryGroupName = "Legacy",
				YnabBudgetId = legacyBudgetId,
				CreatedAt = ts,
				UpdatedAt = ts,
			});
			ctx.YnabSyncRecords.Add(new YnabSyncRecordEntity
			{
				Id = syncId,
				LocalTransactionId = txId,
				YnabTransactionId = "ynab-txn-1",
				YnabBudgetId = nonCanonicalBudgetId,
				YnabAccountId = "ynab-acct-1",
				ImportId = "YNAB:-12345:2024-01-15:abc123:1",
				RequestPayloadJson = "{\"accountId\":\"ynab-acct-1\",\"amount\":-12345}",
				PayloadHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
				SourceVersion = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
				AttemptCount = 3,
				ClaimToken = syncId,
				ClaimedAtUtc = ts.AddMinutes(-2),
				LastAttemptAtUtc = ts.AddMinutes(-2),
				SyncType = YnabSyncType.TransactionPush,
				SyncStatus = YnabSyncStatus.Synced,
				SyncedAtUtc = ts,
				LastError = null,
				CreatedAt = ts,
				UpdatedAt = ts,
			});
			ctx.YnabSyncRecords.Add(new YnabSyncRecordEntity
			{
				Id = legacySyncId,
				LocalTransactionId = txId,
				YnabTransactionId = "legacy-ynab-txn",
				YnabBudgetId = legacyBudgetId,
				SyncType = YnabSyncType.MemoUpdate,
				SyncStatus = YnabSyncStatus.Synced,
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

		result.YnabSelectedBudgetsCreated.Should().Be(2);
		result.YnabAccountMappingsCreated.Should().Be(2);
		result.YnabCategoryMappingsCreated.Should().Be(2);
		result.YnabSyncRecordsCreated.Should().Be(2);
		result.NormalizedDescriptionsCreated.Should().Be(1);
		result.NormalizedDescriptionSettingsCreated.Should().Be(1);

		await using ApplicationDbContext assert = Target();

		ReceiptEntity receipt = (await assert.Receipts.FindAsync(receiptId))!;
		receipt.OriginalImagePath.Should().Be("receipts/orig/abc.jpg");
		receipt.ProcessedImagePath.Should().Be("receipts/proc/abc.png");

		YnabSelectedBudgetEntity budget = (await assert.YnabSelectedBudgets.FindAsync(budgetRowId))!;
		budget.BudgetId.Should().Be(canonicalBudgetId);
		budget.UpdatedAt.Should().Be(ts);
		(await assert.YnabSelectedBudgets.FindAsync(legacyBudgetRowId))!.BudgetId.Should().Be(legacyBudgetId);

		YnabAccountMappingEntity acctMap = (await assert.YnabAccountMappings.FindAsync(acctMapId))!;
		acctMap.ReceiptsAccountId.Should().Be(accountId);
		acctMap.YnabAccountId.Should().Be("ynab-acct-1");
		acctMap.YnabAccountName.Should().Be("YNAB Checking");
		acctMap.YnabBudgetId.Should().Be(canonicalBudgetId);
		(await assert.YnabAccountMappings.FindAsync(legacyAcctMapId))!.YnabBudgetId.Should().Be(legacyBudgetId);

		YnabCategoryMappingEntity catMap = (await assert.YnabCategoryMappings.FindAsync(catMapId))!;
		catMap.ReceiptsCategory.Should().Be("Food");
		catMap.YnabCategoryId.Should().Be("ynab-cat-1");
		catMap.YnabCategoryName.Should().Be("Groceries");
		catMap.YnabCategoryGroupName.Should().Be("Everyday");
		catMap.YnabBudgetId.Should().Be(canonicalBudgetId);
		(await assert.YnabCategoryMappings.FindAsync(legacyCatMapId))!.YnabBudgetId.Should().Be(legacyBudgetId);

		YnabSyncRecordEntity sync = (await assert.YnabSyncRecords.FindAsync(syncId))!;
		sync.LocalTransactionId.Should().Be(txId);
		sync.YnabTransactionId.Should().Be("ynab-txn-1");
		sync.SyncType.Should().Be(YnabSyncType.TransactionPush);
		sync.SyncStatus.Should().Be(YnabSyncStatus.Synced);
		sync.SyncedAtUtc.Should().Be(ts);
		sync.ImportId.Should().Be("YNAB:-12345:2024-01-15:abc123:1");
		sync.RequestPayloadJson.Should().Be("{\"accountId\":\"ynab-acct-1\",\"amount\":-12345}");
		sync.PayloadHash.Should().Be("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
		sync.SourceVersion.Should().Be("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");
		sync.AttemptCount.Should().Be(3);
		sync.ClaimToken.Should().Be(syncId);
		sync.ClaimedAtUtc.Should().Be(ts.AddMinutes(-2));
		sync.LastAttemptAtUtc.Should().Be(ts.AddMinutes(-2));
		sync.DeletedAt.Should().BeNull();
		sync.YnabBudgetId.Should().Be(canonicalBudgetId);
		(await assert.YnabSyncRecords.FindAsync(legacySyncId))!.YnabBudgetId.Should().Be(legacyBudgetId);

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
