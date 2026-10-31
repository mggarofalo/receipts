using System.Globalization;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models;
using Application.Utilities;
using Common;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Infrastructure.Services;

public partial class BackupImportService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	ILogger<BackupImportService> logger) : IBackupImportService
{
	// Bounds the restored SQLite file, after decompression. Uploads are capped separately at the HTTP edge.
	internal const long MaxSqliteBytes = 1024L * 1024 * 1024; // 1 GiB

	public Task<BackupImportResult> ImportFromSqliteAsync(Stream sqliteStream, CancellationToken cancellationToken)
		=> ImportFromSqliteCoreAsync(sqliteStream, cancellationToken, requireCandidate: false);

	// Internal evaluation seam that requires a v14 archive. Public import also accepts v14.
	internal Task<BackupImportResult> ImportCandidateFromSqliteAsync(Stream sqliteStream, CancellationToken cancellationToken)
		=> ImportFromSqliteCoreAsync(sqliteStream, cancellationToken, requireCandidate: true);

	private async Task<BackupImportResult> ImportFromSqliteCoreAsync(Stream sqliteStream, CancellationToken cancellationToken, bool requireCandidate)
	{
		string tempPath = Path.GetTempFileName();
		try
		{
			await using (FileStream fs = File.Create(tempPath))
			{
				try
				{
					await PortableBackupCompression.CopyAsSqliteAsync(sqliteStream, fs, MaxSqliteBytes, cancellationToken);
				}
				catch (InvalidDataException exception)
				{
					throw new InvalidOperationException("The uploaded file is not a valid gzip-compressed backup.", exception);
				}
			}

			return await ImportFromFileAsync(tempPath, cancellationToken, requireCandidate);
		}
		finally
		{
			try
			{
				File.Delete(tempPath);
			}
			catch
			{
				// Best effort cleanup
			}
		}
	}

	private async Task<BackupImportResult> ImportFromFileAsync(string sqlitePath, CancellationToken cancellationToken, bool requireCandidate)
	{
		ValidateSqliteFile(sqlitePath);
		bool candidate = false;
		PortableTaxonomyArchive.State? taxonomy = null;
		Application.Taxonomy.LegacyMigrationRequest? legacyTaxonomy = null;
		List<PortableTaxonomyArchive.DeletionState>? deletion = null;
		await using (SqliteConnection preflight = new($"Data Source={sqlitePath};Mode=ReadOnly;Pooling=False"))
		{
			await preflight.OpenAsync(cancellationToken);
			int preflightVersion = ReadExportVersion(preflight);
			// Format 15 carries the v14 taxonomy codec unchanged.
			candidate = preflightVersion >= PortableAutomaticHistoricalV14.Version;
			if (requireCandidate && !candidate)
			{ throw new InvalidOperationException("Candidate import requires version 14 or later."); }
			if (preflightVersion >= 5)
			{
				foreach (string table in PortableBackupFormat.RequiredTablesFor(preflightVersion))
				{
					if (!TableExists(preflight, table))
					{
						throw new InvalidOperationException($"Backup export_version={preflightVersion} is missing required '{table}' table.");
					}
				}
			}
			if (preflightVersion >= 10)
			{
				try
				{
					taxonomy = candidate ? await PortableTaxonomyArchive.ReadCandidateAsync(preflight, cancellationToken)
							: await PortableTaxonomyArchive.ReadAsync(preflight, cancellationToken, preflightVersion);
					deletion = await PortableTaxonomyArchive.ReadDeletionStateAsync(preflight, taxonomy, cancellationToken);
				}
				catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException or ArgumentException)
				{ throw new InvalidOperationException("Invalid portable taxonomy archive. " + exception.Message, exception); }
			}
		}

		if (taxonomy == null)
		{
			await using SqliteConnection legacySource = new($"Data Source={sqlitePath};Mode=ReadOnly;Pooling=False");
			await legacySource.OpenAsync(cancellationToken);
			legacyTaxonomy = await CaptureLegacyTaxonomyAsync(legacySource, sqlitePath, ReadExportVersion(legacySource), cancellationToken);
		}

		await using IAsyncDisposable importLease =
			await BackupImportLock.AcquireLocalAsync(cancellationToken);
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		context.RestoringNormalizationEvidence = true;
		await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
			await context.Database.BeginTransactionAsync(cancellationToken);
		await BackupImportLock.AcquireDatabaseAsync(context, cancellationToken);
		if (legacyTaxonomy != null)
		{
			LegacyImportPreparation preparation = await PrepareLegacyTaxonomyAsync(context, legacyTaxonomy, cancellationToken);
			legacyTaxonomy = preparation.Source;
			if (preparation.IsReplay)
			{
				// Exact legacy replays are observations already imported; preserve later edits.
				await transaction.CommitAsync(cancellationToken);
				return new BackupImportResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
			}
		}
		if (taxonomy != null)
		{
			await PortableTaxonomyArchive.LockDestinationAsync(context, cancellationToken);
			try
			{
				if (candidate) { await PortableTaxonomyArchive.PreflightCandidateDestinationAsync(context, taxonomy, cancellationToken); }
				else { await PortableTaxonomyArchive.PreflightDestinationAsync(context, taxonomy, cancellationToken); }
			}
			catch (InvalidDataException exception) { throw new InvalidOperationException(exception.Message, exception); }
			await PortableTaxonomyArchive.AbandonDestinationAutomaticAsync(context, cancellationToken);
			await RemovePristineBootstrapAsync(context, cancellationToken);
		}

		if (deletion != null)
		{
			ImportDeletionStates.Add(context, deletion.ToDictionary(r => (r.Table, r.Id)));
			await StageImportedTombstonesAsync(context, deletion, cancellationToken);
		}

		// Restored business rows cannot be used by a pre-restore inference, even if
		// the imported values happen to be identical and EF emits no row update.
		await Infrastructure.NormalizationAi.NormalizationAiService.FenceForRestoreAsync(context, cancellationToken);
		await CategoryAssistanceRestoreFence.FenceAsync(context, cancellationToken);
		await SaveImportedChangesAsync(context, cancellationToken);

		try
		{
			await using SqliteConnection sqlite = new($"Data Source={sqlitePath};Mode=ReadOnly;Pooling=False");
			await sqlite.OpenAsync(cancellationToken);

			int exportVersion = ReadExportVersion(sqlite);
			await PortableEnvelopeArchive.RequireDerivableDestinationAsync(context, exportVersion, cancellationToken);
			if (exportVersion >= 7 && !TableExists(sqlite, "normalization_catalog"))
			{ throw new InvalidOperationException("Backup export_version=7 is missing required 'normalization_catalog' table."); }
			if (exportVersion >= 5)
			{
				foreach (string table in PortableBackupFormat.RequiredTablesFor(exportVersion))
				{
					if (!TableExists(sqlite, table))
					{
						throw new InvalidOperationException($"Backup export_version={exportVersion} is missing required '{table}' table.");
					}
				}
				await ValidateCurationReferencesAsync(sqlite, cancellationToken);
			}

			try
			{
				await PortableCatalogArchive.ConsumeConsolidationsAsync(context, sqlite, exportVersion, cancellationToken);
			}
			catch (InvalidDataException exception)
			{
				throw new InvalidOperationException(exception.Message, exception);
			}

			// Import in dependency order: independent entities first, then dependent ones.
			// Accounts must come before Cards (FK constraint on Cards.AccountId → Accounts.Id).
			(int accountsCreated, int accountsUpdated) = await UpsertAccountsAsync(context, sqlite, exportVersion, cancellationToken);
			(int cardsCreated, int cardsUpdated) = await UpsertCardsAsync(context, sqlite, exportVersion, cancellationToken);
			(int categoriesCreated, int categoriesUpdated) = await UpsertCategoriesAsync(context, sqlite, cancellationToken);
			(int subcategoriesCreated, int subcategoriesUpdated) = await UpsertSubcategoriesAsync(context, sqlite, cancellationToken);
			(int normalizedDescriptionsCreated, int normalizedDescriptionsUpdated) = await UpsertNormalizedDescriptionsAsync(context, sqlite, exportVersion, cancellationToken);
			(int itemTemplatesCreated, int itemTemplatesUpdated) = await UpsertItemTemplatesAsync(context, sqlite, exportVersion, cancellationToken);
			Dictionary<(Guid First, Guid Second), Guid> priorAcceptedPairs = await LockReceiptImportAsync(context, sqlite, exportVersion, cancellationToken);
			(int receiptsCreated, int receiptsUpdated) = await UpsertReceiptsAsync(context, sqlite, exportVersion, cancellationToken);
			(int receiptItemsCreated, int receiptItemsUpdated) = await UpsertReceiptItemsAsync(context, sqlite, exportVersion, cancellationToken);
			(int transactionsCreated, int transactionsUpdated) = await UpsertTransactionsAsync(context, sqlite, exportVersion, cancellationToken);
			(int adjustmentsCreated, int adjustmentsUpdated) = await UpsertAdjustmentsAsync(context, sqlite, cancellationToken);

			// v4 tables. Each method is gated on export_version >= 4, so older backups (v1-3)
			// skip them entirely and restore with these tables treated as absent. YnabAccountMappings
			// reference Accounts and YnabSyncRecords reference Transactions — both already imported
			// above — so the FK import order is satisfied.
			(int ynabSelectedBudgetsCreated, int ynabSelectedBudgetsUpdated) = await UpsertYnabSelectedBudgetsAsync(context, sqlite, exportVersion, cancellationToken);
			(int ynabAccountMappingsCreated, int ynabAccountMappingsUpdated) = await UpsertYnabAccountMappingsAsync(context, sqlite, exportVersion, cancellationToken);
			(int ynabCategoryMappingsCreated, int ynabCategoryMappingsUpdated) = await UpsertYnabCategoryMappingsAsync(context, sqlite, exportVersion,
					taxonomy?.Nodes.Select(node => node.CategoryId).ToHashSet(), cancellationToken);
			(int ynabSyncRecordsCreated, int ynabSyncRecordsUpdated) = await UpsertYnabSyncRecordsAsync(context, sqlite, exportVersion, cancellationToken);
			(int normalizedDescriptionSettingsCreated, int normalizedDescriptionSettingsUpdated) = await UpsertNormalizedDescriptionSettingsAsync(context, sqlite, exportVersion, cancellationToken);

			(int acceptedPairsCreated, int acceptedPairsUpdated) = await UpsertAcceptedDuplicatePairsAsync(context, sqlite, exportVersion, priorAcceptedPairs, cancellationToken);
			try
			{
				await PortableCatalogArchive.ImportAsync(context, sqlite, exportVersion, cancellationToken);
			}
			catch (InvalidDataException exception)
			{
				throw new InvalidOperationException(exception.Message, exception);
			}

			if (taxonomy != null)
			{
				await PortableTaxonomyArchive.RestoreAsync(context, taxonomy, deletion!, cancellationToken);
			}

			if (legacyTaxonomy != null)
			{
				await LegacyTaxonomyMigrationService.AppendInTransactionAsync(context, legacyTaxonomy, cancellationToken);
			}
			if (taxonomy != null) { await PortableTaxonomyArchive.RestoreSourceRevisionsAsync(context, taxonomy, cancellationToken); }
			// Envelope-only updates keep restored source revisions (see the AddEnvelopes migration).
			await PortableEnvelopeArchive.ImportAsync(context, sqlite, exportVersion, cancellationToken);
			await transaction.CommitAsync(cancellationToken);

			BackupImportResult result = new(
				accountsCreated, accountsUpdated,
				cardsCreated, cardsUpdated,
				categoriesCreated, categoriesUpdated,
				subcategoriesCreated, subcategoriesUpdated,
				itemTemplatesCreated, itemTemplatesUpdated,
				receiptsCreated, receiptsUpdated,
				receiptItemsCreated, receiptItemsUpdated,
				transactionsCreated, transactionsUpdated,
				adjustmentsCreated, adjustmentsUpdated,
				ynabSelectedBudgetsCreated, ynabSelectedBudgetsUpdated,
				ynabAccountMappingsCreated, ynabAccountMappingsUpdated,
				ynabCategoryMappingsCreated, ynabCategoryMappingsUpdated,
				ynabSyncRecordsCreated, ynabSyncRecordsUpdated,
				normalizedDescriptionsCreated, normalizedDescriptionsUpdated,
				normalizedDescriptionSettingsCreated, normalizedDescriptionSettingsUpdated,
				acceptedPairsCreated, acceptedPairsUpdated);

			logger.LogInformation(
				"Backup import complete: {TotalCreated} created, {TotalUpdated} updated",
				result.TotalCreated, result.TotalUpdated);

			return result;
		}
		catch (DbUpdateException ex) when (ex.InnerException is PostgresException
		{ SqlState: PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NotNullViolation or PostgresErrorCodes.StringDataRightTruncation })
		{
			await transaction.RollbackAsync(CancellationToken.None);
			throw new InvalidOperationException("Backup data conflicts with database constraints. The import was rolled back.", ex);
		}
		catch
		{
			await transaction.RollbackAsync(CancellationToken.None);
			throw;
		}
	}

	private static void ValidateSqliteFile(string path)
	{
		// SQLite files start with the magic string "SQLite format 3\000"
		byte[] header = new byte[16];
		using FileStream fs = File.OpenRead(path);
		int bytesRead = fs.Read(header, 0, header.Length);
		if (bytesRead < 16)
		{
			throw new InvalidOperationException("The uploaded file is not a valid SQLite database.");
		}

		string magic = System.Text.Encoding.ASCII.GetString(header, 0, 16);
		if (magic != "SQLite format 3\0")
		{
			throw new InvalidOperationException("The uploaded file is not a valid SQLite database.");
		}
	}

	/// <summary>
	/// Clears soft-delete markers so that a restored record becomes visible again.
	/// </summary>
	private static void ClearSoftDelete(ISoftDeletable entity)
	{
		entity.DeletedAt = null;
		entity.DeletedByUserId = null;
		entity.DeletedByApiKeyId = null;
		entity.CascadeDeletedByParentId = null;
	}

	internal static bool TableExists(SqliteConnection sqlite, string tableName)
	{
		using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
		cmd.Parameters.AddWithValue("@name", tableName);
		return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
	}

	// Reads the export_version from backup_metadata; defaults to 1 for legacy backups
	// that predate version bumps or were written without metadata.
	private static int ReadExportVersion(SqliteConnection sqlite)
	{
		if (!TableExists(sqlite, "backup_metadata"))
		{
			return 1;
		}

		using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = "SELECT value FROM backup_metadata WHERE key = 'export_version'";
		object? result = cmd.ExecuteScalar();
		if (result is null || result is DBNull)
		{
			return 1;
		}

		if (!int.TryParse(result.ToString(), out int version) || version < 1 || version > PortableBackupFormat.LatestImportVersion)
		{
			throw new InvalidOperationException($"Unsupported backup export_version '{result}'. Supported versions are 1 through {PortableBackupFormat.LatestImportVersion}.");
		}
		return version;
	}

	// Import Accounts before Cards so the FK Cards.AccountId → Accounts.Id resolves.
	// v3+ backups include a dedicated `accounts` table. Legacy (<v3) backups have
	// no such table — we infer one Account per Card using 1:1 mapping, matching the
	// IntroduceAccountAggregate migration that introduced the aggregate in prod.
	private static async Task<(int Created, int Updated)> UpsertAccountsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion, CancellationToken cancellationToken)
	{
		if (exportVersion >= 3)
		{
			if (!TableExists(sqlite, "accounts"))
			{
				throw new InvalidOperationException($"Backup export_version={exportVersion} is missing required 'accounts' table.");
			}

			int created = 0, updated = 0;
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = "SELECT id, name, is_active FROM accounts";
			await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

			while (await reader.ReadAsync(cancellationToken))
			{
				Guid id = Guid.Parse(reader.GetString(0));
				string name = reader.GetString(1);
				bool isActive = reader.GetBoolean(2);

				AccountEntity? existing = await context.Accounts.FindAsync([id], cancellationToken);
				if (existing is not null)
				{
					existing.Name = name;
					existing.IsActive = isActive;
					updated++;
				}
				else
				{
					context.Accounts.Add(new AccountEntity
					{
						Id = id,
						Name = name,
						IsActive = isActive,
					});
					created++;
				}
			}

			await SaveImportedChangesAsync(context, cancellationToken);
			return (created, updated);
		}

		// Legacy fallback: derive one Account per Card (same Id + name), matching
		// the 1:1 mapping from the IntroduceAccountAggregate migration.
		bool isLegacy = exportVersion < 2;
		string cardsTableName = isLegacy ? "accounts" : "cards";
		string codeColumn = isLegacy ? "account_code" : "card_code";

		if (!TableExists(sqlite, cardsTableName))
		{
			return (0, 0);
		}

		int legacyCreated = 0, legacyUpdated = 0;
		await using SqliteCommand legacyCmd = sqlite.CreateCommand();
		legacyCmd.CommandText = $"SELECT id, {codeColumn}, name, is_active FROM {cardsTableName}";
		await using SqliteDataReader legacyReader = await legacyCmd.ExecuteReaderAsync(cancellationToken);

		while (await legacyReader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(legacyReader.GetString(0));
			string name = legacyReader.GetString(2);
			bool isActive = legacyReader.GetBoolean(3);

			AccountEntity? existing = await context.Accounts.FindAsync([id], cancellationToken);
			if (existing is not null)
			{
				existing.Name = name;
				existing.IsActive = isActive;
				legacyUpdated++;
			}
			else
			{
				context.Accounts.Add(new AccountEntity
				{
					Id = id,
					Name = name,
					IsActive = isActive,
				});
				legacyCreated++;
			}
		}

		await SaveImportedChangesAsync(context, cancellationToken);
		return (legacyCreated, legacyUpdated);
	}

	private static async Task<(int Created, int Updated)> UpsertCardsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion, CancellationToken cancellationToken)
	{
		// v2+ writes to `cards`/`card_code`; v1 wrote to `accounts`/`account_code`.
		// v3+ adds `account_id`. For legacy (<3) backups we infer AccountId = Card.Id,
		// matching the 1:1 Account-per-Card seeding from the IntroduceAccountAggregate
		// migration. UpsertAccountsAsync has already ensured the parent exists.
		bool isLegacy = exportVersion < 2;
		bool hasAccountIdColumn = exportVersion >= 3;
		string tableName = isLegacy ? "accounts" : "cards";
		string codeColumn = isLegacy ? "account_code" : "card_code";

		if (!TableExists(sqlite, tableName))
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		string selectColumns = hasAccountIdColumn
			? $"id, {codeColumn}, name, is_active, account_id"
			: $"id, {codeColumn}, name, is_active";
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = $"SELECT {selectColumns} FROM {tableName}";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			string cardCode = reader.GetString(1);
			string name = reader.GetString(2);
			bool isActive = reader.GetBoolean(3);

			Guid accountId;
			if (hasAccountIdColumn)
			{
				if (reader.IsDBNull(4))
				{
					throw new InvalidOperationException($"Backup card {id} is missing account_id (export_version={exportVersion} requires it).");
				}
				accountId = Guid.Parse(reader.GetString(4));
			}
			else
			{
				// Legacy fallback: AccountId = Card.Id (1:1 with Account).
				accountId = id;
			}

			CardEntity? existing = await context.Cards.FindAsync([id], cancellationToken);
			if (existing is not null)
			{
				existing.CardCode = cardCode;
				existing.Name = name;
				existing.IsActive = isActive;
				existing.AccountId = accountId;
				updated++;
			}
			else
			{
				context.Cards.Add(new CardEntity
				{
					Id = id,
					CardCode = cardCode,
					Name = name,
					IsActive = isActive,
					AccountId = accountId,
				});
				created++;
			}
		}

		await SaveImportedChangesAsync(context, cancellationToken);
		return (created, updated);
	}

	private static async Task<(int Created, int Updated)> UpsertCategoriesAsync(
		ApplicationDbContext context, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		if (!TableExists(sqlite, "categories"))
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = "SELECT id, name, description, is_active FROM categories";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			string name = reader.GetString(1);
			string? description = reader.IsDBNull(2) ? null : reader.GetString(2);
			bool isActive = reader.GetBoolean(3);

			CategoryEntity? existing = await context.Categories
				.IgnoreQueryFilters()
				.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
			if (existing is not null)
			{
				existing.Name = name;
				existing.Description = description;
				existing.IsActive = isActive;
				ClearSoftDelete(existing);
				updated++;
			}
			else
			{
				context.Categories.Add(new CategoryEntity
				{
					Id = id,
					Name = name,
					Description = description,
					IsActive = isActive,
				});
				created++;
			}
		}

		await SaveImportedChangesAsync(context, cancellationToken);
		return (created, updated);
	}

	private static async Task<(int Created, int Updated)> UpsertSubcategoriesAsync(
		ApplicationDbContext context, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		if (!TableExists(sqlite, "subcategories"))
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = "SELECT id, name, category_id, description, is_active FROM subcategories";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			string name = reader.GetString(1);
			Guid categoryId = Guid.Parse(reader.GetString(2));
			string? description = reader.IsDBNull(3) ? null : reader.GetString(3);
			bool isActive = reader.GetBoolean(4);

			SubcategoryEntity? existing = await context.Subcategories
				.IgnoreQueryFilters()
				.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
			if (existing is not null)
			{
				existing.Name = name;
				existing.CategoryId = categoryId;
				existing.Description = description;
				existing.IsActive = isActive;
				ClearSoftDelete(existing);
				updated++;
			}
			else
			{
				context.Subcategories.Add(new SubcategoryEntity
				{
					Id = id,
					Name = name,
					CategoryId = categoryId,
					Description = description,
					IsActive = isActive,
				});
				created++;
			}
		}

		await SaveImportedChangesAsync(context, cancellationToken);
		return (created, updated);
	}

	private static async Task<(int Created, int Updated)> UpsertItemTemplatesAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion, CancellationToken cancellationToken)
	{
		if (!TableExists(sqlite, "item_templates"))
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		bool hasCuration = exportVersion >= 5;
		cmd.CommandText = "SELECT id, name, default_category, default_subcategory, default_unit_price, default_unit_price_currency, default_item_code, description"
			+ (hasCuration ? ", normalized_description_id" : "") + " FROM item_templates";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			string name = reader.GetString(1);
			string? defaultCategory = reader.IsDBNull(2) ? null : reader.GetString(2);
			string? defaultSubcategory = reader.IsDBNull(3) ? null : reader.GetString(3);
			decimal? defaultUnitPrice = reader.IsDBNull(4) ? null : reader.GetDecimal(4);
			// Legacy backups may contain defaults that fit the former numeric(18,2) column.
			// PostgreSQL rounds to scale before checking range; validate without changing the value.
			if (defaultUnitPrice.HasValue
				&& Math.Abs(decimal.Round(defaultUnitPrice.Value, 4, MidpointRounding.AwayFromZero)) >= 100_000_000_000_000m)
			{
				throw new InvalidOperationException($"Backup item template {id} has a default unit price outside the supported range. The import was rolled back.");
			}
			Currency? defaultUnitPriceCurrency = reader.IsDBNull(5) ? null : ParseCurrency(reader.GetString(5));
			string? defaultItemCode = reader.IsDBNull(6) ? null : reader.GetString(6);
			string? description = reader.IsDBNull(7) ? null : reader.GetString(7);
			Guid? canonicalId = hasCuration ? ReadNullableGuid(reader, 8) : null;

			ItemTemplateEntity? existing = await context.ItemTemplates
				.IgnoreQueryFilters()
				.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
			if (existing is not null)
			{
				existing.Name = name;
				existing.DefaultCategory = defaultCategory;
				existing.DefaultSubcategory = defaultSubcategory;
				existing.DefaultUnitPrice = defaultUnitPrice;
				existing.DefaultUnitPriceCurrency = defaultUnitPriceCurrency;
				existing.DefaultItemCode = defaultItemCode;
				existing.Description = description;
				if (hasCuration)
				{
					existing.NormalizedDescriptionId = canonicalId;
				}
				ClearSoftDelete(existing);
				updated++;
			}
			else
			{
				context.ItemTemplates.Add(new ItemTemplateEntity
				{
					Id = id,
					Name = name,
					DefaultCategory = defaultCategory,
					DefaultSubcategory = defaultSubcategory,
					DefaultUnitPrice = defaultUnitPrice,
					DefaultUnitPriceCurrency = defaultUnitPriceCurrency,
					DefaultItemCode = defaultItemCode,
					Description = description,
					NormalizedDescriptionId = canonicalId,
				});
				created++;
			}
		}

		await SaveImportedChangesAsync(context, cancellationToken);
		return (created, updated);
	}

	private static async Task<(int Created, int Updated)> UpsertReceiptsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion, CancellationToken cancellationToken)
	{
		if (!TableExists(sqlite, "receipts"))
		{
			return (0, 0);
		}

		// v4 adds the image-path columns. Older backups (<4) never carried them, so we neither
		// read them nor touch a receipt's existing paths on update — preserving pre-v4 behaviour.
		bool hasImagePaths = exportVersion >= 4;
		string selectColumns = hasImagePaths
			? "id, location, date, tax_amount, tax_amount_currency, original_image_path, processed_image_path"
			: "id, location, date, tax_amount, tax_amount_currency";

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = $"SELECT {selectColumns} FROM receipts";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			string location = reader.GetString(1);
			DateOnly date = DateOnly.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
			decimal taxAmount = reader.GetDecimal(3);
			Currency taxAmountCurrency = ParseCurrency(reader.GetString(4));
			string? originalImagePath = hasImagePaths && !reader.IsDBNull(5) ? reader.GetString(5) : null;
			string? processedImagePath = hasImagePaths && !reader.IsDBNull(6) ? reader.GetString(6) : null;

			ReceiptEntity? existing = await context.Receipts
				.IgnoreQueryFilters()
				.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
			if (existing is not null)
			{
				existing.Location = location;
				existing.Date = date;
				existing.TaxAmount = taxAmount;
				existing.TaxAmountCurrency = taxAmountCurrency;
				// Only overwrite image paths when the backup actually carried them (v4+); a v1-3
				// restore must not null out paths already present on the existing receipt.
				if (hasImagePaths)
				{
					existing.OriginalImagePath = originalImagePath;
					existing.ProcessedImagePath = processedImagePath;
				}
				ClearSoftDelete(existing);
				updated++;
			}
			else
			{
				context.Receipts.Add(new ReceiptEntity
				{
					Id = id,
					Location = location,
					Date = date,
					TaxAmount = taxAmount,
					TaxAmountCurrency = taxAmountCurrency,
					OriginalImagePath = originalImagePath,
					ProcessedImagePath = processedImagePath,
				});
				created++;
			}
		}

		await SaveImportedChangesAsync(context, cancellationToken);
		return (created, updated);
	}

	private static async Task<(int Created, int Updated)> UpsertReceiptItemsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion, CancellationToken cancellationToken)
	{
		if (!TableExists(sqlite, "receipt_items"))
		{
			return (0, 0);
		}

		Dictionary<Guid, Guid> archiveAssignments = await PortableCatalogArchive.LoadLegacyAssignmentIdsAsync(sqlite, exportVersion, cancellationToken);
		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		bool hasCuration = exportVersion >= 5;
		cmd.CommandText = "SELECT id, receipt_id, receipt_item_code, description, quantity, unit_price, unit_price_currency, total_amount, total_amount_currency, category, subcategory"
			+ (hasCuration ? ", normalized_description_id, normalized_description_match_score" : "") + " FROM receipt_items";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			Guid receiptId = Guid.Parse(reader.GetString(1));
			string? receiptItemCode = reader.IsDBNull(2) ? null : reader.GetString(2);
			string description = reader.GetString(3);
			decimal quantity = reader.GetDecimal(4);
			decimal unitPrice = reader.GetDecimal(5);
			Currency unitPriceCurrency = ParseCurrency(reader.GetString(6));
			decimal totalAmount = reader.GetDecimal(7);
			Currency totalAmountCurrency = ParseCurrency(reader.GetString(8));
			string category = reader.GetString(9);
			string? subcategory = reader.IsDBNull(10) ? null : reader.GetString(10);
			Guid? canonicalId = hasCuration ? ReadNullableGuid(reader, 11) : null;
			double? matchScore = hasCuration ? ReadNullableScore(reader, 12) : null;
			if (canonicalId is null && matchScore is not null)
			{
				throw new InvalidOperationException($"Receipt item {id} has a match score without a canonical description.");
			}

			ReceiptItemEntity? existing = await context.ReceiptItems
				.IgnoreQueryFilters()
				.FirstOrDefaultAsync(ri => ri.Id == id, cancellationToken);
			if (existing is not null)
			{
				// Older formats cannot restore a link for new text. Preserve absent metadata only
				// while the raw description still matches the target's existing classification.
				bool preserveAssignment = archiveAssignments.TryGetValue(id, out Guid archiveAssignmentId)
					&& existing.NormalizationAssignmentId == archiveAssignmentId
					&& existing.NormalizationOrigin == "legacy_unknown" && existing.NormalizationMappingId is null
					&& existing.NormalizedDescriptionId == canonicalId && existing.NormalizedDescriptionMatchScore == matchScore
					&& existing.ReceiptId == receiptId && existing.Description == description && existing.ReceiptItemCode == receiptItemCode;
				if (hasCuration || !string.Equals(existing.Description, description, StringComparison.Ordinal))
				{
					existing.NormalizedDescriptionId = canonicalId;
					existing.NormalizedDescriptionMatchScore = matchScore;
				}
				existing.ReceiptId = receiptId;
				existing.NormalizationOrigin = existing.NormalizedDescriptionId.HasValue ? "legacy_unknown" : "unassigned";
				existing.NormalizationMappingId = null;
				if (!preserveAssignment) { existing.NormalizationAssignmentId = Guid.NewGuid(); }
				existing.ReceiptItemCode = receiptItemCode;
				existing.Description = description;
				existing.Quantity = quantity;
				existing.UnitPrice = unitPrice;
				existing.UnitPriceCurrency = unitPriceCurrency;
				existing.TotalAmount = totalAmount;
				existing.TotalAmountCurrency = totalAmountCurrency;
				existing.Category = category;
				existing.Subcategory = subcategory;
				ClearSoftDelete(existing);
				updated++;
			}
			else
			{
				context.ReceiptItems.Add(new ReceiptItemEntity
				{
					Id = id,
					ReceiptId = receiptId,
					ReceiptItemCode = receiptItemCode,
					Description = description,
					Quantity = quantity,
					UnitPrice = unitPrice,
					UnitPriceCurrency = unitPriceCurrency,
					TotalAmount = totalAmount,
					TotalAmountCurrency = totalAmountCurrency,
					Category = category,
					Subcategory = subcategory,
					NormalizedDescriptionId = canonicalId,
					NormalizedDescriptionMatchScore = matchScore,
					NormalizationOrigin = canonicalId.HasValue ? "legacy_unknown" : "unassigned",
					NormalizationAssignmentId = Guid.NewGuid(),
				});
				created++;
			}
		}

		await SaveImportedChangesAsync(context, cancellationToken);
		return (created, updated);
	}

	private static async Task<(int Created, int Updated)> UpsertTransactionsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion, CancellationToken cancellationToken)
	{
		if (!TableExists(sqlite, "transactions"))
		{
			return (0, 0);
		}

		// v2+ exports carry `card_id`; v1 only had `account_id` (which was effectively the
		// card id — Account.Id == Card.Id in the pre-aggregate schema). In both cases the
		// column value is the originating Card's Id.
		string cardColumn = exportVersion < 2 ? "account_id" : "card_id";

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = $"SELECT id, receipt_id, {cardColumn}, amount, amount_currency, date FROM transactions";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			Guid receiptId = Guid.Parse(reader.GetString(1));
			Guid cardId = Guid.Parse(reader.GetString(2));
			decimal amount = reader.GetDecimal(3);
			Currency amountCurrency = ParseCurrency(reader.GetString(4));
			DateOnly date = DateOnly.Parse(reader.GetString(5), CultureInfo.InvariantCulture);

			TransactionEntity? existing = await context.Transactions
				.IgnoreQueryFilters()
				.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
			if (existing is not null)
			{
				existing.ReceiptId = receiptId;
				existing.CardId = cardId;
				existing.Amount = amount;
				existing.AmountCurrency = amountCurrency;
				existing.Date = date;
				ClearSoftDelete(existing);
				updated++;
			}
			else
			{
				context.Transactions.Add(new TransactionEntity
				{
					Id = id,
					ReceiptId = receiptId,
					CardId = cardId,
					Amount = amount,
					AmountCurrency = amountCurrency,
					Date = date,
				});
				created++;
			}
		}

		await SaveImportedChangesAsync(context, cancellationToken);
		return (created, updated);
	}

	private static async Task<(int Created, int Updated)> UpsertAdjustmentsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		if (!TableExists(sqlite, "adjustments"))
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = "SELECT id, receipt_id, type, amount, amount_currency, description FROM adjustments";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			Guid receiptId = Guid.Parse(reader.GetString(1));
			AdjustmentType type = Enum.Parse<AdjustmentType>(reader.GetString(2));
			decimal amount = reader.GetDecimal(3);
			Currency amountCurrency = ParseCurrency(reader.GetString(4));
			string? description = reader.IsDBNull(5) ? null : reader.GetString(5);

			AdjustmentEntity? existing = await context.Adjustments
				.IgnoreQueryFilters()
				.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
			if (existing is not null)
			{
				existing.ReceiptId = receiptId;
				existing.Type = type;
				existing.Amount = amount;
				existing.AmountCurrency = amountCurrency;
				existing.Description = description;
				ClearSoftDelete(existing);
				updated++;
			}
			else
			{
				context.Adjustments.Add(new AdjustmentEntity
				{
					Id = id,
					ReceiptId = receiptId,
					Type = type,
					Amount = amount,
					AmountCurrency = amountCurrency,
					Description = description,
				});
				created++;
			}
		}

		await SaveImportedChangesAsync(context, cancellationToken);
		return (created, updated);
	}

	// Parses an ISO-8601 round-trip timestamp written by the exporter, culture-independently.
	private static DateTimeOffset ParseTimestamp(string value) =>
		DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

	// v4 tables. Each returns (0, 0) when export_version < 4 or when the table is absent, so a
	// v1-3 backup restores with these treated as empty and never throws.
	private static async Task<(int Created, int Updated)> UpsertYnabSelectedBudgetsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion, CancellationToken cancellationToken)
	{
		if (exportVersion < 4 || !TableExists(sqlite, "ynab_selected_budgets"))
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = "SELECT id, budget_id, updated_at FROM ynab_selected_budgets";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			string budgetId = YnabDestinationId.Canonicalize(reader.GetString(1));
			DateTimeOffset updatedAt = ParseTimestamp(reader.GetString(2));

			YnabSelectedBudgetEntity? existing = await context.YnabSelectedBudgets.FindAsync([id], cancellationToken);
			if (existing is not null)
			{
				existing.BudgetId = budgetId;
				existing.UpdatedAt = updatedAt;
				updated++;
			}
			else
			{
				context.YnabSelectedBudgets.Add(new YnabSelectedBudgetEntity
				{
					Id = id,
					BudgetId = budgetId,
					UpdatedAt = updatedAt,
				});
				created++;
			}
		}

		await SaveImportedChangesAsync(context, cancellationToken);
		return (created, updated);
	}

	// FK: ReceiptsAccountId → Accounts.Id. Accounts are upserted before this method runs.
	private static async Task<(int Created, int Updated)> UpsertYnabAccountMappingsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion, CancellationToken cancellationToken)
	{
		if (exportVersion < 4 || !TableExists(sqlite, "ynab_account_mappings"))
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = "SELECT id, receipts_account_id, ynab_account_id, ynab_account_name, ynab_budget_id, created_at, updated_at FROM ynab_account_mappings";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			Guid receiptsAccountId = Guid.Parse(reader.GetString(1));
			string ynabAccountId = reader.GetString(2);
			string ynabAccountName = reader.GetString(3);
			string ynabBudgetId = YnabDestinationId.Canonicalize(reader.GetString(4));
			DateTimeOffset createdAt = ParseTimestamp(reader.GetString(5));
			DateTimeOffset updatedAt = ParseTimestamp(reader.GetString(6));

			YnabAccountMappingEntity? existing = await context.YnabAccountMappings.FindAsync([id], cancellationToken);
			if (existing is not null)
			{
				existing.ReceiptsAccountId = receiptsAccountId;
				existing.YnabAccountId = ynabAccountId;
				existing.YnabAccountName = ynabAccountName;
				existing.YnabBudgetId = ynabBudgetId;
				existing.CreatedAt = createdAt;
				existing.UpdatedAt = updatedAt;
				updated++;
			}
			else
			{
				context.YnabAccountMappings.Add(new YnabAccountMappingEntity
				{
					Id = id,
					ReceiptsAccountId = receiptsAccountId,
					YnabAccountId = ynabAccountId,
					YnabAccountName = ynabAccountName,
					YnabBudgetId = ynabBudgetId,
					CreatedAt = createdAt,
					UpdatedAt = updatedAt,
				});
				created++;
			}
		}

		await SaveImportedChangesAsync(context, cancellationToken);
		return (created, updated);
	}

	private static async Task<(int Created, int Updated)> UpsertYnabCategoryMappingsAsync(
				ApplicationDbContext context, SqliteConnection sqlite, int exportVersion,
				HashSet<Guid>? taxonomyIdentityIds, CancellationToken cancellationToken)
	{
		if (exportVersion < 4 || !TableExists(sqlite, "ynab_category_mappings"))
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = exportVersion >= 13
				? "SELECT id, receipts_category, ynab_category_id, ynab_category_name, ynab_category_group_name, ynab_budget_id, created_at, updated_at, receipts_category_id, legacy_resolution, legacy_candidate_category_ids FROM ynab_category_mappings"
				: "SELECT id, receipts_category, ynab_category_id, ynab_category_name, ynab_category_group_name, ynab_budget_id, created_at, updated_at FROM ynab_category_mappings";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			string receiptsCategory = reader.GetString(1);
			string ynabCategoryId = reader.GetString(2);
			string ynabCategoryName = reader.GetString(3);
			string ynabCategoryGroupName = reader.GetString(4);
			string ynabBudgetId = YnabDestinationId.Canonicalize(reader.GetString(5));
			DateTimeOffset createdAt = ParseTimestamp(reader.GetString(6));
			DateTimeOffset updatedAt = ParseTimestamp(reader.GetString(7));
			Guid? receiptsCategoryId = null;
			string legacyResolution = "Unmatched";
			string legacyCandidateCategoryIds = "[]";
			if (exportVersion >= 13)
			{
				if (!reader.IsDBNull(8))
				{
					if (!Guid.TryParse(reader.GetString(8), out Guid parsedId) || parsedId == Guid.Empty)
					{
						throw new InvalidOperationException($"Invalid stable category ID in YNAB mapping {id}.");
					}

					receiptsCategoryId = parsedId;
				}
				legacyResolution = reader.GetString(9);
				legacyCandidateCategoryIds = reader.GetString(10);
				ValidateYnabCategoryEvidence(id, receiptsCategoryId, legacyResolution, legacyCandidateCategoryIds);
				Guid[] candidates = JsonSerializer.Deserialize<Guid[]>(legacyCandidateCategoryIds)!;
				if (taxonomyIdentityIds is null ||
					receiptsCategoryId is Guid stableId && !taxonomyIdentityIds.Contains(stableId) ||
					candidates.Any(candidate => !taxonomyIdentityIds.Contains(candidate)))
				{
					throw new InvalidOperationException($"Unknown taxonomy identity in YNAB mapping {id}.");
				}
			}

			YnabCategoryMappingEntity? existing = await context.YnabCategoryMappings.FindAsync([id], cancellationToken);
			if (existing is not null)
			{
				// A mapping's local category and budget identify its scope. Restore may
				// fill a legacy unbound mapping, but cannot silently rebind an existing one.
				if (YnabDestinationId.Canonicalize(existing.YnabBudgetId) != ynabBudgetId ||
					exportVersion >= 13 && existing.ReceiptsCategoryId is Guid boundCategory && receiptsCategoryId != boundCategory)
				{
					throw new InvalidOperationException($"Conflicting stable category or budget scope in YNAB mapping {id}.");
				}

				existing.ReceiptsCategory = receiptsCategory;
				if (exportVersion >= 13)
				{
					existing.ReceiptsCategoryId = receiptsCategoryId;
					existing.LegacyResolution = legacyResolution;
					existing.LegacyCandidateCategoryIds = legacyCandidateCategoryIds;
				}
				existing.YnabCategoryId = ynabCategoryId;
				existing.YnabCategoryName = ynabCategoryName;
				existing.YnabCategoryGroupName = ynabCategoryGroupName;
				existing.YnabBudgetId = ynabBudgetId;
				existing.CreatedAt = createdAt;
				existing.UpdatedAt = updatedAt;
				updated++;
			}
			else
			{
				context.YnabCategoryMappings.Add(new YnabCategoryMappingEntity
				{
					Id = id,
					ReceiptsCategory = receiptsCategory,
					ReceiptsCategoryId = receiptsCategoryId,
					LegacyResolution = legacyResolution,
					LegacyCandidateCategoryIds = legacyCandidateCategoryIds,
					YnabCategoryId = ynabCategoryId,
					YnabCategoryName = ynabCategoryName,
					YnabCategoryGroupName = ynabCategoryGroupName,
					YnabBudgetId = ynabBudgetId,
					CreatedAt = createdAt,
					UpdatedAt = updatedAt,
				});
				created++;
			}
		}

		await SaveImportedChangesAsync(context, cancellationToken);
		return (created, updated);
	}

	private static void ValidateYnabCategoryEvidence(Guid mappingId, Guid? categoryId,
			string resolution, string candidateJson)
	{
		Guid[]? candidates;
		try { candidates = JsonSerializer.Deserialize<Guid[]>(candidateJson); }
		catch (JsonException exception)
		{ throw new InvalidOperationException($"Invalid legacy candidate IDs in YNAB mapping {mappingId}.", exception); }
		if (candidates is null || candidates.Any(id => id == Guid.Empty) ||
			candidates.Distinct().Count() != candidates.Length)
		{
			throw new InvalidOperationException($"Invalid legacy candidate IDs in YNAB mapping {mappingId}.");
		}

		bool valid = resolution switch
		{
			"Unmatched" => candidates.Length == 0 && categoryId is null,
			"Ambiguous" => candidates.Length > 1 && categoryId is null,
			"Unique" => candidates.Length == 1 && categoryId == candidates[0],
			"Explicit" => categoryId is not null,
			_ => false,
		};
		if (!valid)
		{
			throw new InvalidOperationException($"Inconsistent legacy resolution in YNAB mapping {mappingId}.");
		}
	}

	// FK: LocalTransactionId → Transactions.Id. Transactions are upserted before this method
	// runs. YnabSyncRecords are soft-deletable, so lookups ignore the query filter and updates
	// clear the soft-delete markers, mirroring the other soft-deletable upserts.
	private static async Task<(int Created, int Updated)> UpsertYnabSyncRecordsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion, CancellationToken cancellationToken)
	{
		if (exportVersion < 4 || !TableExists(sqlite, "ynab_sync_records"))
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = exportVersion >= 6
			? "SELECT id, local_transaction_id, ynab_transaction_id, ynab_budget_id, ynab_account_id, import_id, request_payload_json, payload_hash, source_version, attempt_count, claim_token, claimed_at_utc, last_attempt_at_utc, sync_type, sync_status, synced_at_utc, last_error, created_at, updated_at FROM ynab_sync_records"
			: "SELECT id, local_transaction_id, ynab_transaction_id, ynab_budget_id, ynab_account_id, sync_type, sync_status, synced_at_utc, last_error, created_at, updated_at FROM ynab_sync_records";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			Guid localTransactionId = Guid.Parse(reader.GetString(1));
			string? ynabTransactionId = reader.IsDBNull(2) ? null : reader.GetString(2);
			string ynabBudgetId = YnabDestinationId.Canonicalize(reader.GetString(3));
			string? ynabAccountId = reader.IsDBNull(4) ? null : reader.GetString(4);
			string? importId = exportVersion >= 6 && !reader.IsDBNull(5) ? reader.GetString(5) : null;
			string? requestPayloadJson = exportVersion >= 6 && !reader.IsDBNull(6) ? reader.GetString(6) : null;
			string? payloadHash = exportVersion >= 6 && !reader.IsDBNull(7) ? reader.GetString(7) : null;
			string? sourceVersion = exportVersion >= 6 && !reader.IsDBNull(8) ? reader.GetString(8) : null;
			int attemptCount = exportVersion >= 6 ? reader.GetInt32(9) : 0;
			Guid? claimToken = exportVersion >= 6 && !reader.IsDBNull(10) ? Guid.Parse(reader.GetString(10)) : null;
			DateTimeOffset? claimedAtUtc = exportVersion >= 6 && !reader.IsDBNull(11) ? ParseTimestamp(reader.GetString(11)) : null;
			DateTimeOffset? lastAttemptAtUtc = exportVersion >= 6 && !reader.IsDBNull(12) ? ParseTimestamp(reader.GetString(12)) : null;
			int offset = exportVersion >= 6 ? 8 : 0;
			YnabSyncType syncType = Enum.Parse<YnabSyncType>(reader.GetString(5 + offset));
			YnabSyncStatus syncStatus = Enum.Parse<YnabSyncStatus>(reader.GetString(6 + offset));
			DateTimeOffset? syncedAtUtc = reader.IsDBNull(7 + offset) ? null : ParseTimestamp(reader.GetString(7 + offset));
			string? lastError = reader.IsDBNull(8 + offset) ? null : reader.GetString(8 + offset);
			DateTimeOffset createdAt = ParseTimestamp(reader.GetString(9 + offset));
			DateTimeOffset updatedAt = ParseTimestamp(reader.GetString(10 + offset));
			if (exportVersion < 6 && syncType == YnabSyncType.TransactionPush && syncStatus != YnabSyncStatus.Synced)
			{
				syncStatus = YnabSyncStatus.Unknown;
				lastError = "This imported YNAB attempt has no immutable operation snapshot. Review the destination transaction before resolving it.";
			}

			YnabSyncRecordEntity? existing = await context.YnabSyncRecords
				.IgnoreQueryFilters()
				.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
			if (existing is not null)
			{
				existing.LocalTransactionId = localTransactionId;
				existing.YnabTransactionId = ynabTransactionId;
				existing.YnabBudgetId = ynabBudgetId;
				existing.YnabAccountId = ynabAccountId;
				existing.ImportId = importId;
				existing.RequestPayloadJson = requestPayloadJson;
				existing.PayloadHash = payloadHash;
				existing.SourceVersion = sourceVersion;
				existing.AttemptCount = attemptCount;
				existing.ClaimToken = claimToken;
				existing.ClaimedAtUtc = claimedAtUtc;
				existing.LastAttemptAtUtc = lastAttemptAtUtc;
				existing.SyncType = syncType;
				existing.SyncStatus = syncStatus;
				existing.SyncedAtUtc = syncedAtUtc;
				existing.LastError = lastError;
				existing.CreatedAt = createdAt;
				existing.UpdatedAt = updatedAt;
				ClearSoftDelete(existing);
				updated++;
			}
			else
			{
				context.YnabSyncRecords.Add(new YnabSyncRecordEntity
				{
					Id = id,
					LocalTransactionId = localTransactionId,
					YnabTransactionId = ynabTransactionId,
					YnabBudgetId = ynabBudgetId,
					YnabAccountId = ynabAccountId,
					ImportId = importId,
					RequestPayloadJson = requestPayloadJson,
					PayloadHash = payloadHash,
					SourceVersion = sourceVersion,
					AttemptCount = attemptCount,
					ClaimToken = claimToken,
					ClaimedAtUtc = claimedAtUtc,
					LastAttemptAtUtc = lastAttemptAtUtc,
					SyncType = syncType,
					SyncStatus = syncStatus,
					SyncedAtUtc = syncedAtUtc,
					LastError = lastError,
					CreatedAt = createdAt,
					UpdatedAt = updatedAt,
				});
				created++;
			}
		}

		await SaveImportedChangesAsync(context, cancellationToken);
		return (created, updated);
	}

	// Singleton settings row (thresholds). No FK, so order is flexible. Thresholds are parsed
	// with InvariantCulture to match the culture-safe export.
	private static async Task<(int Created, int Updated)> UpsertNormalizedDescriptionSettingsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion, CancellationToken cancellationToken)
	{
		if (exportVersion < 4 || !TableExists(sqlite, "normalized_description_settings"))
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = "SELECT id, auto_accept_threshold, pending_review_threshold, updated_at FROM normalized_description_settings";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			double autoAcceptThreshold = double.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
			double pendingReviewThreshold = double.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
			DateTimeOffset updatedAt = ParseTimestamp(reader.GetString(3));

			NormalizedDescriptionSettingsEntity? existing = await context.NormalizedDescriptionSettings.FindAsync([id], cancellationToken);
			if (existing is not null)
			{
				existing.AutoAcceptThreshold = autoAcceptThreshold;
				existing.PendingReviewThreshold = pendingReviewThreshold;
				existing.UpdatedAt = updatedAt;
				updated++;
			}
			else
			{
				context.NormalizedDescriptionSettings.Add(new NormalizedDescriptionSettingsEntity
				{
					Id = id,
					AutoAcceptThreshold = autoAcceptThreshold,
					PendingReviewThreshold = pendingReviewThreshold,
					UpdatedAt = updatedAt,
				});
				created++;
			}
		}

		await SaveImportedChangesAsync(context, cancellationToken);
		return (created, updated);
	}
	private static Currency ParseCurrency(string value)
	{
		if (Enum.TryParse(value, out Currency currency)
			&& Enum.IsDefined(currency)
			&& string.Equals(Enum.GetName(currency), value, StringComparison.Ordinal))
		{
			return currency;
		}

		throw new InvalidOperationException("Backup contains an unsupported currency value. The import was rolled back.");
	}
}
