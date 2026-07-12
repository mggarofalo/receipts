using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

// Postgres-only coverage for the RECEIPTS-801 fix (PR #605): the Transactions.AccountId FK is
// DeleteBehavior.Restrict, and AccountMergeService runs Phase 1 (repoint) and Phase 2 (delete
// orphaned source accounts) inside ONE transaction so a Phase-2 failure rolls back Phase 1. The
// InMemory unit suite cannot prove either — it enforces no FKs and no-ops BeginTransaction.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AccountMergeAtomicityTests(PostgresFixture fixture)
{
	[Fact]
	public async Task HardDeleteAccount_WithReferencingTransaction_IsRejectedByRestrictFk_AndTransactionSurvives()
	{
		// Arrange — the account under test is referenced ONLY by a transaction (its card lives on a
		// different account), so the delete can fail on exactly one constraint: the Transactions
		// AccountId Restrict FK. AccountEntity is not soft-deletable, so Remove() is a real hard DELETE.
		Guid accountUnderTestId = Guid.NewGuid();
		Guid transactionId = Guid.NewGuid();
		{
			await using ApplicationDbContext setup = fixture.CreateDbContext();

			AccountEntity accountUnderTest = AccountEntityGenerator.Generate();
			accountUnderTest.Id = accountUnderTestId;

			AccountEntity cardOwnerAccount = AccountEntityGenerator.Generate();
			CardEntity card = CardEntityGenerator.Generate();
			card.AccountId = cardOwnerAccount.Id;

			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();

			TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, accountUnderTestId, card.Id);
			transaction.Id = transactionId;

			setup.Accounts.AddRange(accountUnderTest, cardOwnerAccount);
			setup.Cards.Add(card);
			setup.Receipts.Add(receipt);
			await setup.SaveChangesAsync();

			setup.Transactions.Add(transaction);
			await setup.SaveChangesAsync();
		}

		// Act — hard-delete the referenced account.
		await using ApplicationDbContext deleteContext = fixture.CreateDbContext();
		AccountEntity toDelete = await deleteContext.Accounts.FirstAsync(a => a.Id == accountUnderTestId);
		deleteContext.Accounts.Remove(toDelete);

		Func<Task> act = async () => await deleteContext.SaveChangesAsync();

		// Assert — Postgres rejects the delete with FK violation SQLSTATE 23503 on the transaction FK.
		DbUpdateException dbEx = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
		PostgresException pg = dbEx.InnerException.Should().BeOfType<PostgresException>().Which;
		pg.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation); // "23503"
		pg.ConstraintName.Should().Be("FK_Transactions_Accounts_AccountId");

		// Assert — the transaction was NOT cascade-deleted; both rows still exist.
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await verify.Accounts.AnyAsync(a => a.Id == accountUnderTestId))
			.Should().BeTrue("the rejected delete must leave the account in place");
		(await verify.Transactions.IgnoreAutoIncludes().AnyAsync(t => t.Id == transactionId))
			.Should().BeTrue("Restrict must not cascade-destroy the referencing transaction");
	}

	[Fact]
	public async Task MergeCards_WhenPhase2Fails_RollsBackPhase1Repoint_NoHalfAppliedMerge()
	{
		// Arrange — two source accounts, each with one card and one transaction, plus a target.
		// Every transaction references a real receipt so the Transaction->Receipt FK is satisfied.
		AccountEntity target = AccountEntityGenerator.Generate();
		AccountEntity source1 = AccountEntityGenerator.Generate();
		AccountEntity source2 = AccountEntityGenerator.Generate();

		CardEntity card1 = CardEntityGenerator.Generate();
		card1.AccountId = source1.Id;
		CardEntity card2 = CardEntityGenerator.Generate();
		card2.AccountId = source2.Id;

		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();

		TransactionEntity tx1 = TransactionEntityGenerator.Generate(receipt.Id, source1.Id, card1.Id);
		TransactionEntity tx2 = TransactionEntityGenerator.Generate(receipt.Id, source2.Id, card2.Id);

		{
			await using ApplicationDbContext setup = fixture.CreateDbContext();
			setup.Accounts.AddRange(target, source1, source2);
			setup.Cards.AddRange(card1, card2);
			setup.Receipts.Add(receipt);
			await setup.SaveChangesAsync();

			setup.Transactions.AddRange(tx1, tx2);
			await setup.SaveChangesAsync();
		}

		// Force Phase 2 to fail without touching production code: a context that throws on its SECOND
		// SaveChangesAsync. LoadStateAsync only reads (0 saves); the merge context saves once in Phase 1
		// (repoint + audit) and again in Phase 2 (delete orphaned accounts) — so the throw lands exactly
		// on the Phase-2 delete, inside the service's own transaction.
		FailOnSecondSaveContextFactory factory = new(fixture);
		AccountMergeService service = new(factory, new NullCurrentUserAccessor());

		// Act
		Func<Task> act = async () => await service.MergeCardsAsync(
			target.Id, [card1.Id, card2.Id], null, CancellationToken.None);

		// Assert — the injected Phase-2 failure surfaced.
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*RECEIPTS-801*");

		// Assert — the whole merge rolled back: nothing was half-applied.
		await using ApplicationDbContext verify = fixture.CreateDbContext();

		CardEntity verifiedCard1 = await verify.Cards.AsNoTracking().FirstAsync(c => c.Id == card1.Id);
		CardEntity verifiedCard2 = await verify.Cards.AsNoTracking().FirstAsync(c => c.Id == card2.Id);
		verifiedCard1.AccountId.Should().Be(source1.Id, "Phase-1 card repoint must roll back");
		verifiedCard2.AccountId.Should().Be(source2.Id, "Phase-1 card repoint must roll back");

		List<TransactionEntity> verifiedTxns = await verify.Transactions
			.IgnoreAutoIncludes().AsNoTracking()
			.Where(t => t.Id == tx1.Id || t.Id == tx2.Id)
			.ToListAsync();
		verifiedTxns.Single(t => t.Id == tx1.Id).AccountId.Should().Be(source1.Id, "Phase-1 transaction repoint must roll back");
		verifiedTxns.Single(t => t.Id == tx2.Id).AccountId.Should().Be(source2.Id, "Phase-1 transaction repoint must roll back");

		List<Guid> seededAccountIds = [target.Id, source1.Id, source2.Id];
		List<Guid> survivingAccounts = await verify.Accounts.AsNoTracking()
			.Where(a => seededAccountIds.Contains(a.Id))
			.Select(a => a.Id)
			.ToListAsync();
		survivingAccounts.Should().BeEquivalentTo(seededAccountIds, "the Phase-2 account delete must roll back too");

		bool anyMergeAudit = await verify.AuditLogs.AsNoTracking()
			.AnyAsync(a => a.Action == Infrastructure.Entities.Audit.AuditAction.Merge
				&& (a.EntityId == source1.Id.ToString()
					|| a.EntityId == source2.Id.ToString()
					|| a.EntityId == target.Id.ToString()));
		anyMergeAudit.Should().BeFalse("Phase-1 merge audit rows must roll back with the transaction");
	}

	// A real ApplicationDbContext against the fixture's data source that throws on its second
	// SaveChangesAsync — a tiny, test-only seam to drive AccountMergeService's Phase-2 failure path.
	private sealed class FailOnSecondSaveContext(DbContextOptions<ApplicationDbContext> options)
		: ApplicationDbContext(options)
	{
		private int _saveCount;

		public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
		{
			_saveCount++;
			return _saveCount == 2
				? throw new InvalidOperationException("Injected Phase-2 failure (RECEIPTS-801 merge-rollback test).")
				: base.SaveChangesAsync(cancellationToken);
		}
	}

	private sealed class FailOnSecondSaveContextFactory(PostgresFixture fixture)
		: IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new FailOnSecondSaveContext(fixture.CreateOptions());
	}
}
