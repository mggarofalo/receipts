using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Trait("Category", "Integration")]
public class TransactionAccountMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DivergentOwnership_RejectsMigrationWithoutDataLoss_ThenLogsSuccessfulRetry(bool trashed)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		string previous = context.Database.GetMigrations().TakeWhile(id => id != "20260905215220_DropTransactionAccountId").Last();
		IMigrator migrator = context.GetService<IMigrator>();
		await migrator.MigrateAsync(previous);
		Guid account = Guid.NewGuid(), other = Guid.NewGuid(), card = Guid.NewGuid(), receipt = Guid.NewGuid(), transaction = Guid.NewGuid();
		await SeedAsync(context, account, other, card, receipt, transaction, trashed);
		try
		{
			await context.Database.ExecuteSqlInterpolatedAsync($"""UPDATE receipts."Transactions" SET "AccountId" = {other} WHERE "Id" = {transaction}""");
			Func<Task> apply = () => migrator.MigrateAsync();
			(await apply.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should()
				.Contain("RECEIPTS-852").And.Contain("1 divergent transaction(s), including trash");
			(await ScalarAsync<long>("""SELECT count(*) FROM receipts."Transactions" WHERE "Id" = @id AND "AccountId" = @owner""", transaction, other)).Should().Be(1);
			(await context.Database.GetAppliedMigrationsAsync()).Should().NotContain("20260905215220_DropTransactionAccountId");
			await context.Database.ExecuteSqlInterpolatedAsync($"""UPDATE receipts."Transactions" SET "AccountId" = {account} WHERE "Id" = {transaction}""");
			CapturingLogger logger = new();
			await new DatabaseMigratorService(new Factory(fixture), logger).MigrateAsync();
			logger.Messages.Should().ContainSingle(message => message.Contains("RECEIPTS-852 account ownership precondition: 0 divergent transaction(s), including trash."));
			(await ScalarAsync<long>("""SELECT count(*) FROM receipts."Transactions" WHERE "Id" = @id""", transaction)).Should().Be(1);
			(await HasAccountColumnAsync()).Should().BeFalse();
		}
		finally
		{
			await context.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM receipts."Transactions" WHERE "Id" = {transaction}""");
			await migrator.MigrateAsync();
		}
	}

	[Fact]
	public async Task Down_BackfillsActiveAndTrashedOwnershipFromCurrentCard_AndRestoresConstraints()
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		string previous = context.Database.GetMigrations().TakeWhile(id => id != "20260905215220_DropTransactionAccountId").Last();
		IMigrator migrator = context.GetService<IMigrator>();
		await migrator.MigrateAsync(previous);
		Guid account = Guid.NewGuid(), replacement = Guid.NewGuid(), card = Guid.NewGuid(), receipt = Guid.NewGuid(), active = Guid.NewGuid(), trashed = Guid.NewGuid();
		await SeedAsync(context, account, replacement, card, receipt, active, false);
		await context.Database.ExecuteSqlInterpolatedAsync($"""
			INSERT INTO receipts."Transactions" ("Id", "ReceiptId", "CardId", "AccountId", "Amount", "AmountCurrency", "Date", "DeletedAt")
			VALUES ({trashed}, {receipt}, {card}, {account}, 20, 'USD', CURRENT_DATE, CURRENT_TIMESTAMP)
			""");
		try
		{
			await migrator.MigrateAsync();
			await context.Database.ExecuteSqlInterpolatedAsync($"""UPDATE library."Cards" SET "AccountId" = {replacement} WHERE "Id" = {card}""");
			await migrator.MigrateAsync(previous);
			(await ScalarAsync<long>("""SELECT count(*) FROM receipts."Transactions" WHERE "CardId" = @id AND "AccountId" = @owner""", card, replacement)).Should().Be(2);
			(await ScalarAsync<long>("""SELECT count(*) FROM pg_indexes WHERE schemaname = 'receipts' AND indexname = 'IX_Transactions_AccountId'""")).Should().Be(1);
			(await ScalarAsync<long>("""SELECT count(*) FROM pg_constraint WHERE conname = 'FK_Transactions_Accounts_AccountId'""")).Should().Be(1);
			Func<Task> invalid = async () => await context.Database.ExecuteSqlInterpolatedAsync($"""UPDATE receipts."Transactions" SET "AccountId" = {Guid.NewGuid()} WHERE "Id" = {active}""");
			(await invalid.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
			await migrator.MigrateAsync();
			(await HasAccountColumnAsync()).Should().BeFalse();
		}
		finally
		{
			await context.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM receipts."Transactions" WHERE "CardId" = {card}""");
			await migrator.MigrateAsync();
		}
	}

	private static async Task SeedAsync(ApplicationDbContext context, Guid account, Guid other, Guid card, Guid receipt, Guid transaction, bool trashed)
	{
		await context.Database.ExecuteSqlInterpolatedAsync($"""
			INSERT INTO library."Accounts" ("Id", "Name", "IsActive") VALUES ({account}, 'Migration owner', true), ({other}, 'Other owner', true);
			INSERT INTO library."Cards" ("Id", "CardCode", "Name", "IsActive", "AccountId") VALUES ({card}, '1234', 'Migration card', true, {account});
			INSERT INTO receipts."Receipts" ("Id", "Location", "Date", "TaxAmount", "TaxAmountCurrency") VALUES ({receipt}, 'Migration', CURRENT_DATE, 0, 'USD');
			INSERT INTO receipts."Transactions" ("Id", "ReceiptId", "CardId", "AccountId", "Amount", "AmountCurrency", "Date", "DeletedAt")
			VALUES ({transaction}, {receipt}, {card}, {account}, 10, 'USD', CURRENT_DATE, CASE WHEN {trashed} THEN CURRENT_TIMESTAMP ELSE NULL END)
			""");
	}

	private async Task<bool> HasAccountColumnAsync() => await ScalarAsync<long>("""SELECT count(*) FROM information_schema.columns WHERE table_schema = 'receipts' AND table_name = 'Transactions' AND column_name = 'AccountId'""") != 0;

	private async Task<T> ScalarAsync<T>(string sql, Guid? id = null, Guid? owner = null)
	{
		await using NpgsqlConnection connection = new(fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(sql, connection);
		if (id is not null)
		{
			command.Parameters.AddWithValue("id", id.Value);
		}

		if (owner is not null)
		{
			command.Parameters.AddWithValue("owner", owner.Value);
		}

		return (T)(await command.ExecuteScalarAsync())!;
	}

	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}

	private sealed class CapturingLogger : ILogger<DatabaseMigratorService>
	{
		public List<string> Messages { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => true;
		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
	}
}
