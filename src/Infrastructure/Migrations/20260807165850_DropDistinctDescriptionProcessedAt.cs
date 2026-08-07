using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// Drops the vestigial <c>DistinctDescriptions.ProcessedAt</c> column and the trigram index
/// that existed to serve the same removed feature (RECEIPTS-859).
/// </summary>
/// <remarks>
/// Both belonged to <c>ItemSimilarityEdgeRefresher</c>, removed in RECEIPTS-836.
///
/// <c>ProcessedAt</c> was that service's watermark for finding descriptions whose similarity
/// edges had not been computed yet. It has had exactly one writer since — the reconciliation
/// INSERT in <c>ApplicationDbContext</c>, writing a literal NULL — and no reader at all.
///
/// <c>IX_DistinctDescriptions_Description_trgm</c> served the refresher's <c>%</c> similarity
/// join. Every trigram query left in the codebase runs against <c>library.ItemTemplates</c> or
/// <c>receipts.ReceiptItems</c>, each of which carries its own GIN index from
/// <c>AddPgTrgmExtensionAndTrigramIndexes</c>. Nothing queries this table by similarity at all:
/// its only access is the reconciliation INSERT/DELETE, both keyed on the primary key. Keeping
/// the index would cost a GIN write on every receipt save and buy nothing, so it goes too.
///
/// The table itself stays. It still records "which descriptions active receipt items use",
/// which the normalization pipeline is built around.
///
/// No <c>DELETE FROM</c> here, so the FK-cascade hazard does not apply — and nothing has an FK
/// to this table in any case: the only ones were from <c>ItemSimilarityEdges</c>, dropped with
/// that table in RECEIPTS-836.
/// </remarks>
public partial class DropDistinctDescriptionProcessedAt : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(
			name: "ProcessedAt",
			schema: "matching",
			table: "DistinctDescriptions");

		// Raw SQL because EF does not model custom index methods
		// (`USING gin (col gin_trgm_ops)`), which is also why it was created that way.
		//
		// Both schemas, because the index was created unqualified in
		// 20260419162706_AddDistinctDescriptionsAndItemSimilarityEdges — before
		// OrganizeTablesIntoSchemas moved the table into `matching`. ALTER TABLE ... SET
		// SCHEMA carries a table's indexes with it, so `matching` is where it should be; the
		// second drop is insurance against any database where it is not, since a stray GIN
		// index in `public` would otherwise linger forever with nothing left to name it.
		migrationBuilder.Sql("""DROP INDEX IF EXISTS "matching"."IX_DistinctDescriptions_Description_trgm";""");
		migrationBuilder.Sql("""DROP INDEX IF EXISTS "public"."IX_DistinctDescriptions_Description_trgm";""");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		// The column comes back empty. Its only historical writer is gone, so there is no
		// value to restore — reverting gives back the shape, not the data, which is all the
		// removed feature would have needed anyway before recomputing from scratch.
		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "ProcessedAt",
			schema: "matching",
			table: "DistinctDescriptions",
			type: "timestamptz",
			nullable: true);

		migrationBuilder.Sql("""
			CREATE INDEX IF NOT EXISTS "IX_DistinctDescriptions_Description_trgm"
			    ON "matching"."DistinctDescriptions" USING gin ("Description" gin_trgm_ops);
			""");
	}
}
