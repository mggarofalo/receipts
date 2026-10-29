using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddCategoryAssistanceSecondDispatch : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<Guid>(
			name: "DispatchToken",
			schema: "category_assistance",
			table: "Attempts",
			type: "uuid",
			nullable: true);

		// Preserve identity of a dispatch already in flight at migration time.
		migrationBuilder.Sql("UPDATE category_assistance.\"Attempts\" SET \"DispatchToken\" = \"ClaimToken\" WHERE \"State\" = 'dispatching'");

		migrationBuilder.AddColumn<long>(
			name: "FirstStageInputTokens",
			schema: "category_assistance",
			table: "Attempts",
			type: "bigint",
			nullable: true);

		migrationBuilder.AddColumn<long>(
			name: "FirstStageOutputTokens",
			schema: "category_assistance",
			table: "Attempts",
			type: "bigint",
			nullable: true);

		migrationBuilder.AddColumn<DateOnly>(
			name: "SecondDebitUtcDay",
			schema: "category_assistance",
			table: "Attempts",
			type: "date",
			nullable: true);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "SecondSendStartedAt",
			schema: "category_assistance",
			table: "Attempts",
			type: "timestamptz",
			nullable: true);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "SecondStageDecidedAt",
			schema: "category_assistance",
			table: "Attempts",
			type: "timestamptz",
			nullable: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(
			name: "DispatchToken",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.DropColumn(
			name: "FirstStageInputTokens",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.DropColumn(
			name: "FirstStageOutputTokens",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.DropColumn(
			name: "SecondDebitUtcDay",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.DropColumn(
			name: "SecondSendStartedAt",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.DropColumn(
			name: "SecondStageDecidedAt",
			schema: "category_assistance",
			table: "Attempts");
	}
}
