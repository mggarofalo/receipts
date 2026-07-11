using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests;

// RECEIPTS-788: CleanUpInvalidCategories' seed INSERT must be replayable — re-running Up after
// a Down (which intentionally keeps the "Uncategorized" row) must no-op instead of throwing a
// unique violation. This exercises the migration's exact guarded INSERT against real Postgres
// twice, mirroring a replay. The fresh-DB insert path is already covered by the fixture applying
// the (modified) migration at startup.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class MigrationReplayTests(PostgresFixture fixture)
{
	[Fact]
	public async Task CleanUpInvalidCategories_SeedInsert_IsIdempotentOnReplay()
	{
		// The exact SQL the migration now runs. Unqualified "Categories" resolves via the
		// fixture search_path to the table's current schema (library).
		const string seedSql =
			"INSERT INTO \"Categories\" (\"Id\", \"Description\", \"Name\")" +
			" VALUES ('f0e7a123-9b56-4d3a-8c1e-2a5b7d9f4e6c', 'Default category for items without a valid category', 'Uncategorized')" +
			" ON CONFLICT DO NOTHING;";

		await using ApplicationDbContext context = fixture.CreateDbContext();

		// Run it twice — the second run simulates replay after a Down that kept the row. A bare
		// INSERT would throw a unique violation here; ON CONFLICT DO NOTHING must no-op.
		Func<Task> act = async () =>
		{
			await context.Database.ExecuteSqlRawAsync(seedSql);
			await context.Database.ExecuteSqlRawAsync(seedSql);
		};

		await act.Should().NotThrowAsync();

		// Exactly one "Uncategorized" category remains — no duplicate, no error.
		int count = await context.Categories
			.IgnoreQueryFilters()
			.CountAsync(c => c.Name == "Uncategorized");
		count.Should().Be(1);
	}
}
