using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddYnabOAuthConnection : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "YnabOAuthConnections",
			schema: "ynab",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				Status = table.Column<string>(type: "text", maxLength: 32, nullable: false),
				ProtectedTokens = table.Column<string>(type: "text", nullable: true),
				AccessTokenExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
				ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
				ConnectedAtUtc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				ConnectedByUserId = table.Column<string>(type: "text", maxLength: 450, nullable: true),
				RefreshedAtUtc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
				StatusChangedAtUtc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_YnabOAuthConnections", x => x.Id);
			});
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "YnabOAuthConnections",
			schema: "ynab");
	}
}
