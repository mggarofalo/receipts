using System.Data.Common;
using Application.Commands.Reports;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class DuplicateAcceptanceOrderingTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	[Fact]
	public async Task AcceptanceHoldingReceiptLocks_EditWaitsThenInvalidatesCommittedAcceptance()
	{
		Guid[] ids = await SeedAsync();
		InsertGate gate = new();
		Factory factory = new(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(gate).Options);
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
		Task<int> acceptance = AcceptAsync(factory, ids, timeout.Token);
		await gate.Inserted.Task.WaitAsync(timeout.Token);
		await using ApplicationDbContext edit = fixture.CreateDbContext();
		await edit.Database.OpenConnectionAsync(timeout.Token);
		TransactionEntity payment = await edit.Transactions.SingleAsync(t => t.ReceiptId == ids[0], timeout.Token);
		payment.Amount = 12m;
		Task<int> save = edit.SaveChangesAsync(timeout.Token);
		try
		{
			await WaitForDatabaseLockAsync(((NpgsqlConnection)edit.Database.GetDbConnection()).ProcessID, timeout.Token);
		}
		catch
		{
			gate.Release.TrySetResult();
			try { await Task.WhenAll(acceptance, save); } catch { /* Preserve the lock diagnostic after draining both operations. */ }
			throw;
		}
		finally { gate.Release.TrySetResult(); }
		(await acceptance).Should().Be(1);
		await save;
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.AcceptedDuplicatePairs.CountAsync()).Should().Be(0);
		(await read.AcceptedDuplicatePairs.IgnoreQueryFilters().SingleAsync()).DeletedAt.Should().NotBeNull();
		(await read.Transactions.SingleAsync(t => t.ReceiptId == ids[0])).Amount.Should().Be(12m);
	}

	[Fact]
	public async Task EditHoldingReceiptLocks_AcceptanceWaitsThenAcknowledgesNewFacts()
	{
		Guid[] ids = await SeedAsync();
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
		await using ApplicationDbContext edit = fixture.CreateDbContext();
		await using var transaction = await edit.Database.BeginTransactionAsync(timeout.Token);
		TransactionEntity payment = await edit.Transactions.SingleAsync(t => t.ReceiptId == ids[0], timeout.Token);
		payment.Amount = 12m;
		await edit.SaveChangesAsync(timeout.Token);
		Factory factory = new(fixture.CreateOptions());
		Task<int> acceptance = AcceptAsync(factory, ids, timeout.Token);
		int processId = await factory.ProcessId.Task.WaitAsync(timeout.Token);
		try
		{
			await WaitForDatabaseLockAsync(processId, timeout.Token);
			await transaction.CommitAsync(timeout.Token);
		}
		catch
		{
			await transaction.RollbackAsync();
			try { await acceptance; } catch { /* Drain the operation before disposing its fixture. */ }
			throw;
		}
		(await acceptance).Should().Be(1);
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.AcceptedDuplicatePairs.CountAsync()).Should().Be(1);
		(await read.Transactions.SingleAsync(t => t.ReceiptId == ids[0])).Amount.Should().Be(12m);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task FaultAfterInvalidation_RollsBackFactsAcceptanceAndAudit_EvenIfCallerCommits(bool callerTransaction)
	{
		Guid[] ids = await SeedAsync();
		(await AcceptAsync(new(fixture.CreateOptions()), ids, CancellationToken.None)).Should().Be(1);
		await using ApplicationDbContext before = fixture.CreateDbContext();
		int auditCount = await before.AuditLogs.CountAsync(a => a.EntityId == ids[0].ToString());
		RejectInvalidation fault = new();
		var options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(fault).Options;
		await using (ApplicationDbContext edit = new(options))
		{
			await using var transaction = callerTransaction ? await edit.Database.BeginTransactionAsync() : null;
			ReceiptEntity receipt = await edit.Receipts.SingleAsync(r => r.Id == ids[0]);
			receipt.Location = "Must roll back";
			Func<Task> save = () => edit.SaveChangesAsync();
			await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected after invalidation");
			fault.AffectedRows.Should().Be(1, "the failure must occur after the acceptance was actually invalidated");
			if (transaction is not null)
			{
				await transaction.CommitAsync();
			}
		}
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.Receipts.SingleAsync(r => r.Id == ids[0])).Location.Should().Be("Original");
		(await read.AcceptedDuplicatePairs.SingleAsync()).DeletedAt.Should().BeNull();
		(await read.AcceptedDuplicatePairs.IgnoreQueryFilters().CountAsync()).Should().Be(1);
		(await read.AuditLogs.CountAsync(a => a.EntityId == ids[0].ToString())).Should().Be(auditCount);
	}

	private static Task<int> AcceptAsync(Factory factory, Guid[] ids, CancellationToken token)
		=> new AcceptDuplicateGroupCommandHandler(new DuplicateAcceptanceStore(factory), TimeProvider.System)
			.Handle(new([.. ids]), token).AsTask();

	private async Task WaitForDatabaseLockAsync(int processId, CancellationToken token)
	{
		await using ApplicationDbContext observer = fixture.CreateDbContext();
		try
		{
			while (!await observer.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {processId} AND wait_event_type = 'Lock') AS \"Value\"").SingleAsync(token))
			{
				await Task.Delay(20, token);
			}
		}
		catch (OperationCanceledException exception) when (token.IsCancellationRequested)
		{
			throw new TimeoutException($"PostgreSQL backend {processId} never entered the expected receipt-lock wait.", exception);
		}
	}

	private async Task<Guid[]> SeedAsync()
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		context.Accounts.Add(account);
		context.Cards.Add(card);
		Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
		foreach (Guid id in ids)
		{
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			receipt.Id = id;
			receipt.Location = "Original";
			receipt.Date = new(2025, 3, 1);
			context.Receipts.Add(receipt);
			TransactionEntity payment = TransactionEntityGenerator.Generate(id, account.Id, card.Id);
			payment.Amount = 10m;
			context.Transactions.Add(payment);
		}
		await context.SaveChangesAsync();
		return ids;
	}

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public TaskCompletionSource<int> ProcessId { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public ApplicationDbContext CreateDbContext() => new(options);
		public async Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
		{
			ApplicationDbContext context = new(options);
			await context.Database.OpenConnectionAsync(cancellationToken);
			ProcessId.TrySetResult(((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID);
			return context;
		}
	}

	private sealed class InsertGate : DbCommandInterceptor
	{
		public TaskCompletionSource Inserted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("INSERT INTO \"receipts\".\"AcceptedDuplicatePairs\"", StringComparison.Ordinal))
			{
				Inserted.TrySetResult();
				await Release.Task.WaitAsync(cancellationToken);
			}
			return result;
		}
	}

	private sealed class RejectInvalidation : DbCommandInterceptor
	{
		public int AffectedRows { get; private set; }
		public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal) && command.CommandText.Contains("\"AcceptedDuplicatePairs\"", StringComparison.Ordinal))
			{
				AffectedRows = result;
				throw new InvalidOperationException("Injected after invalidation");
			}
			return ValueTask.FromResult(result);
		}
	}
}
