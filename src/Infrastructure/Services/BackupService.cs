using System.Globalization;
using Application.Interfaces.Services;
using Infrastructure.Entities.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public class BackupService(
	IDbContextFactory<ApplicationDbContext> dbContextFactory,
	ILogger<BackupService> logger) : IBackupService
{
	public async Task<string> ExportToSqliteAsync(CancellationToken cancellationToken = default)
	{
		string tempPath = Path.Combine(Path.GetTempPath(), $"receipts-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
		logger.LogInformation("Starting SQLite export to {Path}", tempPath);

		await using ApplicationDbContext source = await dbContextFactory.CreateDbContextAsync(cancellationToken);

		string connectionString = $"Data Source={tempPath}";
		await using SqliteConnection sqlite = new(connectionString);
		await sqlite.OpenAsync(cancellationToken);

		await using SqliteTransaction transaction = (SqliteTransaction)await sqlite.BeginTransactionAsync(cancellationToken);

		try
		{
			// The set of exported tables is defined by the CreateSchemaAsync DDL below and the
			// Export* calls that follow. The following tables are DELIBERATELY EXCLUDED from the
			// backup (an intentional decision, not an oversight -- see RECEIPTS-802):
			//
			//   - AuditLogs / AuthAuditLogs: append-only audit/history logs. A backup captures
			//     restorable *state*, not the activity trail that produced it. Re-importing
			//     historical log rows into a target instance would misrepresent when actions
			//     actually occurred there, so the audit trail is left to each instance.
			//   - YnabSyncEvents: the YNAB sync activity log -- the SAME class of data as the audit
			//     logs above (an append-only history of push attempts, not state). NOTE: this is
			//     distinct from YnabSyncRecords (the current per-transaction sync *state*), which
			//     IS exported below via ExportYnabSyncRecordsAsync.
			//   - YnabServerKnowledge: the YNAB delta-sync cursor. It is re-fetchable from YNAB on
			//     the next sync (regenerable derived data, like the embedding vectors also omitted),
			//     so restoring a stale cursor would only risk a bad delta window.
			//   - ASP.NET Identity users and authentication settings: excluded for security.
			//
			// Excluding all log/audit and regenerable-cursor tables is the consistent choice: a
			// backup restores your data, not the history of how it got there.
			await CreateSchemaAsync(sqlite, cancellationToken);
			await ExportAccountsAsync(source, sqlite, cancellationToken);
			await ExportCardsAsync(source, sqlite, cancellationToken);
			await ExportCategoriesAsync(source, sqlite, cancellationToken);
			await ExportSubcategoriesAsync(source, sqlite, cancellationToken);
			await ExportItemTemplatesAsync(source, sqlite, cancellationToken);
			await ExportReceiptsAsync(source, sqlite, cancellationToken);
			await ExportReceiptItemsAsync(source, sqlite, cancellationToken);
			await ExportTransactionsAsync(source, sqlite, cancellationToken);
			await ExportAdjustmentsAsync(source, sqlite, cancellationToken);
			await ExportYnabSelectedBudgetsAsync(source, sqlite, cancellationToken);
			await ExportYnabAccountMappingsAsync(source, sqlite, cancellationToken);
			await ExportYnabCategoryMappingsAsync(source, sqlite, cancellationToken);
			await ExportYnabSyncRecordsAsync(source, sqlite, cancellationToken);
			await ExportNormalizedDescriptionsAsync(source, sqlite, cancellationToken);
			await ExportNormalizedDescriptionSettingsAsync(source, sqlite, cancellationToken);
			await WriteMetadataAsync(sqlite, cancellationToken);

			await transaction.CommitAsync(cancellationToken);
		}
		catch
		{
			await transaction.RollbackAsync(cancellationToken);
			if (File.Exists(tempPath))
			{
				File.Delete(tempPath);
			}
			throw;
		}

		logger.LogInformation("SQLite export completed: {Path}", tempPath);
		return tempPath;
	}

	internal static async Task CreateSchemaAsync(SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		string[] ddl =
		[
			"""
			CREATE TABLE backup_metadata (
				key TEXT NOT NULL PRIMARY KEY,
				value TEXT NOT NULL
			)
			""",
			"""
			CREATE TABLE accounts (
				id TEXT NOT NULL PRIMARY KEY,
				name TEXT NOT NULL,
				is_active INTEGER NOT NULL
			)
			""",
			"""
			CREATE TABLE cards (
				id TEXT NOT NULL PRIMARY KEY,
				card_code TEXT NOT NULL,
				name TEXT NOT NULL,
				is_active INTEGER NOT NULL,
				account_id TEXT NOT NULL,
				FOREIGN KEY (account_id) REFERENCES accounts(id)
			)
			""",
			"""
			CREATE TABLE categories (
				id TEXT NOT NULL PRIMARY KEY,
				name TEXT NOT NULL,
				description TEXT,
				is_active INTEGER NOT NULL
			)
			""",
			"""
			CREATE TABLE subcategories (
				id TEXT NOT NULL PRIMARY KEY,
				name TEXT NOT NULL,
				category_id TEXT NOT NULL,
				description TEXT,
				is_active INTEGER NOT NULL,
				FOREIGN KEY (category_id) REFERENCES categories(id)
			)
			""",
			"""
			CREATE TABLE item_templates (
				id TEXT NOT NULL PRIMARY KEY,
				name TEXT NOT NULL,
				default_category TEXT,
				default_subcategory TEXT,
				default_unit_price TEXT,
				default_unit_price_currency TEXT,
				default_item_code TEXT,
				description TEXT
			)
			""",
			"""
			CREATE TABLE receipts (
				id TEXT NOT NULL PRIMARY KEY,
				location TEXT NOT NULL,
				date TEXT NOT NULL,
				tax_amount TEXT NOT NULL,
				tax_amount_currency TEXT NOT NULL,
				original_image_path TEXT,
				processed_image_path TEXT
			)
			""",
			"""
			CREATE TABLE receipt_items (
				id TEXT NOT NULL PRIMARY KEY,
				receipt_id TEXT NOT NULL,
				receipt_item_code TEXT,
				description TEXT NOT NULL,
				quantity TEXT NOT NULL,
				unit_price TEXT NOT NULL,
				unit_price_currency TEXT NOT NULL,
				total_amount TEXT NOT NULL,
				total_amount_currency TEXT NOT NULL,
				category TEXT NOT NULL,
				subcategory TEXT,
				FOREIGN KEY (receipt_id) REFERENCES receipts(id)
			)
			""",
			"""
			CREATE TABLE transactions (
				id TEXT NOT NULL PRIMARY KEY,
				receipt_id TEXT NOT NULL,
				card_id TEXT NOT NULL,
				amount TEXT NOT NULL,
				amount_currency TEXT NOT NULL,
				date TEXT NOT NULL,
				FOREIGN KEY (receipt_id) REFERENCES receipts(id),
				FOREIGN KEY (card_id) REFERENCES cards(id)
			)
			""",
			"""
			CREATE TABLE adjustments (
				id TEXT NOT NULL PRIMARY KEY,
				receipt_id TEXT NOT NULL,
				type TEXT NOT NULL,
				amount TEXT NOT NULL,
				amount_currency TEXT NOT NULL,
				description TEXT,
				FOREIGN KEY (receipt_id) REFERENCES receipts(id)
			)
			""",
			"""
			CREATE TABLE ynab_selected_budgets (
				id TEXT NOT NULL PRIMARY KEY,
				budget_id TEXT NOT NULL,
				updated_at TEXT NOT NULL
			)
			""",
			"""
			CREATE TABLE ynab_account_mappings (
				id TEXT NOT NULL PRIMARY KEY,
				receipts_account_id TEXT NOT NULL,
				ynab_account_id TEXT NOT NULL,
				ynab_account_name TEXT NOT NULL,
				ynab_budget_id TEXT NOT NULL,
				created_at TEXT NOT NULL,
				updated_at TEXT NOT NULL,
				FOREIGN KEY (receipts_account_id) REFERENCES accounts(id)
			)
			""",
			"""
			CREATE TABLE ynab_category_mappings (
				id TEXT NOT NULL PRIMARY KEY,
				receipts_category TEXT NOT NULL,
				ynab_category_id TEXT NOT NULL,
				ynab_category_name TEXT NOT NULL,
				ynab_category_group_name TEXT NOT NULL,
				ynab_budget_id TEXT NOT NULL,
				created_at TEXT NOT NULL,
				updated_at TEXT NOT NULL
			)
			""",
			"""
			CREATE TABLE ynab_sync_records (
				id TEXT NOT NULL PRIMARY KEY,
				local_transaction_id TEXT NOT NULL,
				ynab_transaction_id TEXT,
				ynab_budget_id TEXT NOT NULL,
				ynab_account_id TEXT,
				sync_type TEXT NOT NULL,
				sync_status TEXT NOT NULL,
				synced_at_utc TEXT,
				last_error TEXT,
				created_at TEXT NOT NULL,
				updated_at TEXT NOT NULL,
				FOREIGN KEY (local_transaction_id) REFERENCES transactions(id)
			)
			""",
			"""
			CREATE TABLE normalized_descriptions (
				id TEXT NOT NULL PRIMARY KEY,
				canonical_name TEXT NOT NULL,
				status TEXT NOT NULL,
				created_at TEXT NOT NULL
			)
			""",
			"""
			CREATE TABLE normalized_description_settings (
				id TEXT NOT NULL PRIMARY KEY,
				auto_accept_threshold TEXT NOT NULL,
				pending_review_threshold TEXT NOT NULL,
				updated_at TEXT NOT NULL
			)
			""",
		];

		foreach (string sql in ddl)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	private static async Task ExportAccountsAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<AccountEntity> accounts = await source.Accounts.AsNoTracking().ToListAsync(cancellationToken);

		const string sql = "INSERT INTO accounts (id, name, is_active) VALUES ($id, $name, $active)";
		foreach (AccountEntity account in accounts)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", account.Id.ToString());
			cmd.Parameters.AddWithValue("$name", account.Name);
			cmd.Parameters.AddWithValue("$active", account.IsActive ? 1 : 0);
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	private static async Task ExportCardsAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<CardEntity> cards = await source.Cards.AsNoTracking().ToListAsync(cancellationToken);

		const string sql = "INSERT INTO cards (id, card_code, name, is_active, account_id) VALUES ($id, $code, $name, $active, $accountId)";
		foreach (CardEntity card in cards)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", card.Id.ToString());
			cmd.Parameters.AddWithValue("$code", card.CardCode);
			cmd.Parameters.AddWithValue("$name", card.Name);
			cmd.Parameters.AddWithValue("$active", card.IsActive ? 1 : 0);
			cmd.Parameters.AddWithValue("$accountId", card.AccountId.ToString());
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	private static async Task ExportCategoriesAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<CategoryEntity> categories = await source.Categories
			.AsNoTracking()
			.Where(c => c.DeletedAt == null)
			.ToListAsync(cancellationToken);

		const string sql = "INSERT INTO categories (id, name, description, is_active) VALUES ($id, $name, $desc, $active)";
		foreach (CategoryEntity category in categories)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", category.Id.ToString());
			cmd.Parameters.AddWithValue("$name", category.Name);
			cmd.Parameters.AddWithValue("$desc", (object?)category.Description ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$active", category.IsActive ? 1 : 0);
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	private static async Task ExportSubcategoriesAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<SubcategoryEntity> subcategories = await source.Subcategories
			.AsNoTracking()
			.Where(s => s.DeletedAt == null)
			.ToListAsync(cancellationToken);

		const string sql = "INSERT INTO subcategories (id, name, category_id, description, is_active) VALUES ($id, $name, $catId, $desc, $active)";
		foreach (SubcategoryEntity sub in subcategories)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", sub.Id.ToString());
			cmd.Parameters.AddWithValue("$name", sub.Name);
			cmd.Parameters.AddWithValue("$catId", sub.CategoryId.ToString());
			cmd.Parameters.AddWithValue("$desc", (object?)sub.Description ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$active", sub.IsActive ? 1 : 0);
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	private static async Task ExportItemTemplatesAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<ItemTemplateEntity> templates = await source.ItemTemplates
			.AsNoTracking()
			.Where(t => t.DeletedAt == null)
			.ToListAsync(cancellationToken);

		const string sql = """
			INSERT INTO item_templates (id, name, default_category, default_subcategory,
				default_unit_price, default_unit_price_currency,
				default_item_code, description)
			VALUES ($id, $name, $cat, $subcat, $price, $priceCurrency, $itemCode, $desc)
			""";

		foreach (ItemTemplateEntity template in templates)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", template.Id.ToString());
			cmd.Parameters.AddWithValue("$name", template.Name);
			cmd.Parameters.AddWithValue("$cat", (object?)template.DefaultCategory ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$subcat", (object?)template.DefaultSubcategory ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$price", template.DefaultUnitPrice.HasValue ? template.DefaultUnitPrice.Value.ToString(CultureInfo.InvariantCulture) : DBNull.Value);
			cmd.Parameters.AddWithValue("$priceCurrency", template.DefaultUnitPriceCurrency.HasValue ? template.DefaultUnitPriceCurrency.Value.ToString() : DBNull.Value);
			cmd.Parameters.AddWithValue("$itemCode", (object?)template.DefaultItemCode ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$desc", (object?)template.Description ?? DBNull.Value);
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	private static async Task ExportReceiptsAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<ReceiptEntity> receipts = await source.Receipts
			.AsNoTracking()
			.Where(r => r.DeletedAt == null)
			.ToListAsync(cancellationToken);

		const string sql = "INSERT INTO receipts (id, location, date, tax_amount, tax_amount_currency, original_image_path, processed_image_path) VALUES ($id, $loc, $date, $tax, $taxCurrency, $origImg, $procImg)";
		foreach (ReceiptEntity receipt in receipts)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", receipt.Id.ToString());
			cmd.Parameters.AddWithValue("$loc", receipt.Location);
			cmd.Parameters.AddWithValue("$date", receipt.Date.ToString("O"));
			cmd.Parameters.AddWithValue("$tax", receipt.TaxAmount.ToString(CultureInfo.InvariantCulture));
			cmd.Parameters.AddWithValue("$taxCurrency", receipt.TaxAmountCurrency.ToString());
			cmd.Parameters.AddWithValue("$origImg", (object?)receipt.OriginalImagePath ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$procImg", (object?)receipt.ProcessedImagePath ?? DBNull.Value);
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	private static async Task ExportReceiptItemsAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<ReceiptItemEntity> items = await source.ReceiptItems
			.AsNoTracking()
			.Where(i => i.DeletedAt == null)
			.ToListAsync(cancellationToken);

		const string sql = """
			INSERT INTO receipt_items (id, receipt_id, receipt_item_code, description, quantity,
				unit_price, unit_price_currency, total_amount, total_amount_currency,
				category, subcategory)
			VALUES ($id, $receiptId, $itemCode, $desc, $qty, $unitPrice, $unitPriceCurrency,
				$totalAmt, $totalAmtCurrency, $cat, $subcat)
			""";

		foreach (ReceiptItemEntity item in items)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", item.Id.ToString());
			cmd.Parameters.AddWithValue("$receiptId", item.ReceiptId.ToString());
			cmd.Parameters.AddWithValue("$itemCode", (object?)item.ReceiptItemCode ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$desc", item.Description);
			cmd.Parameters.AddWithValue("$qty", item.Quantity.ToString(CultureInfo.InvariantCulture));
			cmd.Parameters.AddWithValue("$unitPrice", item.UnitPrice.ToString(CultureInfo.InvariantCulture));
			cmd.Parameters.AddWithValue("$unitPriceCurrency", item.UnitPriceCurrency.ToString());
			cmd.Parameters.AddWithValue("$totalAmt", item.TotalAmount.ToString(CultureInfo.InvariantCulture));
			cmd.Parameters.AddWithValue("$totalAmtCurrency", item.TotalAmountCurrency.ToString());
			cmd.Parameters.AddWithValue("$cat", item.Category);
			cmd.Parameters.AddWithValue("$subcat", (object?)item.Subcategory ?? DBNull.Value);
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	private static async Task ExportTransactionsAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<TransactionEntity> transactions = await source.Transactions
			.AsNoTracking()
			.Where(t => t.DeletedAt == null)
			.ToListAsync(cancellationToken);

		const string sql = "INSERT INTO transactions (id, receipt_id, card_id, amount, amount_currency, date) VALUES ($id, $receiptId, $cardId, $amt, $amtCurrency, $date)";
		foreach (TransactionEntity txn in transactions)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", txn.Id.ToString());
			cmd.Parameters.AddWithValue("$receiptId", txn.ReceiptId.ToString());
			cmd.Parameters.AddWithValue("$cardId", txn.CardId.ToString());
			cmd.Parameters.AddWithValue("$amt", txn.Amount.ToString(CultureInfo.InvariantCulture));
			cmd.Parameters.AddWithValue("$amtCurrency", txn.AmountCurrency.ToString());
			cmd.Parameters.AddWithValue("$date", txn.Date.ToString("O"));
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	private static async Task ExportAdjustmentsAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<AdjustmentEntity> adjustments = await source.Adjustments
			.AsNoTracking()
			.Where(a => a.DeletedAt == null)
			.ToListAsync(cancellationToken);

		const string sql = "INSERT INTO adjustments (id, receipt_id, type, amount, amount_currency, description) VALUES ($id, $receiptId, $type, $amt, $amtCurrency, $desc)";
		foreach (AdjustmentEntity adj in adjustments)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", adj.Id.ToString());
			cmd.Parameters.AddWithValue("$receiptId", adj.ReceiptId.ToString());
			cmd.Parameters.AddWithValue("$type", adj.Type.ToString());
			cmd.Parameters.AddWithValue("$amt", adj.Amount.ToString(CultureInfo.InvariantCulture));
			cmd.Parameters.AddWithValue("$amtCurrency", adj.AmountCurrency.ToString());
			cmd.Parameters.AddWithValue("$desc", (object?)adj.Description ?? DBNull.Value);
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	// v4: YNAB configuration/state and normalized descriptions. These are gated on
	// export_version >= 4 in the importer so older backups (v1-3) still restore with the
	// tables treated as absent.
	private static async Task ExportYnabSelectedBudgetsAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<YnabSelectedBudgetEntity> budgets = await source.YnabSelectedBudgets.AsNoTracking().ToListAsync(cancellationToken);

		const string sql = "INSERT INTO ynab_selected_budgets (id, budget_id, updated_at) VALUES ($id, $budgetId, $updatedAt)";
		foreach (YnabSelectedBudgetEntity budget in budgets)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", budget.Id.ToString());
			cmd.Parameters.AddWithValue("$budgetId", budget.BudgetId);
			cmd.Parameters.AddWithValue("$updatedAt", budget.UpdatedAt.ToString("O"));
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	private static async Task ExportYnabAccountMappingsAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<YnabAccountMappingEntity> mappings = await source.YnabAccountMappings.AsNoTracking().ToListAsync(cancellationToken);

		const string sql = """
			INSERT INTO ynab_account_mappings (id, receipts_account_id, ynab_account_id, ynab_account_name,
				ynab_budget_id, created_at, updated_at)
			VALUES ($id, $accountId, $ynabAccountId, $ynabAccountName, $ynabBudgetId, $createdAt, $updatedAt)
			""";

		foreach (YnabAccountMappingEntity mapping in mappings)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", mapping.Id.ToString());
			cmd.Parameters.AddWithValue("$accountId", mapping.ReceiptsAccountId.ToString());
			cmd.Parameters.AddWithValue("$ynabAccountId", mapping.YnabAccountId);
			cmd.Parameters.AddWithValue("$ynabAccountName", mapping.YnabAccountName);
			cmd.Parameters.AddWithValue("$ynabBudgetId", mapping.YnabBudgetId);
			cmd.Parameters.AddWithValue("$createdAt", mapping.CreatedAt.ToString("O"));
			cmd.Parameters.AddWithValue("$updatedAt", mapping.UpdatedAt.ToString("O"));
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	private static async Task ExportYnabCategoryMappingsAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<YnabCategoryMappingEntity> mappings = await source.YnabCategoryMappings.AsNoTracking().ToListAsync(cancellationToken);

		const string sql = """
			INSERT INTO ynab_category_mappings (id, receipts_category, ynab_category_id, ynab_category_name,
				ynab_category_group_name, ynab_budget_id, created_at, updated_at)
			VALUES ($id, $receiptsCategory, $ynabCategoryId, $ynabCategoryName, $ynabCategoryGroupName,
				$ynabBudgetId, $createdAt, $updatedAt)
			""";

		foreach (YnabCategoryMappingEntity mapping in mappings)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", mapping.Id.ToString());
			cmd.Parameters.AddWithValue("$receiptsCategory", mapping.ReceiptsCategory);
			cmd.Parameters.AddWithValue("$ynabCategoryId", mapping.YnabCategoryId);
			cmd.Parameters.AddWithValue("$ynabCategoryName", mapping.YnabCategoryName);
			cmd.Parameters.AddWithValue("$ynabCategoryGroupName", mapping.YnabCategoryGroupName);
			cmd.Parameters.AddWithValue("$ynabBudgetId", mapping.YnabBudgetId);
			cmd.Parameters.AddWithValue("$createdAt", mapping.CreatedAt.ToString("O"));
			cmd.Parameters.AddWithValue("$updatedAt", mapping.UpdatedAt.ToString("O"));
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	// Soft-deleted sync records are excluded by the entity's global query filter, mirroring
	// the export behaviour of the other soft-deletable tables.
	private static async Task ExportYnabSyncRecordsAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<YnabSyncRecordEntity> records = await source.YnabSyncRecords.AsNoTracking().ToListAsync(cancellationToken);

		const string sql = """
			INSERT INTO ynab_sync_records (id, local_transaction_id, ynab_transaction_id, ynab_budget_id,
				ynab_account_id, sync_type, sync_status, synced_at_utc, last_error, created_at, updated_at)
			VALUES ($id, $localTransactionId, $ynabTransactionId, $ynabBudgetId, $ynabAccountId, $syncType,
				$syncStatus, $syncedAtUtc, $lastError, $createdAt, $updatedAt)
			""";

		foreach (YnabSyncRecordEntity record in records)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", record.Id.ToString());
			cmd.Parameters.AddWithValue("$localTransactionId", record.LocalTransactionId.ToString());
			cmd.Parameters.AddWithValue("$ynabTransactionId", (object?)record.YnabTransactionId ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$ynabBudgetId", record.YnabBudgetId);
			cmd.Parameters.AddWithValue("$ynabAccountId", (object?)record.YnabAccountId ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$syncType", record.SyncType.ToString());
			cmd.Parameters.AddWithValue("$syncStatus", record.SyncStatus.ToString());
			cmd.Parameters.AddWithValue("$syncedAtUtc", record.SyncedAtUtc.HasValue ? record.SyncedAtUtc.Value.ToString("O") : DBNull.Value);
			cmd.Parameters.AddWithValue("$lastError", (object?)record.LastError ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$createdAt", record.CreatedAt.ToString("O"));
			cmd.Parameters.AddWithValue("$updatedAt", record.UpdatedAt.ToString("O"));
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	// The embedding vector is intentionally NOT exported: it is large, regenerable derived
	// data whose dimension is a build-time constant that has changed across versions
	// (384 -> 1024). Restoring a stale-dimension vector would abort the whole import, so the
	// embedding is left null and repopulated by the embedding-generation pipeline after restore.
	private static async Task ExportNormalizedDescriptionsAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<NormalizedDescriptionEntity> descriptions = await source.NormalizedDescriptions.AsNoTracking().ToListAsync(cancellationToken);

		const string sql = "INSERT INTO normalized_descriptions (id, canonical_name, status, created_at) VALUES ($id, $canonicalName, $status, $createdAt)";
		foreach (NormalizedDescriptionEntity description in descriptions)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", description.Id.ToString());
			cmd.Parameters.AddWithValue("$canonicalName", description.CanonicalName);
			cmd.Parameters.AddWithValue("$status", description.Status.ToString());
			cmd.Parameters.AddWithValue("$createdAt", description.CreatedAt.ToString("O"));
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	// Singleton settings row holding non-regenerable user thresholds. Doubles are written with
	// InvariantCulture for the same reason decimals are (RECEIPTS-771) — a comma-decimal host
	// must not corrupt the values.
	private static async Task ExportNormalizedDescriptionSettingsAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		List<NormalizedDescriptionSettingsEntity> settings = await source.NormalizedDescriptionSettings.AsNoTracking().ToListAsync(cancellationToken);

		const string sql = """
			INSERT INTO normalized_description_settings (id, auto_accept_threshold, pending_review_threshold, updated_at)
			VALUES ($id, $autoAccept, $pendingReview, $updatedAt)
			""";
		foreach (NormalizedDescriptionSettingsEntity setting in settings)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$id", setting.Id.ToString());
			cmd.Parameters.AddWithValue("$autoAccept", setting.AutoAcceptThreshold.ToString(CultureInfo.InvariantCulture));
			cmd.Parameters.AddWithValue("$pendingReview", setting.PendingReviewThreshold.ToString(CultureInfo.InvariantCulture));
			cmd.Parameters.AddWithValue("$updatedAt", setting.UpdatedAt.ToString("O"));
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	private static async Task WriteMetadataAsync(SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		Dictionary<string, string> metadata = new()
		{
			["export_version"] = "4",
			["exported_at"] = DateTimeOffset.UtcNow.ToString("O"),
			["format"] = "receipts-sqlite-backup",
		};

		const string sql = "INSERT INTO backup_metadata (key, value) VALUES ($key, $value)";
		foreach (KeyValuePair<string, string> kv in metadata)
		{
			await using SqliteCommand cmd = sqlite.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$key", kv.Key);
			cmd.Parameters.AddWithValue("$value", kv.Value);
			await cmd.ExecuteNonQueryAsync(cancellationToken);
		}
	}
}
