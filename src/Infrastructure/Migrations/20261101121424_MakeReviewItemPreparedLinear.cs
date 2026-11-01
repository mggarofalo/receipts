using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class MakeReviewItemPreparedLinear : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		// The evidence guard asks once per written purchase. Searching a comma-joined setting of every
		// prepared id made one save quadratic in its size; a restore at three times the owner's data spent
		// over 30 s in it. The application now puts the ids in a session temp table with a primary key and
		// sets the transaction-local flag to 'on', so each answer is one index probe. Any other flag value,
		// including the old joined string, trusts nothing: the guard only ever downgrades evidence.
		migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION matching.review_item_prepared(item_id uuid) RETURNS boolean
                LANGUAGE plpgsql STABLE AS $$
                BEGIN
                  IF current_setting('receipts.review_prepared', true) IS DISTINCT FROM 'on' THEN
                    RETURN false;
                  END IF;
                  RETURN EXISTS (SELECT 1 FROM pg_temp.review_prepared WHERE "Id" = item_id);
                END;
                $$;
                """);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION matching.review_item_prepared(item_id uuid) RETURNS boolean
                LANGUAGE sql STABLE AS $$
                  SELECT position(',' || item_id::text || ',' in coalesce(current_setting('receipts.review_prepared', true), '')) > 0;
                $$;
                """);
	}
}
