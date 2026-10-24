using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class IsolateCanonicalBusinessRevision : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                CREATE FUNCTION matching.refresh_canonical_business_revision() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    -- Protect all other columns, including future business fields.
                    IF TG_OP = 'UPDATE'
                       AND (NEW."Embedding" IS DISTINCT FROM OLD."Embedding"
                            OR NEW."EmbeddingModelVersion" IS DISTINCT FROM OLD."EmbeddingModelVersion")
                       AND (to_jsonb(NEW) - ARRAY['Embedding', 'EmbeddingModelVersion', 'NormalizationRevision'])
                           = (to_jsonb(OLD) - ARRAY['Embedding', 'EmbeddingModelVersion', 'NormalizationRevision']) THEN
                        NEW."NormalizationRevision" := OLD."NormalizationRevision";
                    ELSE
                        NEW."NormalizationRevision" := gen_random_uuid();
                    END IF;
                    RETURN NEW;
                END;
                $$;
                DROP TRIGGER canonical_normalization_revision ON matching."NormalizedDescriptions";
                CREATE TRIGGER canonical_normalization_revision
                BEFORE INSERT OR UPDATE ON matching."NormalizedDescriptions"
                FOR EACH ROW EXECUTE FUNCTION matching.refresh_canonical_business_revision();
                """);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                DROP TRIGGER canonical_normalization_revision ON matching."NormalizedDescriptions";
                CREATE TRIGGER canonical_normalization_revision
                BEFORE UPDATE ON matching."NormalizedDescriptions"
                FOR EACH ROW EXECUTE FUNCTION matching.refresh_normalization_revision();
                DROP FUNCTION matching.refresh_canonical_business_revision();
                """);
	}
}
