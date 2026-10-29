using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddHistoricalMappingSweep : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "HistoricalMappingSweeps",
			schema: "taxonomy",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				RequestHash = table.Column<string>(type: "text", nullable: false),
				RestoreGeneration = table.Column<Guid>(type: "uuid", nullable: false),
				Cursor = table.Column<Guid>(type: "uuid", nullable: true),
				PendingOperationId = table.Column<Guid>(type: "uuid", nullable: true),
				Completed = table.Column<bool>(type: "boolean", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_HistoricalMappingSweeps", x => x.Id);
			});
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                LOCK TABLE taxonomy."HistoricalMappingSweeps" IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM taxonomy."HistoricalMappingSweeps") THEN
                        RAISE EXCEPTION 'Historical mapping sweep checkpoints are retained' USING ERRCODE = '23514';
                    END IF;
                END $$;
                """);
		migrationBuilder.DropTable(
			name: "HistoricalMappingSweeps",
			schema: "taxonomy");
	}
}
