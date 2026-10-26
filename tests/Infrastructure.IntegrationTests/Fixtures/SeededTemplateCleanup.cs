using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Fixtures;

internal static class SeededTemplateCleanup
{
	// These four disposable suggestions are inserted by SeedDevelopmentData.
	// A fresh migrated database has no purchases or immutable template evidence.
	private static readonly Guid[] SeedIds =
	[
		Guid.Parse("33255f68-44df-4813-ad55-92260303c0ce"),
		Guid.Parse("3d11bbb7-ee69-4701-a45c-58bfc6458158"),
		Guid.Parse("a2de7840-ef72-42d5-b90f-9c25eb63f502"),
		Guid.Parse("cb05ed31-92a0-4c3d-bdbe-b9bd05183f38"),
	];

	public static async Task RemoveFromFreshDatabaseAsync(PostgresFixture fixture)
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		(await db.ItemTemplates.Where(row => SeedIds.Contains(row.Id)).ExecuteDeleteAsync())
			.Should().Be(SeedIds.Length, "the test starts with exactly the four migration suggestions");
	}
}
