using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// RECEIPTS-876: an editable display label, and a remembered "no".
/// </summary>
/// <remarks>
/// The Rejected status needs no schema change — Status is stored as a string via
/// HasConversion, so a third enum member is data, not DDL.
/// </remarks>
public partial class AddNormalizedDescriptionDisplayLabelAndRejectedStatus : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<string>(
			name: "DisplayLabel",
			schema: "matching",
			table: "NormalizedDescriptions",
			type: "text",
			maxLength: 200,
			nullable: true);

		// Unique functional index on the EFFECTIVE display name. EF cannot model functional
		// indexes, hence raw SQL.
		//
		// Over the COALESCE rather than over "DisplayLabel" alone: the collision that actually
		// bites is renaming row B to match row A's un-renamed CanonicalName, which an index on
		// the label column would happily allow. The two rows would then be indistinguishable
		// everywhere a user looks, including as two identically-named buckets in the spending
		// report.
		//
		// Safe to create against existing data: DisplayLabel is NULL on every row at this
		// point, so the expression reduces to lower("CanonicalName"), which
		// IX_NormalizedDescriptions_CanonicalName_Lower already guarantees is unique.
		migrationBuilder.Sql(
			"""
			CREATE UNIQUE INDEX "IX_NormalizedDescriptions_DisplayName_Lower"
			    ON "matching"."NormalizedDescriptions" (lower(COALESCE("DisplayLabel", "CanonicalName")));
			""");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""DROP INDEX IF EXISTS "matching"."IX_NormalizedDescriptions_DisplayName_Lower";""");

		// Rows rejected while this migration was applied keep a status the reverted enum cannot
		// parse, and Enum.Parse would then throw on every read of the table. Fold them back to
		// PendingReview so the downgrade leaves a readable table: their items are already
		// unlinked, so they land in the review queue exactly as they would have if they had
		// never been rejected.
		migrationBuilder.Sql(
			"""
			UPDATE "matching"."NormalizedDescriptions"
			   SET "Status" = 'PendingReview'
			 WHERE "Status" = 'Rejected';
			""");

		migrationBuilder.DropColumn(
			name: "DisplayLabel",
			schema: "matching",
			table: "NormalizedDescriptions");
	}
}
