using System.Globalization;
using Application.Interfaces.Services;
using Application.Models;
using Common;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public class BackupImportService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	ILogger<BackupImportService> logger) : IBackupImportService
{
	private const long MaxFileSizeBytes = 100 * 1024 * 1024; // 100 MB

	public async Task<BackupImportResult> ImportFromSqliteAsync(Stream sqliteStream, CancellationToken cancellationToken)
	{
		string tempPath = Path.GetTempFileName();
		try
		{
			await using (FileStream fs = File.Create(tempPath))
			{
				long totalCopied = 0;
				byte[] buffer = new byte[81920];
				int bytesRead;
				while ((bytesRead = await sqliteStream.ReadAsync(buffer, cancellationToken)) > 0)
				{
					totalCopied += bytesRead;
					if (totalCopied > MaxFileSizeBytes)
					{
						throw new InvalidOperationException($"SQLite file exceeds maximum allowed size of {MaxFileSizeBytes / (1024 * 1024)} MB.");
					}
					await fs.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
				}
			}

			return await ImportFromFileAsync(tempPath, cancellationToken);
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

	private async Task<BackupImportResult> ImportFromFileAsync(string sqlitePath, CancellationToken cancellationToken)
	{
		ValidateSqliteFile(sqlitePath);

		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
			await context.Database.BeginTransactionAsync(cancellationToken);

		try
		{
			await using SqliteConnection sqlite = new($"Data Source={sqlitePath};Mode=ReadOnly;Pooling=False");
			await sqlite.OpenAsync(cancellationToken);

			int exportVersion = ReadExportVersion(sqlite);

			// Import in dependency order: independent entities first, then dependent ones.
			// Accounts must come before Cards (FK constraint on Cards.AccountId → Accounts.Id).
			(int accountsCreated, int accountsUpdated) = await UpsertAccountsAsync(context, sqlite, exportVersion, cancellationToken);
			(int cardsCreated, int cardsUpdated) = await UpsertCardsAsync(context, sqlite, exportVersion, cancellationToken);
			(int categoriesCreated, int categoriesUpdated) = await UpsertCategoriesAsync(context, sqlite, cancellationToken);
			(int subcategoriesCreated, int subcategoriesUpdated) = await UpsertSubcategoriesAsync(context, sqlite, cancellationToken);
			(int itemTemplatesCreated, int itemTemplatesUpdated) = await UpsertItemTemplatesAsync(context, sqlite, cancellationToken);
			(int receiptsCreated, int receiptsUpdated) = await UpsertReceiptsAsync(context, sqlite, exportVersion, cancellationToken);
			(int receiptItemsCreated, int receiptItemsUpdated) = await UpsertReceiptItemsAsync(context, sqlite, cancellationToken);
			(int transactionsCreated, int transactionsUpdated) = await UpsertTransactionsAsync(context, sqlite, exportVersion, cancellationToken);
			(int adjustmentsCreated, int adjustmentsUpdated) = await UpsertAdjustmentsAsync(context, sqlite, cancellationToken);

			// v4 tables. Each method is gated on export_version >= 4, so older backups (v1-3)
			// skip them entirely and restore with these tables treated as absent. YnabAccountMappings
			// reference Accounts and YnabSyncRecords reference Transactions — both already imported
			// above — so the FK import order is satisfied.
			(int ynabSelectedBudgetsCreated, int ynabSelectedBudgetsUpdated) = await UpsertYnabSelectedBudgetsAsync(context, sqlite, exportVersion, cancellationToken);
			(int ynabAccountMappingsCreated, int ynabAccountMappingsUpdated) = await UpsertYnabAccountMappingsAsync(context, sqlite, exportVersion, cancellationToken);
			(int ynabCategoryMappingsCreated, int ynabCategoryMappingsUpdated) = await UpsertYnabCategoryMappingsAsync(context, sqlite, exportVersion, cancellationToken);
			(int ynabSyncRecordsCreated, int ynabSyncRecordsUpdated) = await UpsertYnabSyncRecordsAsync(context, sqlite, exportVersion, cancellationToken);
			(int normalizedDescriptionsCreated, int normalizedDescriptionsUpdated) = await UpsertNormalizedDescriptionsAsync(context, sqlite, exportVersion, cancellationToken);
			(int normalizedDescriptionSettingsCreated, int normalizedDescriptionSettingsUpdated) = await UpsertNormalizedDescriptionSettingsAsync(context, sqlite, exportVersion, cancellationToken);

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
				normalizedDescriptionSettingsCreated, normalizedDescriptionSettingsUpdated);

			logger.LogInformation(
				"Backup import complete: {TotalCreated} created, {TotalUpdated} updated",
				result.TotalCreated, result.TotalUpdated);

			return result;
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

	private static bool TableExists(SqliteConnection sqlite, string tableName)
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

		return int.TryParse(result.ToString(), out int version) ? version : 1;
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

			await context.SaveChangesAsync(cancellationToken);
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

		await context.SaveChangesAsync(cancellationToken);
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

		await context.SaveChangesAsync(cancellationToken);
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

		await context.SaveChangesAsync(cancellationToken);
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

		await context.SaveChangesAsync(cancellationToken);
		return (created, updated);
	}

	private static async Task<(int Created, int Updated)> UpsertItemTemplatesAsync(
		ApplicationDbContext context, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		if (!TableExists(sqlite, "item_templates"))
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = "SELECT id, name, default_category, default_subcategory, default_unit_price, default_unit_price_currency, default_item_code, description FROM item_templates";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			string name = reader.GetString(1);
			string? defaultCategory = reader.IsDBNull(2) ? null : reader.GetString(2);
			string? defaultSubcategory = reader.IsDBNull(3) ? null : reader.GetString(3);
			decimal? defaultUnitPrice = reader.IsDBNull(4) ? null : reader.GetDecimal(4);
			Currency? defaultUnitPriceCurrency = reader.IsDBNull(5) ? null : Enum.Parse<Currency>(reader.GetString(5));
			string? defaultItemCode = reader.IsDBNull(6) ? null : reader.GetString(6);
			string? description = reader.IsDBNull(7) ? null : reader.GetString(7);

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
				});
				created++;
			}
		}

		await context.SaveChangesAsync(cancellationToken);
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
			Currency taxAmountCurrency = Enum.Parse<Currency>(reader.GetString(4));
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

		await context.SaveChangesAsync(cancellationToken);
		return (created, updated);
	}

	private static async Task<(int Created, int Updated)> UpsertReceiptItemsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		if (!TableExists(sqlite, "receipt_items"))
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = "SELECT id, receipt_id, receipt_item_code, description, quantity, unit_price, unit_price_currency, total_amount, total_amount_currency, category, subcategory FROM receipt_items";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			Guid receiptId = Guid.Parse(reader.GetString(1));
			string? receiptItemCode = reader.IsDBNull(2) ? null : reader.GetString(2);
			string description = reader.GetString(3);
			decimal quantity = reader.GetDecimal(4);
			decimal unitPrice = reader.GetDecimal(5);
			Currency unitPriceCurrency = Enum.Parse<Currency>(reader.GetString(6));
			decimal totalAmount = reader.GetDecimal(7);
			Currency totalAmountCurrency = Enum.Parse<Currency>(reader.GetString(8));
			string category = reader.GetString(9);
			string? subcategory = reader.IsDBNull(10) ? null : reader.GetString(10);

			ReceiptItemEntity? existing = await context.ReceiptItems
				.IgnoreQueryFilters()
				.FirstOrDefaultAsync(ri => ri.Id == id, cancellationToken);
			if (existing is not null)
			{
				existing.ReceiptId = receiptId;
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
				});
				created++;
			}
		}

		await context.SaveChangesAsync(cancellationToken);
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

		// Resolve Transaction.AccountId by joining the Card's parent Account at import time.
		// The backup transactions table does not carry a separate account_id column (v3
		// introduced accounts as a distinct table but did not denormalize onto transactions).
		// Cards have already been upserted above; Card.AccountId is non-nullable post-575.
		Dictionary<Guid, Guid> cardAccountIdByCardId = await context.Cards
			.AsNoTracking()
			.ToDictionaryAsync(c => c.Id, c => c.AccountId, cancellationToken);

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
			Currency amountCurrency = Enum.Parse<Currency>(reader.GetString(4));
			DateOnly date = DateOnly.Parse(reader.GetString(5), CultureInfo.InvariantCulture);

			// Fall back to cardId when the Card is missing from the lookup (shouldn't happen
			// in practice — Cards are upserted before Transactions — but defensive).
			Guid accountId = cardAccountIdByCardId.TryGetValue(cardId, out Guid parent)
				? parent
				: cardId;

			TransactionEntity? existing = await context.Transactions
				.IgnoreQueryFilters()
				.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
			if (existing is not null)
			{
				existing.ReceiptId = receiptId;
				existing.AccountId = accountId;
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
					AccountId = accountId,
					CardId = cardId,
					Amount = amount,
					AmountCurrency = amountCurrency,
					Date = date,
				});
				created++;
			}
		}

		await context.SaveChangesAsync(cancellationToken);
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
			Currency amountCurrency = Enum.Parse<Currency>(reader.GetString(4));
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

		await context.SaveChangesAsync(cancellationToken);
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
			string budgetId = reader.GetString(1);
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

		await context.SaveChangesAsync(cancellationToken);
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
			string ynabBudgetId = reader.GetString(4);
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

		await context.SaveChangesAsync(cancellationToken);
		return (created, updated);
	}

	private static async Task<(int Created, int Updated)> UpsertYnabCategoryMappingsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion, CancellationToken cancellationToken)
	{
		if (exportVersion < 4 || !TableExists(sqlite, "ynab_category_mappings"))
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = "SELECT id, receipts_category, ynab_category_id, ynab_category_name, ynab_category_group_name, ynab_budget_id, created_at, updated_at FROM ynab_category_mappings";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			string receiptsCategory = reader.GetString(1);
			string ynabCategoryId = reader.GetString(2);
			string ynabCategoryName = reader.GetString(3);
			string ynabCategoryGroupName = reader.GetString(4);
			string ynabBudgetId = reader.GetString(5);
			DateTimeOffset createdAt = ParseTimestamp(reader.GetString(6));
			DateTimeOffset updatedAt = ParseTimestamp(reader.GetString(7));

			YnabCategoryMappingEntity? existing = await context.YnabCategoryMappings.FindAsync([id], cancellationToken);
			if (existing is not null)
			{
				existing.ReceiptsCategory = receiptsCategory;
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

		await context.SaveChangesAsync(cancellationToken);
		return (created, updated);
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
		cmd.CommandText = "SELECT id, local_transaction_id, ynab_transaction_id, ynab_budget_id, ynab_account_id, sync_type, sync_status, synced_at_utc, last_error, created_at, updated_at FROM ynab_sync_records";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			Guid localTransactionId = Guid.Parse(reader.GetString(1));
			string? ynabTransactionId = reader.IsDBNull(2) ? null : reader.GetString(2);
			string ynabBudgetId = reader.GetString(3);
			string? ynabAccountId = reader.IsDBNull(4) ? null : reader.GetString(4);
			YnabSyncType syncType = Enum.Parse<YnabSyncType>(reader.GetString(5));
			YnabSyncStatus syncStatus = Enum.Parse<YnabSyncStatus>(reader.GetString(6));
			DateTimeOffset? syncedAtUtc = reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7));
			string? lastError = reader.IsDBNull(8) ? null : reader.GetString(8);
			DateTimeOffset createdAt = ParseTimestamp(reader.GetString(9));
			DateTimeOffset updatedAt = ParseTimestamp(reader.GetString(10));

			YnabSyncRecordEntity? existing = await context.YnabSyncRecords
				.IgnoreQueryFilters()
				.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
			if (existing is not null)
			{
				existing.LocalTransactionId = localTransactionId;
				existing.YnabTransactionId = ynabTransactionId;
				existing.YnabBudgetId = ynabBudgetId;
				existing.YnabAccountId = ynabAccountId;
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

		await context.SaveChangesAsync(cancellationToken);
		return (created, updated);
	}

	// The embedding vector is intentionally not restored (see BackupService for rationale): new
	// rows are created with a null embedding and repopulated by the embedding pipeline, and the
	// embedding on an existing row is left untouched so a restore never destroys a valid vector.
	private static async Task<(int Created, int Updated)> UpsertNormalizedDescriptionsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion, CancellationToken cancellationToken)
	{
		if (exportVersion < 4 || !TableExists(sqlite, "normalized_descriptions"))
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = "SELECT id, canonical_name, status, created_at FROM normalized_descriptions";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			string canonicalName = reader.GetString(1);
			NormalizedDescriptionStatus status = Enum.Parse<NormalizedDescriptionStatus>(reader.GetString(2));
			DateTimeOffset createdAt = ParseTimestamp(reader.GetString(3));

			NormalizedDescriptionEntity? existing = await context.NormalizedDescriptions.FindAsync([id], cancellationToken);
			if (existing is not null)
			{
				existing.CanonicalName = canonicalName;
				existing.Status = status;
				existing.CreatedAt = createdAt;
				updated++;
			}
			else
			{
				context.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
				{
					Id = id,
					CanonicalName = canonicalName,
					Status = status,
					CreatedAt = createdAt,
				});
				created++;
			}
		}

		await context.SaveChangesAsync(cancellationToken);
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

		await context.SaveChangesAsync(cancellationToken);
		return (created, updated);
	}
}
