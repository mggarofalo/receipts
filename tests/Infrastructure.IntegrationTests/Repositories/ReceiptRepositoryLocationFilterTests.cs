using Application.Models;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Repositories;

// Postgres-only coverage for RECEIPTS-841: ReceiptRepository.ApplyLocationFilter uses
// EF.Functions.ILike with an escaped, wildcard-free pattern. The InMemory provider used by the
// unit test suite does not implement EF.Functions.ILike at all, so it cannot prove this filter's
// SQL translation, its case-insensitivity, or that '%'/'_' in the location are escaped rather than
// treated as LIKE wildcards. Only a real Postgres connection can catch a regression here.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ReceiptRepositoryLocationFilterTests(PostgresFixture fixture)
{
	[Fact]
	public async Task GetAllAsync_LocationFilter_ReturnsOnlyExactMatch_NotLongerPrefixMatch()
	{
		// Arrange — drill-downs from the Spending by Location report must land on exactly the rows
		// the aggregate counted. "Target" must not also pull in "Target Optical".
		await ResetTablesAsync();

		Guid target = Guid.NewGuid();
		Guid targetOptical = Guid.NewGuid();

		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			AddReceipt(seed, target, "Target");
			AddReceipt(seed, targetOptical, "Target Optical");
			await seed.SaveChangesAsync();
		}

		ReceiptRepository repository = new(new FixtureDbContextFactory(fixture));

		// Act
		List<ReceiptEntity> result = await repository.GetAllAsync(
			0, 50, SortParams.Default, accountId: null, cardId: null, q: null, location: "Target", CancellationToken.None);

		// Assert
		result.Should().ContainSingle();
		result[0].Id.Should().Be(target);
	}

	[Fact]
	public async Task GetAllAsync_LocationFilter_IsCaseInsensitive()
	{
		// Arrange
		await ResetTablesAsync();

		Guid receiptId = Guid.NewGuid();

		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			AddReceipt(seed, receiptId, "Target");
			await seed.SaveChangesAsync();
		}

		ReceiptRepository repository = new(new FixtureDbContextFactory(fixture));

		// Act
		List<ReceiptEntity> lower = await repository.GetAllAsync(
			0, 50, SortParams.Default, accountId: null, cardId: null, q: null, location: "target", CancellationToken.None);
		List<ReceiptEntity> upper = await repository.GetAllAsync(
			0, 50, SortParams.Default, accountId: null, cardId: null, q: null, location: "TARGET", CancellationToken.None);

		// Assert
		lower.Should().ContainSingle().Which.Id.Should().Be(receiptId);
		upper.Should().ContainSingle().Which.Id.Should().Be(receiptId);
	}

	[Fact]
	public async Task GetAllAsync_LocationFilter_TreatsPercentSignLiterally_NotAsWildcard()
	{
		// Arrange — if '%' were left unescaped, ILIKE '50% Off' would also match "50XYZ Off"
		// (% means "any sequence, including none" in LIKE/ILIKE).
		//
		// KNOWN PRODUCTION BUG (found while writing this test, RECEIPTS-841): this currently FAILS.
		// EF.Functions.ILike(matchExpression, pattern) — the 2-argument overload used by
		// ReceiptRepository.ApplyLocationFilter/ApplySearchFilter — makes Npgsql's EF Core provider
		// emit `ILIKE <pattern> ESCAPE ''`. An empty ESCAPE string disables backslash-escape
		// processing entirely, so EscapeLikePattern's `\%`/`\_` never neutralize anything: '%' and
		// '_' in the search text still act as SQL wildcards. Confirmed via EF SQL logging — the
		// generated command for this test is:
		//   ... WHERE r."Location" ILIKE '50\% Off' ESCAPE ''
		// which (because ESCAPE '' disables escaping) requires a LITERAL backslash in the data to
		// match, so it matches neither seeded row. Fix: use the 3-argument overload
		// EF.Functions.ILike(matchExpression, pattern, "\\") to make Npgsql emit `ESCAPE '\'`.
		await ResetTablesAsync();

		Guid literalMatch = Guid.NewGuid();
		Guid wouldMatchIfWildcard = Guid.NewGuid();

		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			AddReceipt(seed, literalMatch, "50% Off");
			AddReceipt(seed, wouldMatchIfWildcard, "50XYZ Off");
			await seed.SaveChangesAsync();
		}

		ReceiptRepository repository = new(new FixtureDbContextFactory(fixture));

		// Act
		List<ReceiptEntity> result = await repository.GetAllAsync(
			0, 50, SortParams.Default, accountId: null, cardId: null, q: null, location: "50% Off", CancellationToken.None);

		// Assert
		result.Should().ContainSingle();
		result[0].Id.Should().Be(literalMatch);
	}

	[Fact]
	public async Task GetAllAsync_LocationFilter_TreatsUnderscoreLiterally_NotAsWildcard()
	{
		// Arrange — if '_' were left unescaped, ILIKE 'Aisle_5' would also match "AisleX5"
		// (_ means "exactly one arbitrary character" in LIKE/ILIKE).
		//
		// KNOWN PRODUCTION BUG — see the identical note on
		// GetAllAsync_LocationFilter_TreatsPercentSignLiterally_NotAsWildcard above. This currently
		// FAILS for the same reason (EF.Functions.ILike's 2-arg overload emits `ESCAPE ''`).
		await ResetTablesAsync();

		Guid literalMatch = Guid.NewGuid();
		Guid wouldMatchIfWildcard = Guid.NewGuid();

		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			AddReceipt(seed, literalMatch, "Aisle_5");
			AddReceipt(seed, wouldMatchIfWildcard, "AisleX5");
			await seed.SaveChangesAsync();
		}

		ReceiptRepository repository = new(new FixtureDbContextFactory(fixture));

		// Act
		List<ReceiptEntity> result = await repository.GetAllAsync(
			0, 50, SortParams.Default, accountId: null, cardId: null, q: null, location: "Aisle_5", CancellationToken.None);

		// Assert
		result.Should().ContainSingle();
		result[0].Id.Should().Be(literalMatch);
	}

	[Fact]
	public async Task GetCountAsync_LocationFilter_CountsOnlyExactMatches()
	{
		// Arrange
		await ResetTablesAsync();

		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			AddReceipt(seed, Guid.NewGuid(), "Target");
			AddReceipt(seed, Guid.NewGuid(), "Target");
			AddReceipt(seed, Guid.NewGuid(), "Target Optical");
			await seed.SaveChangesAsync();
		}

		ReceiptRepository repository = new(new FixtureDbContextFactory(fixture));

		// Act
		int count = await repository.GetCountAsync(accountId: null, cardId: null, q: null, location: "Target", CancellationToken.None);

		// Assert
		count.Should().Be(2);
	}

	[Fact]
	public async Task GetAllAsync_LocationFilter_CombinesWithSearchQuery_AsAnd()
	{
		// Arrange — q (substring match) alone would return both "Target" and "Target Store", but
		// location (exact match) narrows that down to just "Target": the two filters AND together
		// rather than the location filter simply replacing q.
		await ResetTablesAsync();

		Guid exactTarget = Guid.NewGuid();

		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			AddReceipt(seed, exactTarget, "Target");
			AddReceipt(seed, Guid.NewGuid(), "Target Store");
			AddReceipt(seed, Guid.NewGuid(), "Walmart");
			await seed.SaveChangesAsync();
		}

		ReceiptRepository repository = new(new FixtureDbContextFactory(fixture));

		// Act — q alone (sanity check) matches both Target locations.
		List<ReceiptEntity> qOnly = await repository.GetAllAsync(
			0, 50, SortParams.Default, accountId: null, cardId: null, q: "Target", location: null, CancellationToken.None);

		// q AND location together must narrow to the exact match only.
		List<ReceiptEntity> qAndLocation = await repository.GetAllAsync(
			0, 50, SortParams.Default, accountId: null, cardId: null, q: "Target", location: "Target", CancellationToken.None);

		// Assert
		qOnly.Should().HaveCount(2);
		qAndLocation.Should().ContainSingle();
		qAndLocation[0].Id.Should().Be(exactTarget);
	}

	private static void AddReceipt(ApplicationDbContext context, Guid receiptId, string location)
	{
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.Id = receiptId;
		receipt.Location = location;
		context.Receipts.Add(receipt);
	}

	private async Task ResetTablesAsync()
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		await context.Database.ExecuteSqlRawAsync(
			"""TRUNCATE "Transactions", "ReceiptItems", "Adjustments", "Receipts" RESTART IDENTITY CASCADE;""");
	}

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
