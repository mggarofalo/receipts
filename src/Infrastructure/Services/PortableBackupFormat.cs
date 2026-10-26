namespace Infrastructure.Services;

internal static class PortableBackupFormat
{
	public const int CurrentVersion = 11;

	// v5+ is a complete portable snapshot. Legacy partial-file compatibility is handled
	// separately by the importer; missing v5 tables must not silently lose durable state.
	private static IReadOnlyList<string> LegacyRequiredTables { get; } = Array.AsReadOnly<string>([
		"accounts", "cards", "categories", "subcategories", "item_templates", "receipts",
		"receipt_items", "transactions", "adjustments", "ynab_selected_budgets",
		"ynab_account_mappings", "ynab_category_mappings", "ynab_sync_records",
		"normalized_descriptions", "normalized_description_settings", "accepted_duplicate_pairs",
	]);
	// Common domain tables used by offline inspection across supported formats.
	public static IReadOnlyList<string> RequiredTables => LegacyRequiredTables;

	public static IReadOnlyList<string> RequiredTablesFor(int version)
	{
		if (version < 1 || version > CurrentVersion) { throw new InvalidOperationException($"Unsupported backup version {version}."); }
		if (version < 5) { return []; }
		return version < 7 ? LegacyRequiredTables
			: version < 10 ? [.. LegacyRequiredTables, "normalization_catalog"]
			: [.. LegacyRequiredTables, "normalization_catalog", "taxonomy_archive", "portable_deletion_state", .. (version >= 11 ? new[] { "category_mapping_archive" } : Array.Empty<string>())];
	}
}
