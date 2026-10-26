using System.Globalization;
using System.Text.Json;
using Application.Models;
using Common;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

/// <summary>
/// End-to-end export → import round-trip coverage for RECEIPTS-771/770/789: culture-safe decimals,
/// the v4 YNAB/normalized-description tables, receipt image paths, and backward compatibility with
/// pre-v4 backups. Export and import use separate, migrated PostgreSQL databases.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class BackupRoundTripFidelityTests(PostgresFixture server) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
	private PostgresFixture _source = null!;
	private PostgresFixture _target = null!;

	public async Task InitializeAsync()
	{
		_source = await server.CreateIsolatedAsync();
		try
		{
			_target = await server.CreateIsolatedAsync();
			// Migrations seed default settings; this test supplies its own row
			// and expects the destination to begin without application data.
			await using (ApplicationDbContext source = Source())
			{
				await source.NormalizedDescriptionSettings.ExecuteDeleteAsync();
			}

			await using (ApplicationDbContext target = Target())
			{
				await target.NormalizedDescriptionSettings.ExecuteDeleteAsync();
			}
		}
		catch
		{
			if (_target is not null)
			{
				await _target.DisposeAsync();
			}

			await _source.DisposeAsync();
			throw;
		}
	}

	private ApplicationDbContext Source() => _source.CreateDbContext();
	private ApplicationDbContext Target() => _target.CreateDbContext();

	private async Task<BackupImportResult> RoundTripAsync()
	{
		BackupService exporter = new(new Factory(_source.CreateOptions()), NullLogger<BackupService>.Instance);
		BackupImportService importer = new(new Factory(_target.CreateOptions()), NullLogger<BackupImportService>.Instance);
		string path = await exporter.ExportToSqliteAsync();
		// The exporter uses a pooled SQLite connection; clear the pool so the underlying file
		// handle is released before we open the file for import (Windows otherwise reports the
		// file as locked). Production streams the file via FileOptions.DeleteOnClose instead.
		SqliteConnection.ClearAllPools();
		try
		{
			await using FileStream stream = File.OpenRead(path);
			return await importer.ImportFromSqliteAsync(stream, CancellationToken.None);
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


	[Fact]
	public async Task V13_YnabStableMappings_RestoreFreshReplayAndPreserveOnV12Import()
	{
		Guid categoryId = Guid.NewGuid(), revisionId = Guid.NewGuid(), mappingId = Guid.NewGuid(), operationId = Guid.NewGuid();
		Guid ambiguousA = Guid.NewGuid(), ambiguousB = Guid.NewGuid(), otherRoot = Guid.NewGuid();
		Guid ambiguousMappingId = Guid.NewGuid();
		string ambiguousCandidates = $"[\"{ambiguousA}\",\"{ambiguousB}\"]";
		DateTimeOffset recordedAt = DateTimeOffset.UtcNow;
		const string candidateJson = "[]";
		await using (ApplicationDbContext source = Source())
		{
			await using var transaction = await source.Database.BeginTransactionAsync();
			source.TaxonomyIdentities.AddRange(new[] { categoryId, ambiguousA, ambiguousB, otherRoot }
					.Select(id => new TaxonomyIdentityEntity { Id = id }));
			source.TaxonomyRevisions.Add(new TaxonomyRevisionEntity
			{
				Id = revisionId,
				Sequence = 1,
				PreviousRevisionId = (await source.TaxonomyHeads.SingleAsync()).RevisionId,
				RecordedAt = recordedAt,
				Actor = "backup-test",
				Reason = "Create category",
				OperationId = operationId,
				AffectedCount = 4,
			});
			source.TaxonomyOperations.Add(new TaxonomyOperationEntity
			{
				Actor = "backup-test",
				OperationId = operationId,
				RequestHash = new string('a', 64),
				RevisionId = revisionId,
				AffectedCount = 4,
				RecordedAt = recordedAt,
				Reason = "Create category",
			});
			source.TaxonomyNodes.AddRange(
					new TaxonomyNodeEntity { RevisionId = revisionId, CategoryId = categoryId, Name = "Food" },
					new TaxonomyNodeEntity { RevisionId = revisionId, CategoryId = otherRoot, Name = "Home" },
					new TaxonomyNodeEntity { RevisionId = revisionId, CategoryId = ambiguousA, ParentId = categoryId, Name = "Shared" },
					new TaxonomyNodeEntity { RevisionId = revisionId, CategoryId = ambiguousB, ParentId = otherRoot, Name = "Shared" });
			(await source.TaxonomyHeads.SingleAsync()).RevisionId = revisionId;
			source.YnabCategoryMappings.Add(new YnabCategoryMappingEntity
			{
				Id = mappingId,
				ReceiptsCategory = "Food",
				ReceiptsCategoryId = categoryId,
				LegacyResolution = "Explicit",
				LegacyCandidateCategoryIds = candidateJson,
				YnabCategoryId = "ynab-food",
				YnabCategoryName = "Food",
				YnabCategoryGroupName = "Daily",
				YnabBudgetId = "budget",
				CreatedAt = DateTimeOffset.UtcNow,
				UpdatedAt = DateTimeOffset.UtcNow,
			});
			source.YnabCategoryMappings.Add(new YnabCategoryMappingEntity
			{
				Id = ambiguousMappingId,
				ReceiptsCategory = "Shared",
				ReceiptsCategoryId = null,
				LegacyResolution = "Ambiguous",
				LegacyCandidateCategoryIds = ambiguousCandidates,
				YnabCategoryId = "ynab-shared",
				YnabCategoryName = "Shared",
				YnabCategoryGroupName = "Daily",
				YnabBudgetId = "budget",
				CreatedAt = recordedAt,
				UpdatedAt = recordedAt,
			});
			await source.SaveChangesAsync();
			await transaction.CommitAsync();
		}

		string path = await new BackupService(new Factory(_source.CreateOptions()), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		SqliteConnection.ClearAllPools();
		try
		{
			BackupImportService importer = new(new Factory(_target.CreateOptions()), NullLogger<BackupImportService>.Instance);
			async Task Restore()
			{
				await using FileStream stream = File.OpenRead(path);
				await importer.ImportFromSqliteAsync(stream, CancellationToken.None);
			}
			await Restore();
			await using (ApplicationDbContext target = Target())
			{
				YnabCategoryMappingEntity row = (await target.YnabCategoryMappings.FindAsync(mappingId))!;
				row.ReceiptsCategoryId.Should().Be(categoryId);
				row.LegacyResolution.Should().Be("Explicit");
				row.LegacyCandidateCategoryIds.Should().Be(candidateJson);
				YnabCategoryMappingEntity ambiguous = (await target.YnabCategoryMappings.FindAsync(ambiguousMappingId))!;
				ambiguous.ReceiptsCategoryId.Should().BeNull();
				ambiguous.LegacyResolution.Should().Be("Ambiguous");
				JsonSerializer.Deserialize<Guid[]>(ambiguous.LegacyCandidateCategoryIds)
						.Should().Equal(ambiguousA, ambiguousB);
			}

			// A second archive must not silently rebind an existing mapping ID.
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "UPDATE ynab_category_mappings SET receipts_category_id=$replacement WHERE id=$id";
				command.Parameters.AddWithValue("$replacement", otherRoot.ToString());
				command.Parameters.AddWithValue("$id", mappingId.ToString());
				await command.ExecuteNonQueryAsync();
			}
			await FluentActions.Invoking(Restore).Should().ThrowAsync<InvalidOperationException>();
			await using (ApplicationDbContext target = Target())
			{
				(await target.YnabCategoryMappings.FindAsync(mappingId))!
						.ReceiptsCategoryId.Should().Be(categoryId);
			}
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "UPDATE ynab_category_mappings SET receipts_category_id=$original WHERE id=$id";
				command.Parameters.AddWithValue("$original", categoryId.ToString());
				command.Parameters.AddWithValue("$id", mappingId.ToString());
				await command.ExecuteNonQueryAsync();
			}
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "UPDATE ynab_category_mappings SET ynab_budget_id='other-budget' WHERE id=$id";
				command.Parameters.AddWithValue("$id", mappingId.ToString());
				await command.ExecuteNonQueryAsync();
			}
			await FluentActions.Invoking(Restore).Should().ThrowAsync<InvalidOperationException>();
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "UPDATE ynab_category_mappings SET ynab_budget_id='budget' WHERE id=$id";
				command.Parameters.AddWithValue("$id", mappingId.ToString());
				await command.ExecuteNonQueryAsync();
			}
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "UPDATE ynab_category_mappings SET receipts_category_id=NULL, legacy_resolution='Unmatched' WHERE id=$id";
				command.Parameters.AddWithValue("$id", mappingId.ToString());
				await command.ExecuteNonQueryAsync();
			}
			await FluentActions.Invoking(Restore).Should().ThrowAsync<InvalidOperationException>();
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "UPDATE ynab_category_mappings SET receipts_category_id=$original, legacy_resolution='Explicit' WHERE id=$id";
				command.Parameters.AddWithValue("$original", categoryId.ToString());
				command.Parameters.AddWithValue("$id", mappingId.ToString());
				await command.ExecuteNonQueryAsync();
			}
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "UPDATE ynab_category_mappings SET receipts_category_id='bad-guid' WHERE id=$id";
				command.Parameters.AddWithValue("$id", mappingId.ToString());
				await command.ExecuteNonQueryAsync();
			}
			await FluentActions.Invoking(Restore).Should().ThrowAsync<InvalidOperationException>()
					.WithMessage("*Invalid stable category ID*");
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "UPDATE ynab_category_mappings SET receipts_category_id=$original WHERE id=$id";
				command.Parameters.AddWithValue("$original", categoryId.ToString());
				command.Parameters.AddWithValue("$id", mappingId.ToString());
				await command.ExecuteNonQueryAsync();
			}
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "UPDATE ynab_category_mappings SET legacy_candidate_category_ids=$candidates WHERE id=$id";
				command.Parameters.AddWithValue("$candidates", $"[\"{ambiguousA}\",\"{Guid.NewGuid()}\"]");
				command.Parameters.AddWithValue("$id", ambiguousMappingId.ToString());
				await command.ExecuteNonQueryAsync();
			}
			await FluentActions.Invoking(Restore).Should().ThrowAsync<InvalidOperationException>()
					.WithMessage("*Unknown taxonomy identity*");
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "UPDATE ynab_category_mappings SET legacy_candidate_category_ids=$candidates WHERE id=$id";
				command.Parameters.AddWithValue("$candidates", ambiguousCandidates);
				command.Parameters.AddWithValue("$id", ambiguousMappingId.ToString());
				await command.ExecuteNonQueryAsync();
			}
			await using (ApplicationDbContext target = Target())
			{
				YnabCategoryMappingEntity row = (await target.YnabCategoryMappings.FindAsync(mappingId))!;
				row.ReceiptsCategoryId = null;
				row.LegacyResolution = "Unmatched";
				await target.SaveChangesAsync();
			}
			await Restore();
			await using (ApplicationDbContext target = Target())
			{
				YnabCategoryMappingEntity row = (await target.YnabCategoryMappings.FindAsync(mappingId))!;
				row.ReceiptsCategoryId.Should().Be(categoryId);
				row.LegacyResolution.Should().Be("Explicit");
			}
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "UPDATE ynab_category_mappings SET legacy_resolution='Unique'";
				await command.ExecuteNonQueryAsync();
			}
			await FluentActions.Invoking(Restore).Should().ThrowAsync<InvalidOperationException>();
			await using (ApplicationDbContext target = Target())
			{
				(await target.YnabCategoryMappings.FindAsync(mappingId))!.ReceiptsCategoryId.Should().Be(categoryId);
			}
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "UPDATE ynab_category_mappings SET legacy_resolution='Explicit'";
				await command.ExecuteNonQueryAsync();
			}

			// A v12 file has only the legacy text; replay must retain the bound identity.
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "UPDATE backup_metadata SET value='12' WHERE key='export_version'; " +
						"ALTER TABLE ynab_category_mappings DROP COLUMN receipts_category_id; " +
						"ALTER TABLE ynab_category_mappings DROP COLUMN legacy_resolution; " +
						"ALTER TABLE ynab_category_mappings DROP COLUMN legacy_candidate_category_ids";
				await command.ExecuteNonQueryAsync();
			}
			await Restore();
			await using (ApplicationDbContext target = Target())
			{
				YnabCategoryMappingEntity row = (await target.YnabCategoryMappings.FindAsync(mappingId))!;
				row.ReceiptsCategoryId.Should().Be(categoryId);
				row.LegacyResolution.Should().Be("Explicit");
			}
			await using PostgresFixture olderTarget = await server.CreateIsolatedAsync();
			BackupImportService olderImporter = new(new Factory(olderTarget.CreateOptions()), NullLogger<BackupImportService>.Instance);
			await using (FileStream stream = File.OpenRead(path))
			{
				await olderImporter.ImportFromSqliteAsync(stream, CancellationToken.None);
			}
			await using (ApplicationDbContext older = olderTarget.CreateDbContext())
			{
				YnabCategoryMappingEntity row = (await older.YnabCategoryMappings.FindAsync(mappingId))!;
				row.ReceiptsCategoryId.Should().BeNull();
				row.LegacyResolution.Should().Be("Unmatched");
				(await older.YnabCategoryMappings.FindAsync(ambiguousMappingId))!
						.ReceiptsCategoryId.Should().BeNull();
			}
		}
		finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
	}

	public async Task DisposeAsync()
	{
		if (_target is not null)
		{
			await _target.DisposeAsync();
		}

		if (_source is not null)
		{
			await _source.DisposeAsync();
		}
	}

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
			Task.FromResult(new ApplicationDbContext(options));
	}
}
