using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class DropTransactionAccountId : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		// The check and drop share the migration transaction. Lock both writers so
		// reassignment cannot introduce a divergent row after the precondition.
		migrationBuilder.Sql("""
                LOCK TABLE receipts."Transactions", library."Cards" IN ACCESS EXCLUSIVE MODE;
                DO $migration$
                DECLARE divergent_count bigint;
                BEGIN
                    SELECT COUNT(*) INTO divergent_count
                    FROM receipts."Transactions" AS payment
                    JOIN library."Cards" AS card ON card."Id" = payment."CardId"
                    WHERE payment."AccountId" IS DISTINCT FROM card."AccountId";
                    RAISE NOTICE 'RECEIPTS-852 account ownership precondition: % divergent transaction(s), including trash.', divergent_count;
                    IF divergent_count <> 0 THEN
                        RAISE EXCEPTION 'RECEIPTS-852 cannot drop Transaction.AccountId: % divergent transaction(s), including trash. Reconcile account ownership before retrying.', divergent_count;
                    END IF;
                END $migration$;
                """);

		migrationBuilder.DropForeignKey(
			name: "FK_Transactions_Accounts_AccountId",
			schema: "receipts",
			table: "Transactions");

		migrationBuilder.DropIndex(
			name: "IX_Transactions_AccountId",
			schema: "receipts",
			table: "Transactions");

		migrationBuilder.DropColumn(
			name: "AccountId",
			schema: "receipts",
			table: "Transactions");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<Guid>(
			name: "AccountId",
			schema: "receipts",
			table: "Transactions",
			type: "uuid",
			nullable: true);

		migrationBuilder.Sql("""
                LOCK TABLE library."Cards" IN ACCESS EXCLUSIVE MODE;
                UPDATE receipts."Transactions" AS payment
                SET "AccountId" = card."AccountId"
                FROM library."Cards" AS card
                WHERE card."Id" = payment."CardId";
                """);

		migrationBuilder.AlterColumn<Guid>(
			name: "AccountId",
			schema: "receipts",
			table: "Transactions",
			type: "uuid",
			nullable: false,
			oldClrType: typeof(Guid),
			oldType: "uuid",
			oldNullable: true);

		migrationBuilder.CreateIndex(
			name: "IX_Transactions_AccountId",
			schema: "receipts",
			table: "Transactions",
			column: "AccountId");

		migrationBuilder.AddForeignKey(
			name: "FK_Transactions_Accounts_AccountId",
			schema: "receipts",
			table: "Transactions",
			column: "AccountId",
			principalSchema: "library",
			principalTable: "Accounts",
			principalColumn: "Id",
			onDelete: ReferentialAction.Restrict);
	}
}
