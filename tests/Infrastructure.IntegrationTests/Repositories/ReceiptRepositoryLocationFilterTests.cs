using Application.Models;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Repositories;

// Postgres-only coverage for RECEIPTS-841: ReceiptRepository.ApplyLocationFilter matches Location
// with a plain equality (`r.Location == location`), not a LIKE/ILIKE pattern, so drill-downs from
// the Spending by Location report land on exactly the rows the aggregate counted — that report
// groups on the raw Location column, which Postgres compares byte-for-byte (case-sensitive,
// whitespace-sensitive, and with no wildcard semantics for '%'/'_'). Only a real Postgres
// connection can prove that a plain `==` actually translates into a byte-for-byte comparison under
// the database's real collation, rather than some provider-level case-folding or trimming that
// would silently reintroduce the mismatch this filter exists to prevent.
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
	public async Task GetAllAsync_LocationFilter_IsCaseSensitive()
	{
		// Arrange — the Spending by Location report groups on the raw Location column, so "Walmart"
		// and "walmart" are two separate report rows with separate visit counts. A case-insensitive
		// drill-down filter would return the union of both and contradict the count the user clicked.
		await ResetTablesAsync();

		Guid titleCase = Guid.NewGuid();
		Guid lowerCase = Guid.NewGuid();

		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			AddReceipt(seed, titleCase, "Walmart");
			AddReceipt(seed, lowerCase, "walmart");
			await seed.SaveChangesAsync();
		}

		ReceiptRepository repository = new(new FixtureDbContextFactory(fixture));

		// Act
		List<ReceiptEntity> result = await repository.GetAllAsync(
			0, 50, SortParams.Default, accountId: null, cardId: null, q: null, location: "Walmart", CancellationToken.None);

		// Assert
		result.Should().ContainSingle();
		result[0].Id.Should().Be(titleCase);
	}

	[Fact]
	public async Task GetAllAsync_LocationFilter_TrailingWhitespace_IsSignificant()
	{
		// Arrange — "Target " (trailing space) is its own bucket in the Spending by Location report
		// because nothing in the write path trims Location. The drill-down filter must not trim
		// either: filtering for "Target " must land only on the padded receipt, and filtering for
		// "Target" must land only on the unpadded one. Regression guard for RECEIPTS-841 BUG-002.
		await ResetTablesAsync();

		Guid padded = Guid.NewGuid();
		Guid unpadded = Guid.NewGuid();

		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			AddReceipt(seed, padded, "Target ");
			AddReceipt(seed, unpadded, "Target");
			await seed.SaveChangesAsync();
		}

		ReceiptRepository repository = new(new FixtureDbContextFactory(fixture));

		// Act
		List<ReceiptEntity> paddedResult = await repository.GetAllAsync(
			0, 50, SortParams.Default, accountId: null, cardId: null, q: null, location: "Target ", CancellationToken.None);
		List<ReceiptEntity> unpaddedResult = await repository.GetAllAsync(
			0, 50, SortParams.Default, accountId: null, cardId: null, q: null, location: "Target", CancellationToken.None);

		// Assert
		paddedResult.Should().ContainSingle();
		paddedResult[0].Id.Should().Be(padded);

		unpaddedResult.Should().ContainSingle();
		unpaddedResult[0].Id.Should().Be(unpadded);
	}

	[Fact]
	public async Task GetAllAsync_LocationFilter_TreatsPercentSignLiterally_NotAsWildcard()
	{
		// Arrange — ApplyLocationFilter is a plain equality (`r.Location == location`), not a
		// LIKE/ILIKE pattern match, so '%' carries no special meaning: a location containing a
		// literal '%' must match only the receipt with that exact string, never a receipt whose
		// location happens to share the same prefix as if '%' were a wildcard.
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
		// Arrange — same rationale as GetAllAsync_LocationFilter_TreatsPercentSignLiterally_NotAsWildcard
		// above: ApplyLocationFilter is a plain equality, not a LIKE/ILIKE pattern match, so '_'
		// carries no special meaning and must be matched literally.
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
