using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class BindReceiptReviewIdentity : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<Guid>(
			name: "ReviewedTargetId",
			schema: "receipts",
			table: "ReceiptItems",
			type: "uuid",
			nullable: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(
			name: "ReviewedTargetId",
			schema: "receipts",
			table: "ReceiptItems");
	}
}
