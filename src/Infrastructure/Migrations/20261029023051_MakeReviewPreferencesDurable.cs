using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class MakeReviewPreferencesDurable : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropForeignKey(
			name: "FK_ReviewOperations_Attempts_AttemptId",
			schema: "category_assistance",
			table: "ReviewOperations");

		migrationBuilder.DropForeignKey(
			name: "FK_ReviewPreferences_Attempts_AttemptId",
			schema: "category_assistance",
			table: "ReviewPreferences");

		migrationBuilder.DropIndex(
			name: "IX_ReviewPreferences_AttemptId",
			schema: "category_assistance",
			table: "ReviewPreferences");

		migrationBuilder.DropIndex(
			name: "IX_ReviewOperations_AttemptId",
			schema: "category_assistance",
			table: "ReviewOperations");

		migrationBuilder.AddColumn<string>(
			name: "Description",
			schema: "category_assistance",
			table: "ReviewPreferences",
			type: "text",
			maxLength: 2000,
			nullable: true,
			collation: "C");

		migrationBuilder.AddColumn<string>(
			name: "ItemCodeKey",
			schema: "category_assistance",
			table: "ReviewPreferences",
			type: "text",
			maxLength: 200,
			nullable: true,
			collation: "C");

		migrationBuilder.AddColumn<string>(
			name: "Location",
			schema: "category_assistance",
			table: "ReviewPreferences",
			type: "text",
			maxLength: 1000,
			nullable: true,
			collation: "C");
		// Existing rows are guaranteed to have a linked attempt by the old FK.
		migrationBuilder.Sql("""
                UPDATE category_assistance."ReviewPreferences" p
                SET "Location" = a."Location", "Description" = a."Description", "ItemCodeKey" = a."ItemCodeKey"
                FROM category_assistance."Attempts" a WHERE a."Id" = p."AttemptId";
                ALTER TABLE category_assistance."ReviewPreferences"
                    ALTER COLUMN "Location" SET NOT NULL, ALTER COLUMN "Description" SET NOT NULL,
                    ALTER COLUMN "ItemCodeKey" SET NOT NULL;
                -- The hash bounds index entries; consumers must recheck the exact tuple.
                CREATE INDEX "IX_ReviewPreferences_SourceHash" ON category_assistance."ReviewPreferences"
                    ((md5("Location" || chr(31) || "Description" || chr(31) || "ItemCodeKey")));
                CREATE OR REPLACE FUNCTION organization.mark_preference_dirty() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        PERFORM organization.enqueue_source('normalized', OLD."Location", OLD."Description", nullif(OLD."ItemCodeKey", ''));
                    END IF;
                    IF TG_OP <> 'DELETE' THEN
                        PERFORM organization.enqueue_source('normalized', NEW."Location", NEW."Description", nullif(NEW."ItemCodeKey", ''));
                    END IF;
                    RETURN NULL;
                END $$;
                """);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM category_assistance."ReviewPreferences" p
                        LEFT JOIN category_assistance."Attempts" a ON a."Id" = p."AttemptId"
                        WHERE a."Id" IS NULL OR p."Location" IS DISTINCT FROM a."Location"
                           OR p."Description" IS DISTINCT FROM a."Description"
                           OR p."ItemCodeKey" IS DISTINCT FROM a."ItemCodeKey")
                       OR EXISTS (SELECT 1 FROM category_assistance."ReviewOperations" o
                        LEFT JOIN category_assistance."Attempts" a ON a."Id" = o."AttemptId"
                        WHERE a."Id" IS NULL) THEN
                        RAISE EXCEPTION 'Cannot downgrade: durable review records require their original attempts and source.';
                    END IF;
                END $$;
                CREATE OR REPLACE FUNCTION organization.mark_preference_dirty() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP <> 'INSERT' THEN PERFORM organization.enqueue_preference_source(OLD."AttemptId"); END IF;
                    IF TG_OP <> 'DELETE' THEN PERFORM organization.enqueue_preference_source(NEW."AttemptId"); END IF;
                    RETURN NULL;
                END $$;
                DROP INDEX category_assistance."IX_ReviewPreferences_SourceHash";
                """);
		migrationBuilder.DropColumn(
			name: "Description",
			schema: "category_assistance",
			table: "ReviewPreferences");

		migrationBuilder.DropColumn(
			name: "ItemCodeKey",
			schema: "category_assistance",
			table: "ReviewPreferences");

		migrationBuilder.DropColumn(
			name: "Location",
			schema: "category_assistance",
			table: "ReviewPreferences");

		migrationBuilder.CreateIndex(
			name: "IX_ReviewPreferences_AttemptId",
			schema: "category_assistance",
			table: "ReviewPreferences",
			column: "AttemptId");

		migrationBuilder.CreateIndex(
			name: "IX_ReviewOperations_AttemptId",
			schema: "category_assistance",
			table: "ReviewOperations",
			column: "AttemptId");

		migrationBuilder.AddForeignKey(
			name: "FK_ReviewOperations_Attempts_AttemptId",
			schema: "category_assistance",
			table: "ReviewOperations",
			column: "AttemptId",
			principalSchema: "category_assistance",
			principalTable: "Attempts",
			principalColumn: "Id",
			onDelete: ReferentialAction.Restrict);

		migrationBuilder.AddForeignKey(
			name: "FK_ReviewPreferences_Attempts_AttemptId",
			schema: "category_assistance",
			table: "ReviewPreferences",
			column: "AttemptId",
			principalSchema: "category_assistance",
			principalTable: "Attempts",
			principalColumn: "Id",
			onDelete: ReferentialAction.Restrict);
	}
}
