using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task ReceiptSuggestions_FindTheFreshNeighbourWhileMostOfTheCatalogIsStale()
	{
		await ResetAsync();
		var target = await StaleNeighbourhood.SeedAsync(fixture, "Chicken breast");
		var service = new ReceiptNormalizationCandidatesService(StaleNeighbourhood.IndexPlanFactory(fixture), TimeProvider.System,
			EmbeddingReturning("KS ORG CHKN BRST", StaleNeighbourhood.Query));

		var candidates = await service.GetCandidatesAsync(Actor, new("Synthetic market", "KS ORG CHKN BRST", null), CancellationToken.None);

		candidates.Should().Contain(candidate => candidate.Id == target.Id,
			"stale rows nearer the query must not starve the LIMIT 20 neighbour search");
	}

	[Fact]
	public async Task PinnedPgvector_SupportsTheIterativeScan()
	{
		await using var db = fixture.CreateDbContext();
		var version = await db.Database.SqlQueryRaw<string>("SELECT extversion AS \"Value\" FROM pg_extension WHERE extname = 'vector'").SingleAsync();

		Version.Parse(version).Should().BeGreaterThanOrEqualTo(new Version(0, 8), "NearestNeighbourSearch sets hnsw.iterative_scan");
	}
}
