namespace Infrastructure.Services;

internal static class PortableBackupFormat
{
	public const int CurrentVersion = 6;

	// v5+ is a complete portable snapshot. Legacy partial-file compatibility is handled
	// separately by the importer; missing v5 tables must not silently lose durable state.
	public static IReadOnlyList<string> RequiredTables { get; } = Array.AsReadOnly<string>([
		"accounts", "cards", "categories", "subcategories", "item_templates", "receipts",
		"receipt_items", "transactions", "adjustments", "ynab_selected_budgets",
		"ynab_account_mappings", "ynab_category_mappings", "ynab_sync_records",
		"normalized_descriptions", "normalized_description_settings", "accepted_duplicate_pairs",
	]);
}
