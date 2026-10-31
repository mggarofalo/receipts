using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// RECEIPTS-1094: GIN trigram indexes for the catalog lexical channel. They cover exactly the two
/// expressions its relevance query filters on, so its <c>LIKE '%token%'</c> terms use a bitmap index
/// scan. EF does not model GIN with an operator class on an expression, so this is raw SQL. pg_trgm
/// was installed by 20260309124500_AddPgTrgmExtensionAndTrigramIndexes.
/// </summary>
public partial class AddNormalizedDescriptionTrigramIndexes : Migration
{
	internal const string CanonicalNameIndex = "IX_NormalizedDescriptions_CanonicalName_trgm";
	internal const string DisplayNameIndex = "IX_NormalizedDescriptions_DisplayName_trgm";

	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql(
			$"""
			CREATE INDEX IF NOT EXISTS "{CanonicalNameIndex}"
			    ON matching."NormalizedDescriptions" USING gin (lower("CanonicalName") gin_trgm_ops);
			CREATE INDEX IF NOT EXISTS "{DisplayNameIndex}"
			    ON matching."NormalizedDescriptions" USING gin (lower(COALESCE("DisplayLabel", "CanonicalName")) gin_trgm_ops);
			""");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql(
			$"""
			DROP INDEX IF EXISTS matching."{DisplayNameIndex}";
			DROP INDEX IF EXISTS matching."{CanonicalNameIndex}";
			""");
	}
}
