using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>Adds refresh-family identity without invalidating legacy tokens.</summary>
public partial class AddRefreshSessionId : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder) =>
		migrationBuilder.AddColumn<Guid>(
			name: "RefreshSessionId",
			schema: "identity",
			table: "AspNetUsers",
			type: "uuid",
			nullable: true);

	protected override void Down(MigrationBuilder migrationBuilder) =>
		migrationBuilder.DropColumn(
			name: "RefreshSessionId",
			schema: "identity",
			table: "AspNetUsers");
}
