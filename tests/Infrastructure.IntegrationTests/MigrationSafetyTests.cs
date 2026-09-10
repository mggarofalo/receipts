using System.Data.Common;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class MigrationSafetyTests(PostgresFixture fixture)
{
	[Fact]
	public async Task ScopeYnabDestinationIdentity_PreservesExistingRowsAndScopesUniqueIndexesByBudget()
	{
		const string priorMigration = "20260905190000_AddRefreshSessionId";
		const string budgetA = "11111111-1111-1111-1111-111111111111";
		const string budgetB = "22222222-2222-2222-2222-222222222222";
		string legacyBudgetA = budgetA.ToUpperInvariant();
		string xFormattedBudgetA = Guid.Parse(budgetA).ToString("X");
		Guid selectedBudgetRowId = Guid.NewGuid();
		string receiptsCategory = $"Groceries-{Guid.NewGuid():N}";
		Guid accountMappingAId = Guid.NewGuid();
		Guid categoryMappingAId = Guid.NewGuid();
		Guid syncRecordAId = Guid.NewGuid();
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id, card.Id);

		await using ApplicationDbContext migrationContext = fixture.CreateDbContext();
		IMigrator migrator = migrationContext.GetInfrastructure().GetRequiredService<IMigrator>();
		try
		{
			await migrator.MigrateAsync(priorMigration);
			migrationContext.AddRange(account, card, receipt);
			await migrationContext.SaveChangesAsync();
			await migrationContext.Database.ExecuteSqlRawAsync(
				"""
				INSERT INTO "Transactions" ("Id", "ReceiptId", "AccountId", "CardId", "Amount", "AmountCurrency", "Date")
					VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6});
				""",
				transaction.Id, receipt.Id, account.Id, card.Id, transaction.Amount,
				transaction.AmountCurrency.ToString(), transaction.Date);
			await migrationContext.Database.ExecuteSqlRawAsync(
				"""
				INSERT INTO ynab."YnabSelectedBudgets" ("Id", "BudgetId", "UpdatedAt")
					VALUES ({0}, {1}, CURRENT_TIMESTAMP);
				""",
				selectedBudgetRowId, xFormattedBudgetA);
			migrationContext.YnabAccountMappings.Add(new YnabAccountMappingEntity
			{
				Id = accountMappingAId,
				ReceiptsAccountId = account.Id,
				YnabAccountId = "account-A",
				YnabAccountName = "Account A",
				YnabBudgetId = legacyBudgetA,
			});
			migrationContext.YnabCategoryMappings.Add(new YnabCategoryMappingEntity
			{
				Id = categoryMappingAId,
				ReceiptsCategory = receiptsCategory,
				YnabCategoryId = "category-A",
				YnabCategoryName = "Category A",
				YnabCategoryGroupName = "Group A",
				YnabBudgetId = legacyBudgetA,
			});
			migrationContext.YnabSyncRecords.Add(new YnabSyncRecordEntity
			{
				Id = syncRecordAId,
				LocalTransactionId = transaction.Id,
				YnabTransactionId = "transaction-A",
				YnabBudgetId = legacyBudgetA,
				SyncType = YnabSyncType.TransactionPush,
				SyncStatus = YnabSyncStatus.Synced,
			});
			await migrationContext.SaveChangesAsync();
			migrationContext.ChangeTracker.Clear();

			await migrator.MigrateAsync();

			(await migrationContext.YnabAccountMappings.SingleAsync(row => row.Id == accountMappingAId))
				.YnabBudgetId.Should().Be(budgetA);
			(await migrationContext.YnabCategoryMappings.SingleAsync(row => row.Id == categoryMappingAId))
				.YnabBudgetId.Should().Be(budgetA);
			(await migrationContext.YnabSyncRecords.SingleAsync(row => row.Id == syncRecordAId))
				.YnabBudgetId.Should().Be(budgetA);
			string normalizedSelection = await migrationContext.Database
				.SqlQueryRaw<string>(
					"""SELECT "BudgetId" AS "Value" FROM ynab."YnabSelectedBudgets" WHERE "Id" = {0}""",
					selectedBudgetRowId)
				.SingleAsync();
			normalizedSelection.Should().Be(budgetA);

			migrationContext.YnabAccountMappings.Add(new YnabAccountMappingEntity
			{
				Id = Guid.NewGuid(),
				ReceiptsAccountId = account.Id,
				YnabAccountId = "account-B",
				YnabAccountName = "Account B",
				YnabBudgetId = budgetB,
			});
			migrationContext.YnabCategoryMappings.Add(new YnabCategoryMappingEntity
			{
				Id = Guid.NewGuid(),
				ReceiptsCategory = receiptsCategory,
				YnabCategoryId = "category-B",
				YnabCategoryName = "Category B",
				YnabCategoryGroupName = "Group B",
				YnabBudgetId = budgetB,
			});
			migrationContext.YnabSyncRecords.Add(new YnabSyncRecordEntity
			{
				Id = Guid.NewGuid(),
				LocalTransactionId = transaction.Id,
				YnabTransactionId = "transaction-B",
				YnabBudgetId = budgetB,
				SyncType = YnabSyncType.TransactionPush,
				SyncStatus = YnabSyncStatus.Synced,
			});
			await migrationContext.SaveChangesAsync();

			(await migrationContext.YnabAccountMappings.CountAsync(row => row.ReceiptsAccountId == account.Id)).Should().Be(2);
			(await migrationContext.YnabCategoryMappings.CountAsync(row => row.ReceiptsCategory == receiptsCategory)).Should().Be(2);
			(await migrationContext.YnabSyncRecords.CountAsync(row => row.LocalTransactionId == transaction.Id)).Should().Be(2);

			await AssertUniqueViolationAsync(async duplicateContext =>
			{
				duplicateContext.YnabAccountMappings.Add(new YnabAccountMappingEntity
				{
					Id = Guid.NewGuid(),
					ReceiptsAccountId = account.Id,
					YnabAccountId = "other-account-B",
					YnabAccountName = "Duplicate",
					YnabBudgetId = budgetB,
				});
				await duplicateContext.SaveChangesAsync();
			});

			Func<Task> downgrade = () => migrator.MigrateAsync(priorMigration);
			PostgresException guard = (await downgrade.Should().ThrowAsync<PostgresException>()).Which;
			guard.MessageText.Should().Be(
				"Cannot downgrade ScopeYnabDestinationIdentity while multiple YNAB budget bindings exist. " +
				"Remove the extra destination bindings explicitly before retrying.");

			(await migrationContext.Database.GetAppliedMigrationsAsync())
				.Should().Contain("20260910081448_ScopeYnabDestinationIdentity");
			(await migrationContext.YnabAccountMappings.CountAsync(row => row.ReceiptsAccountId == account.Id)).Should().Be(2);
			(await migrationContext.YnabCategoryMappings.CountAsync(row => row.ReceiptsCategory == receiptsCategory)).Should().Be(2);
			(await migrationContext.YnabSyncRecords.CountAsync(row => row.LocalTransactionId == transaction.Id)).Should().Be(2);
			int scopedIndexCount = await migrationContext.Database.SqlQueryRaw<int>(
				"""
				SELECT COUNT(*)::int AS "Value"
				FROM pg_indexes
				WHERE schemaname = 'ynab'
					AND indexname IN (
						'IX_YnabAccountMappings_ReceiptsAccountId_YnabBudgetId',
						'IX_YnabCategoryMappings_ReceiptsCategory_YnabBudgetId',
						'IX_YnabSyncRecords_LocalTransactionId_SyncType_YnabBudgetId')
				""")
				.SingleAsync();
			scopedIndexCount.Should().Be(3);
			await AssertUniqueViolationAsync(async duplicateContext =>
			{
				duplicateContext.YnabCategoryMappings.Add(new YnabCategoryMappingEntity
				{
					Id = Guid.NewGuid(),
					ReceiptsCategory = receiptsCategory,
					YnabCategoryId = "other-category-B",
					YnabCategoryName = "Duplicate",
					YnabCategoryGroupName = "Duplicate",
					YnabBudgetId = budgetB,
				});
				await duplicateContext.SaveChangesAsync();
			});
			await AssertUniqueViolationAsync(async duplicateContext =>
			{
				duplicateContext.YnabSyncRecords.Add(new YnabSyncRecordEntity
				{
					Id = Guid.NewGuid(),
					LocalTransactionId = transaction.Id,
					YnabBudgetId = budgetB,
					SyncType = YnabSyncType.TransactionPush,
					SyncStatus = YnabSyncStatus.Pending,
				});
				await duplicateContext.SaveChangesAsync();
			});
		}
		finally
		{
			await migrator.MigrateAsync();
			await using ApplicationDbContext cleanup = fixture.CreateDbContext();
			await cleanup.YnabSyncRecords.IgnoreQueryFilters()
				.Where(row => row.LocalTransactionId == transaction.Id).ExecuteDeleteAsync();
			await cleanup.YnabCategoryMappings
				.Where(row => row.ReceiptsCategory == receiptsCategory).ExecuteDeleteAsync();
			await cleanup.YnabAccountMappings
				.Where(row => row.ReceiptsAccountId == account.Id).ExecuteDeleteAsync();
			await cleanup.Database.ExecuteSqlRawAsync(
				"""DELETE FROM ynab."YnabSelectedBudgets" WHERE "Id" = {0};""",
				selectedBudgetRowId);
			await cleanup.Transactions.IgnoreQueryFilters()
				.Where(row => row.Id == transaction.Id).ExecuteDeleteAsync();
			await cleanup.Cards.Where(row => row.Id == card.Id).ExecuteDeleteAsync();
			await cleanup.Receipts.IgnoreQueryFilters()
				.Where(row => row.Id == receipt.Id).ExecuteDeleteAsync();
			await cleanup.Accounts.Where(row => row.Id == account.Id).ExecuteDeleteAsync();
		}
	}

	private async Task AssertUniqueViolationAsync(Func<ApplicationDbContext, Task> action)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		Func<Task> act = () => action(context);
		DbUpdateException exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
		exception.InnerException.Should().BeOfType<PostgresException>()
			.Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
	}

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
