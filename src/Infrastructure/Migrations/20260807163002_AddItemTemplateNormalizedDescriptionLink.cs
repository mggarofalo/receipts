using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// Cross-links item templates to the normalized-description registry (RECEIPTS-881).
/// </summary>
/// <remarks>
/// The column is added empty and stays empty. Existing templates link lazily, on their next
/// create/update — deliberately, not as a shortcut:
///
///  1. A migration cannot generate embeddings. The ONNX embedding service lives behind DI and
///     is not available to `dotnet ef database update`, so anything backfilled here would get
///     a NULL vector. Such a row exists in the registry but is invisible to every ANN search,
///     so the same item typed freehand on a later receipt would never match it — the entry
///     would look linked while doing none of the work the link exists for.
///  2. It would invent Active canonical entries for templates nobody has ever used, including
///     the four seeded demo rows, on every fresh install. Those would show up in the registry
///     with 0 linked items and "Last Seen: Never" (RECEIPTS-880) — dead weight indistinguishable
///     from the dead weight that column exists to help an admin find.
///
/// Linking through the service instead gets a real embedding and only creates entries for
/// templates somebody is actually touching.
///
/// The FK is ON DELETE SET NULL, mirroring ReceiptItem's FK to the same table. It is a
/// backstop only: MergeAsync re-points templates explicitly, because letting the database null
/// the column would silently unlink every template pointing at a merged-away row and quietly
/// put its items back through the resolver, with nothing raised anywhere.
/// </remarks>
public partial class AddItemTemplateNormalizedDescriptionLink : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<Guid>(
			name: "NormalizedDescriptionId",
			schema: "library",
			table: "ItemTemplates",
			type: "uuid",
			nullable: true);

		// EF scaffolds four UpdateData calls here setting the seeded templates' new column to
		// null. Dropped: a freshly added nullable column is already null on every row, so they
		// are no-ops that only make the migration harder to read.

		migrationBuilder.CreateIndex(
			name: "IX_ItemTemplates_NormalizedDescriptionId",
			schema: "library",
			table: "ItemTemplates",
			column: "NormalizedDescriptionId");

		migrationBuilder.AddForeignKey(
			name: "FK_ItemTemplates_NormalizedDescriptions_NormalizedDescriptionId",
			schema: "library",
			table: "ItemTemplates",
			column: "NormalizedDescriptionId",
			principalSchema: "matching",
			principalTable: "NormalizedDescriptions",
			principalColumn: "Id",
			onDelete: ReferentialAction.SetNull);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		// Dropping the column loses the links. Nothing else depends on them — an unlinked
		// template behaves exactly as it did before this issue, sending its items through the
		// resolver — so there is no data to preserve on the way down.
		migrationBuilder.DropForeignKey(
			name: "FK_ItemTemplates_NormalizedDescriptions_NormalizedDescriptionId",
			schema: "library",
			table: "ItemTemplates");

		migrationBuilder.DropIndex(
			name: "IX_ItemTemplates_NormalizedDescriptionId",
			schema: "library",
			table: "ItemTemplates");

		migrationBuilder.DropColumn(
			name: "NormalizedDescriptionId",
			schema: "library",
			table: "ItemTemplates");
	}
}
