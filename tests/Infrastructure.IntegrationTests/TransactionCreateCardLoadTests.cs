using System.Data.Common;
using Domain;
using Domain.Core;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.IntegrationTests;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class TransactionCreateCardLoadTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CardReadFailure_DoesNotCommitCreate(bool complete)
	{
		Guid account = Guid.NewGuid(), card = Guid.NewGuid(), receipt = Guid.NewGuid();
		string location = $"Card load {receipt}";
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Accounts.Add(new() { Id = account, Name = "Create owner", IsActive = true });
			seed.Cards.Add(new() { Id = card, AccountId = account, Name = "Create card", CardCode = "1234", IsActive = true });
			if (!complete)
			{
				seed.Receipts.Add(new() { Id = receipt, Location = location, Date = new(2025, 1, 1) });
			}

			await seed.SaveChangesAsync();
		}
		Factory factory = new(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(new CardReadFailure()).Options);
		Func<Task> create = complete
			? async () => await new CompleteReceiptService(factory, new(), new(), new(), new()).CreateAsync(new Receipt(Guid.Empty, location, new(2025, 1, 1), Money.Zero), [new Transaction(Guid.Empty, card, new Money(10), new(2025, 1, 1))], [], [], CancellationToken.None)
			: async () => await new TransactionRepository(factory).CreateAsync([new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receipt, CardId = card, Amount = 10, Date = new(2025, 1, 1) }], CancellationToken.None);
		await create.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected originating card lookup failure");
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.Transactions.IgnoreAutoIncludes().CountAsync(row => row.CardId == card)).Should().Be(0, "a rejected create must not leave a committed payment that a retry duplicates");
		if (complete)
		{
			(await read.Receipts.CountAsync(row => row.Location == location)).Should().Be(0);
		}
	}

	private sealed class CardReadFailure : DbCommandInterceptor
	{
		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("FROM library.\"Cards\"", StringComparison.Ordinal))
			{
				throw new InvalidOperationException("Injected originating card lookup failure");
			}

			return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
		}
	}

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
	}
}
