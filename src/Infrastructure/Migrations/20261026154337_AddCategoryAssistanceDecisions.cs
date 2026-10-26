using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddCategoryAssistanceDecisions : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropCheckConstraint(
			name: "CK_CategoryAssistance_AttemptState",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.AddColumn<string>(
			name: "DecisionActor",
			schema: "category_assistance",
			table: "Attempts",
			type: "text",
			maxLength: 450,
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "DecisionJson",
			schema: "category_assistance",
			table: "Attempts",
			type: "text",
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "DecisionOperationId",
			schema: "category_assistance",
			table: "Attempts",
			type: "uuid",
			nullable: true);

		migrationBuilder.CreateIndex(
			name: "IX_Attempts_DecisionActor_DecisionOperationId",
			schema: "category_assistance",
			table: "Attempts",
			columns: new[] { "DecisionActor", "DecisionOperationId" },
			unique: true);

		migrationBuilder.AddCheckConstraint(
			name: "CK_CategoryAssistance_AttemptState",
			schema: "category_assistance",
			table: "Attempts",
			sql: "\"State\" IN ('queued','dispatching','suggested','abstained','failed','stale','indeterminate','accepted','dismissed')");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM category_assistance."Attempts"
                        WHERE "DecisionActor" IS NOT NULL OR "DecisionJson" IS NOT NULL
                            OR "DecisionOperationId" IS NOT NULL OR "State" IN ('accepted','dismissed')) THEN
                        RAISE EXCEPTION 'Cannot downgrade: category assistance decision history must be retained.';
                    END IF;
                END $$;
                """);

		migrationBuilder.DropIndex(
			name: "IX_Attempts_DecisionActor_DecisionOperationId",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.DropCheckConstraint(
			name: "CK_CategoryAssistance_AttemptState",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.DropColumn(
			name: "DecisionActor",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.DropColumn(
			name: "DecisionJson",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.DropColumn(
			name: "DecisionOperationId",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.AddCheckConstraint(
			name: "CK_CategoryAssistance_AttemptState",
			schema: "category_assistance",
			table: "Attempts",
			sql: "\"State\" IN ('queued','dispatching','suggested','abstained','failed','stale','indeterminate')");
	}
}
