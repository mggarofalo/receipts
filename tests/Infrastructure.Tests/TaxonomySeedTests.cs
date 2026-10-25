using FluentAssertions;
using Infrastructure.Configurations;
using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests;

public class TaxonomySeedTests
{
	[Fact]
	public async Task InMemoryContextInitializesWithSealedEmptyGenesis()
	{
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
		await using ApplicationDbContext db = new(options);

		await db.Database.EnsureCreatedAsync();

		TaxonomyRevisionEntity genesis = await db.TaxonomyRevisions.SingleAsync();
		genesis.Id.Should().Be(TaxonomyConfiguration.GenesisId);
		db.Entry(genesis).Property<long>("WriteTransaction").CurrentValue.Should().Be(0);
		(await db.TaxonomyHeads.SingleAsync()).RevisionId.Should().Be(genesis.Id);
		(await db.TaxonomyNodes.AnyAsync()).Should().BeFalse();
	}
}
