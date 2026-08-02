using Application.Models.Reports;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

/// <summary>
/// Postgres-only coverage for the duplicate-acceptance store (RECEIPTS-834).
///
/// The unit suite runs on EF InMemory, which enforces none of the three guarantees this feature
/// actually rests on: the canonical-order check constraint, the unique index filtered to active
/// rows, and the FK cascade that clears acceptances when a receipt is purged. Asserting them there
/// is asserting them where they cannot fail — a regression that dropped any of the three would ship
/// with a green suite. Only a real relational provider can reject the write.
///
/// The report reads across ALL receipts globally, so each test truncates first to stay isolated
/// from the rest of the collection.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AcceptedDuplicatePairTests(PostgresFixture fixture)
{
	#region Check constraint

	[Fact]
	public async Task CheckConstraint_RejectsAPairStoredOutOfCanonicalOrder()
	{
		// Arrange — the whole pairwise model assumes (A, B) is stored with A < B, because that is what
		// makes lookup by unordered pair a single equality. A row inserted the other way round would be
		// invisible to every suppression check.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		(Guid lower, Guid higher) = await SeedTwoReceiptsAsync(accountId, cardId);

		await using ApplicationDbContext context = fixture.CreateDbContext();

		// Act — deliberately reversed.
		context.AcceptedDuplicatePairs.Add(new AcceptedDuplicatePairEntity
		{
			Id = Guid.NewGuid(),
			ReceiptIdA = higher,
			ReceiptIdB = lower,
			AcceptedAt = DateTimeOffset.UtcNow,
		});

		// Assert
		Func<Task> act = () => context.SaveChangesAsync();
		DbUpdateException thrown = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
		thrown.InnerException.Should().BeOfType<PostgresException>()
			.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
	}

	[Fact]
	public async Task CheckConstraint_RejectsASelfPair()
	{
		// A receipt cannot be its own duplicate; A < B excludes A == B for free.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		(Guid receiptId, _) = await SeedTwoReceiptsAsync(accountId, cardId);

		await using ApplicationDbContext context = fixture.CreateDbContext();
		context.AcceptedDuplicatePairs.Add(new AcceptedDuplicatePairEntity
		{
			Id = Guid.NewGuid(),
			ReceiptIdA = receiptId,
			ReceiptIdB = receiptId,
			AcceptedAt = DateTimeOffset.UtcNow,
		});

		Func<Task> act = () => context.SaveChangesAsync();
		DbUpdateException thrown = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
		thrown.InnerException.Should().BeOfType<PostgresException>()
			.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
	}

	[Fact]
	public async Task AcceptDuplicateGroupAsync_AlwaysWritesPairsInCanonicalOrder()
	{
		// The service must canonicalize regardless of the order the client sent, or the check
		// constraint above would reject roughly half of all real requests.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		(Guid lower, Guid higher) = await SeedTwoReceiptsAsync(accountId, cardId);

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act — submit highest-first.
		int accepted = await service.AcceptDuplicateGroupAsync([higher, lower], CancellationToken.None);

		// Assert
		accepted.Should().Be(1);

		await using ApplicationDbContext context = fixture.CreateDbContext();
		AcceptedDuplicatePairEntity stored = await context.AcceptedDuplicatePairs.SingleAsync();
		stored.ReceiptIdA.Should().Be(lower);
		stored.ReceiptIdB.Should().Be(higher);
	}

	#endregion

	#region Filtered unique index

	[Fact]
	public async Task UniqueIndex_RejectsASecondActiveRowForTheSamePair()
	{
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		(Guid lower, Guid higher) = await SeedTwoReceiptsAsync(accountId, cardId);

		await using ApplicationDbContext context = fixture.CreateDbContext();
		context.AcceptedDuplicatePairs.Add(NewPair(lower, higher));
		await context.SaveChangesAsync();

		// Act — a second ACTIVE row for the same unordered pair.
		context.AcceptedDuplicatePairs.Add(NewPair(lower, higher));

		// Assert
		Func<Task> act = () => context.SaveChangesAsync();
		DbUpdateException thrown = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
		thrown.InnerException.Should().BeOfType<PostgresException>()
			.Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
	}

	[Fact]
	public async Task UniqueIndex_IsFilteredToActiveRows_SoATombstoneDoesNotBlockReAccepting()
	{
		// This is the behaviour the ON CONFLICT write path depends on. If the index were not partial,
		// re-accepting after an un-accept would collide with the tombstone and 23505 the request.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		(Guid lower, Guid higher) = await SeedTwoReceiptsAsync(accountId, cardId);

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act — accept, un-accept, accept again, all through the real write paths.
		await service.AcceptDuplicateGroupAsync([lower, higher], CancellationToken.None);
		await service.UnacceptDuplicateGroupAsync([lower, higher], CancellationToken.None);
		int reAccepted = await service.AcceptDuplicateGroupAsync([lower, higher], CancellationToken.None);

		// Assert — one active row, and the un-accept survives as history.
		reAccepted.Should().Be(1);

		await using ApplicationDbContext context = fixture.CreateDbContext();
		(await context.AcceptedDuplicatePairs.CountAsync()).Should().Be(1);
		(await context.AcceptedDuplicatePairs.IgnoreQueryFilters().CountAsync()).Should().Be(2);
	}

	[Fact]
	public async Task AcceptDuplicateGroupAsync_IsIdempotent_AgainstTheRealUniqueIndex()
	{
		// The ON CONFLICT DO NOTHING path must absorb a repeat accept rather than throwing 23505.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		(Guid lower, Guid higher) = await SeedTwoReceiptsAsync(accountId, cardId);

		ReportService service = new(new FixtureDbContextFactory(fixture));

		int first = await service.AcceptDuplicateGroupAsync([lower, higher], CancellationToken.None);
		int second = await service.AcceptDuplicateGroupAsync([lower, higher], CancellationToken.None);

		first.Should().Be(1);
		second.Should().Be(0, "the pair already existed, so the conflict was absorbed");

		await using ApplicationDbContext context = fixture.CreateDbContext();
		(await context.AcceptedDuplicatePairs.CountAsync()).Should().Be(1);
	}

	[Fact]
	public async Task AcceptDuplicateGroupAsync_PartiallyOverlappingGroups_DoNotCollide()
	{
		// Two groups sharing a pair — {A,B,C} then {B,C,D} both contain (B,C). Before ON CONFLICT the
		// second request died on the unique index and rolled back its unrelated pairs with it.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		List<Guid> ids = await SeedReceiptsAsync(accountId, cardId, count: 4);
		ids.Sort();
		(Guid a, Guid b, Guid c, Guid d) = (ids[0], ids[1], ids[2], ids[3]);

		ReportService service = new(new FixtureDbContextFactory(fixture));

		int firstGroup = await service.AcceptDuplicateGroupAsync([a, b, c], CancellationToken.None);
		int secondGroup = await service.AcceptDuplicateGroupAsync([b, c, d], CancellationToken.None);

		// Assert — (B,C) was absorbed; (B,D) and (C,D) still landed.
		firstGroup.Should().Be(3);
		secondGroup.Should().Be(2);

		await using ApplicationDbContext context = fixture.CreateDbContext();
		(await context.AcceptedDuplicatePairs.CountAsync()).Should().Be(5);
	}

	#endregion

	#region FK cascade on purge

	[Fact]
	public async Task PurgingAReceipt_CascadeDeletesItsAcceptedPairs()
	{
		// A permanently purged receipt can never be flagged again, so its acceptances are dead weight.
		// The cascade is what keeps them from outliving the receipt as orphan rows.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		List<Guid> ids = await SeedReceiptsAsync(accountId, cardId, count: 3);
		ids.Sort();

		ReportService service = new(new FixtureDbContextFactory(fixture));
		await service.AcceptDuplicateGroupAsync(ids, CancellationToken.None);

		await using (ApplicationDbContext seeded = fixture.CreateDbContext())
		{
			(await seeded.AcceptedDuplicatePairs.CountAsync()).Should().Be(3);
		}

		// Act — soft-delete one receipt, then purge.
		await using (ApplicationDbContext deleteContext = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = await deleteContext.Receipts.SingleAsync(r => r.Id == ids[2]);
			deleteContext.Receipts.Remove(receipt);
			await deleteContext.SaveChangesAsync();
		}

		await using (ApplicationDbContext purgeContext = fixture.CreateDbContext())
		{
			TrashService trash = new(purgeContext);
			await trash.PurgeAllDeletedAsync(CancellationToken.None);
		}

		// Assert — the two pairs touching the purged receipt are gone; the surviving pair remains.
		await using ApplicationDbContext context = fixture.CreateDbContext();
		List<AcceptedDuplicatePairEntity> remaining =
			await context.AcceptedDuplicatePairs.IgnoreQueryFilters().ToListAsync();

		remaining.Should().ContainSingle("only the pair between the two surviving receipts is left");
		new[] { remaining[0].ReceiptIdA, remaining[0].ReceiptIdB }
			.Should().BeEquivalentTo([ids[0], ids[1]]);
	}

	[Fact]
	public async Task PurgeAllDeletedAsync_RemovesUnacceptedTombstones()
	{
		// Un-accepting soft-deletes the row. Nothing surfaces these in the recycle bin, so without an
		// explicit purge step the tombstones would accumulate with no way to ever clear them.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		(Guid lower, Guid higher) = await SeedTwoReceiptsAsync(accountId, cardId);

		ReportService service = new(new FixtureDbContextFactory(fixture));
		await service.AcceptDuplicateGroupAsync([lower, higher], CancellationToken.None);
		await service.UnacceptDuplicateGroupAsync([lower, higher], CancellationToken.None);

		await using (ApplicationDbContext before = fixture.CreateDbContext())
		{
			(await before.AcceptedDuplicatePairs.IgnoreQueryFilters().CountAsync())
				.Should().Be(1, "the un-accepted row is still present as a tombstone");
		}

		// Act
		await using (ApplicationDbContext purgeContext = fixture.CreateDbContext())
		{
			TrashService trash = new(purgeContext);
			await trash.PurgeAllDeletedAsync(CancellationToken.None);
		}

		// Assert
		await using ApplicationDbContext after = fixture.CreateDbContext();
		(await after.AcceptedDuplicatePairs.IgnoreQueryFilters().CountAsync()).Should().Be(0);

		// The receipts themselves were never deleted, so they must survive the purge.
		(await after.Receipts.CountAsync()).Should().Be(2);
	}

	[Fact]
	public async Task SoftDeletingAReceipt_DoesNotCascadeToItsAcceptedPairs()
	{
		// The entity deliberately does NOT implement IOwnedBy<ReceiptEntity>. If it did, soft-deleting
		// a receipt would take the acceptance with it and restoring the receipt would resurrect a
		// warning the user had already dismissed.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		(Guid lower, Guid higher) = await SeedTwoReceiptsAsync(accountId, cardId);

		ReportService service = new(new FixtureDbContextFactory(fixture));
		await service.AcceptDuplicateGroupAsync([lower, higher], CancellationToken.None);

		// Act
		await using (ApplicationDbContext deleteContext = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = await deleteContext.Receipts.SingleAsync(r => r.Id == higher);
			deleteContext.Receipts.Remove(receipt);
			await deleteContext.SaveChangesAsync();
		}

		// Assert — the acceptance is untouched and still active.
		await using (ApplicationDbContext context = fixture.CreateDbContext())
		{
			AcceptedDuplicatePairEntity pair = await context.AcceptedDuplicatePairs.SingleAsync();
			pair.DeletedAt.Should().BeNull();
			pair.CascadeDeletedByParentId.Should().BeNull();
		}

		// And on restore, the group is still suppressed.
		await using (ApplicationDbContext restoreContext = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = await restoreContext.Receipts
				.IgnoreQueryFilters()
				.SingleAsync(r => r.Id == higher);
			receipt.DeletedAt = null;
			receipt.DeletedByUserId = null;
			await restoreContext.SaveChangesAsync();
		}

		DuplicateDetectionResult result = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: false, CancellationToken.None);
		result.Groups.Should().BeEmpty("the acceptance outlived the soft delete");
	}

	#endregion

	#region SQL translation

	[Fact]
	public async Task UnacceptDuplicateGroupAsync_ComponentExpansion_TranslatesToSql()
	{
		// The closure loop queries with `closure.Contains(A) || closure.Contains(B)`. InMemory would
		// happily client-evaluate that; a real provider has to translate it.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		List<Guid> ids = await SeedReceiptsAsync(accountId, cardId, count: 3);
		ids.Sort();

		ReportService service = new(new FixtureDbContextFactory(fixture));
		await service.AcceptDuplicateGroupAsync(ids, CancellationToken.None);

		// Soft-delete the third receipt so the client could only ever submit the first two.
		await using (ApplicationDbContext deleteContext = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = await deleteContext.Receipts.SingleAsync(r => r.Id == ids[2]);
			deleteContext.Receipts.Remove(receipt);
			await deleteContext.SaveChangesAsync();
		}

		// Act
		int removed = await service.UnacceptDuplicateGroupAsync([ids[0], ids[1]], CancellationToken.None);

		// Assert — all three pairs cleared, not just the submitted one.
		removed.Should().Be(3);

		await using ApplicationDbContext context = fixture.CreateDbContext();
		(await context.AcceptedDuplicatePairs.CountAsync()).Should().Be(0);
	}

	[Fact]
	public async Task GetDuplicatesAsync_ScopedAcceptedPairLookup_TranslatesToSql()
	{
		// The accepted-pair read is filtered by the clustered receipt IDs. Pin that it translates and
		// still suppresses correctly.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		(Guid lower, Guid higher) = await SeedTwoReceiptsAsync(accountId, cardId);

		ReportService service = new(new FixtureDbContextFactory(fixture));

		DuplicateDetectionResult before = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: false, CancellationToken.None);
		before.Groups.Should().ContainSingle();

		await service.AcceptDuplicateGroupAsync([lower, higher], CancellationToken.None);

		DuplicateDetectionResult after = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: false, CancellationToken.None);
		after.Groups.Should().BeEmpty();

		DuplicateDetectionResult withAccepted = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: true, CancellationToken.None);
		withAccepted.Groups.Should().ContainSingle();
		withAccepted.Groups[0].IsAccepted.Should().BeTrue();
	}

	[Fact]
	public async Task GetAcceptedDuplicatesAsync_HydratesFromSql()
	{
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		(Guid lower, Guid higher) = await SeedTwoReceiptsAsync(accountId, cardId);

		ReportService service = new(new FixtureDbContextFactory(fixture));
		await service.AcceptDuplicateGroupAsync([lower, higher], CancellationToken.None);

		AcceptedDuplicatesResult result = await service.GetAcceptedDuplicatesAsync(CancellationToken.None);

		result.GroupCount.Should().Be(1);
		result.Groups[0].Receipts.Select(r => r.ReceiptId).Should().BeEquivalentTo([lower, higher]);
		result.Groups[0].Receipts.Should().OnlyContain(r => r.TransactionTotal == 10.00m);
	}

	#endregion

	#region Helpers

	private static AcceptedDuplicatePairEntity NewPair(Guid a, Guid b) => new()
	{
		Id = Guid.NewGuid(),
		ReceiptIdA = a,
		ReceiptIdB = b,
		AcceptedAt = DateTimeOffset.UtcNow,
	};

	/// <summary>Seeds two same-day / same-location receipts and returns their IDs in canonical order.</summary>
	private async Task<(Guid Lower, Guid Higher)> SeedTwoReceiptsAsync(Guid accountId, Guid cardId)
	{
		List<Guid> ids = await SeedReceiptsAsync(accountId, cardId, count: 2);
		ids.Sort();
		return (ids[0], ids[1]);
	}

	/// <summary>
	/// Seeds <paramref name="count"/> receipts that all share a date and location (so they cluster as
	/// one duplicate group) each with a 10.00 transaction.
	/// </summary>
	private async Task<List<Guid>> SeedReceiptsAsync(Guid accountId, Guid cardId, int count)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();

		// The report and the acceptance table are both global, so isolate from the rest of the
		// collection. AcceptedDuplicatePairs is included explicitly because CASCADE from Receipts only
		// covers rows whose receipt is being truncated.
		await context.Database.ExecuteSqlRawAsync(
			"""TRUNCATE "AcceptedDuplicatePairs", "Transactions", "ReceiptItems", "Adjustments", "Receipts" RESTART IDENTITY CASCADE;""");

		AccountEntity account = AccountEntityGenerator.Generate();
		account.Id = accountId;
		CardEntity card = CardEntityGenerator.Generate();
		card.Id = cardId;
		card.AccountId = accountId;
		context.Accounts.Add(account);
		context.Cards.Add(card);
		await context.SaveChangesAsync();

		DateOnly date = new(2025, 3, 1);
		List<Guid> ids = [];
		for (int i = 0; i < count; i++)
		{
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			receipt.Date = date;
			receipt.Location = "Shared Location";
			receipt.TaxAmount = 0m;
			context.Receipts.Add(receipt);

			TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, accountId, cardId);
			transaction.Amount = 10.00m;
			transaction.Date = date;
			context.Transactions.Add(transaction);

			ids.Add(receipt.Id);
		}

		await context.SaveChangesAsync();
		return ids;
	}

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}

	#endregion
}
