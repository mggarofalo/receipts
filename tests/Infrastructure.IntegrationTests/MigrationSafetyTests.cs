using System.Data.Common;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class MigrationSafetyTests(PostgresFixture fixture)
{
	// RECEIPTS-574: the PromoteTransactionCardIdNotNull migration includes a pre-check
	// guard that refuses to apply if any Transactions.CardId IS NULL rows remain.
	// This test exercises that guard by rolling the DB back to the prior (nullable)
	// state, inserting a row with NULL CardId, then attempting to reapply — and
	// asserts that the migration throws with the specific guard error message.
	[Fact]
	public async Task PromoteTransactionCardIdNotNull_WithNullCardIdRow_AbortsWithGuardError()
	{
		const string priorMigration = "20260419022200_AddCardIdToTransactions";

		await using ApplicationDbContext context = fixture.CreateDbContext();
		IMigrator migrator = context.GetInfrastructure().GetRequiredService<IMigrator>();

		// Roll back to the pre-574 state where CardId is nullable.
		await migrator.MigrateAsync(priorMigration);

		// Insert a minimal Transaction row with NULL CardId. Use raw SQL to bypass EF's
		// non-null CardId constraint on the current entity model.
		Guid txId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();
		await context.Database.ExecuteSqlRawAsync(
			"""
			INSERT INTO "Accounts" ("Id", "Name", "IsActive") VALUES ({0}, 'Guard Test', true);
			INSERT INTO "Receipts" ("Id", "Location", "Date", "TaxAmount", "TaxAmountCurrency")
				VALUES ({1}, 'Guard Test', CURRENT_DATE, 0, 'USD');
			INSERT INTO "Transactions" ("Id", "ReceiptId", "AccountId", "CardId", "Amount", "AmountCurrency", "Date")
				VALUES ({2}, {1}, {0}, NULL, 1, 'USD', CURRENT_DATE);
			""",
			accountId, receiptId, txId);

		// Attempt to reapply all pending migrations (which includes 574). The guard
		// should raise an exception; the Npgsql provider surfaces it as PostgresException.
		Func<Task> act = () => migrator.MigrateAsync();

		PostgresException ex = (await act.Should().ThrowAsync<PostgresException>())
			.Where(e => e.MessageText.Contains("RECEIPTS-574"))
			.Subject.First();
		ex.MessageText.Should().Contain("cannot promote Transactions.CardId to NOT NULL");

		// The row should still exist in its original nullable state. Query with raw
		// SQL against the unqualified table name (resolved via search_path to public):
		// the forward migration aborted at the 574 guard, before RECEIPTS-746 re-applied,
		// so the tables are still in `public` — not the `receipts` schema the EF model
		// maps `context.Transactions` to. (RECEIPTS-746's Down now correctly returns the
		// tables to public, which is what makes the schema-qualified query miss them here.)
		await context.Database.OpenConnectionAsync();
		try
		{
			await using DbCommand countCmd = context.Database.GetDbConnection().CreateCommand();
			countCmd.CommandText = """SELECT COUNT(*) FROM "Transactions" WHERE "Id" = @id""";
			DbParameter idParam = countCmd.CreateParameter();
			idParam.ParameterName = "id";
			idParam.Value = txId;
			countCmd.Parameters.Add(idParam);

			long nullCardIdCount = (long)(await countCmd.ExecuteScalarAsync())!;
			nullCardIdCount.Should().Be(1);
		}
		finally
		{
			await context.Database.CloseConnectionAsync();
		}

		// Clean up: delete the offending row so later tests (which share the fixture)
		// are not blocked, then reapply the migration to leave the fixture in its
		// canonical post-migration state.
		await context.Database.ExecuteSqlRawAsync("""DELETE FROM "Transactions" WHERE "Id" = {0};""", txId);
		await context.Database.ExecuteSqlRawAsync("""DELETE FROM "Receipts" WHERE "Id" = {0};""", receiptId);
		await context.Database.ExecuteSqlRawAsync("""DELETE FROM "Accounts" WHERE "Id" = {0};""", accountId);
		await migrator.MigrateAsync();
	}

	// RECEIPTS-604: RequireAccountIdOnCards self-heals orphan Cards (AccountId IS
	// NULL) by creating a 1:1 Account with the same Id — mirroring
	// IntroduceAccountAggregate's original backfill. This covers Cards that
	// slipped in between the two migrations (e.g., via backup restore or other
	// paths that bypassed the application-layer validators).
	[Fact]
	public async Task RequireAccountIdOnCards_WithOrphanCard_BackfillsMatchingAccount()
	{
		const string priorMigration = "20260419022200_AddCardIdToTransactions";

		await using ApplicationDbContext context = fixture.CreateDbContext();
		IMigrator migrator = context.GetInfrastructure().GetRequiredService<IMigrator>();

		// Roll back to a state where Cards.AccountId is nullable.
		await migrator.MigrateAsync(priorMigration);

		// Insert an orphan Card directly — no matching Account, AccountId NULL.
		Guid cardId = Guid.NewGuid();
		await context.Database.ExecuteSqlRawAsync(
			"""
			INSERT INTO "Cards" ("Id", "CardCode", "Name", "IsActive", "AccountId")
				VALUES ({0}, 'ORPHAN', 'Orphan Card', true, NULL);
			""",
			cardId);

		// Re-apply all migrations. The self-heal should run before the NOT NULL
		// alter, so the alter sees no NULLs and succeeds.
		await migrator.MigrateAsync();

		// Orphan Card should now point at a matching Account with its own Id.
		Guid? backfilledAccountId = await context.Cards
			.IgnoreQueryFilters()
			.Where(c => c.Id == cardId)
			.Select(c => (Guid?)c.AccountId)
			.FirstOrDefaultAsync();
		backfilledAccountId.Should().Be(cardId);

		bool accountExists = await context.Accounts
			.IgnoreQueryFilters()
			.AnyAsync(a => a.Id == cardId);
		accountExists.Should().BeTrue();

		// Clean up: Card first (FK Restrict on AccountId), then the paired Account.
		await context.Database.ExecuteSqlRawAsync("""DELETE FROM "Cards" WHERE "Id" = {0};""", cardId);
		await context.Database.ExecuteSqlRawAsync("""DELETE FROM "Accounts" WHERE "Id" = {0};""", cardId);
	}
}
