using System.Data.Common;
using Application.Commands.Transaction.Create;
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
using Npgsql;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class ReceiptCascadeConcurrencyTests(PostgresFixture fixture)
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DeleteRacingValidatedPaymentCreate_NeverLeavesActivePaymentUnderTrashedReceipt(bool cancelCreate)
	{
		await using ApplicationDbContext seed = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.TaxAmount = 0m;
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.Quantity = 1m;
		item.UnitPrice = 10m;
		item.TotalAmount = 10m;
		seed.AddRange(receipt, account, card, item);
		await seed.SaveChangesAsync();

		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
		ChildSnapshotGate gate = new();
		Factory deleteFactory = new(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(gate).Options);
		Task delete = new ReceiptRepository(deleteFactory).DeleteAsync([receipt.Id], timeout.Token);
		await gate.Ready.Task.WaitAsync(timeout.Token);
		Factory createFactory = new(fixture.CreateOptions());
		TransactionService service = new(new TransactionRepository(createFactory), new TransactionMapper(), new AccountMapper(),
			createFactory, new ReceiptMapper(), new ReceiptItemMapper(), new AdjustmentMapper());
		Transaction payment = new(Guid.NewGuid(), card.Id, new Money(10m), receipt.Date) { AccountId = account.Id };
		using CancellationTokenSource createCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
		Task<List<Transaction>> create = new CreateTransactionCommandHandler(service).Handle(new([payment], receipt.Id), createCancellation.Token).AsTask();
		try
		{
			int pid = await createFactory.ProcessId.Task.WaitAsync(timeout.Token);
			await using ApplicationDbContext observer = fixture.CreateDbContext();
			while (!create.IsCompleted && !await observer.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {pid} AND wait_event_type = 'Lock') AS \"Value\"").SingleAsync(timeout.Token))
			{
				await Task.Delay(20, timeout.Token);
			}
			if (cancelCreate)
			{
				createCancellation.Cancel();
				Func<Task> cancelledCreate = () => create;
				await cancelledCreate.Should().ThrowAsync<OperationCanceledException>();
			}
		}
		catch
		{
			gate.Release.TrySetResult();
			try { await Task.WhenAll(delete, create); } catch { }
			throw;
		}
		finally { gate.Release.TrySetResult(); }
		await delete;
		if (!cancelCreate)
		{
			try { await create; }
			catch (KeyNotFoundException) { /* Delete won: the active-parent guard rejects create. */ }
		}
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.Receipts.IgnoreQueryFilters().SingleAsync(r => r.Id == receipt.Id)).DeletedAt.Should().NotBeNull();
		(await read.Transactions.IgnoreQueryFilters().Where(t => t.ReceiptId == receipt.Id && t.DeletedAt == null).CountAsync()).Should().Be(0,
			"a committed payment must be in the locked cascade snapshot, or creation must reject the trashed receipt");
	}

	[Fact]
	public async Task PaymentCreateHoldingParentLock_DeleteWaitsAndCascadesCommittedPayment()
	{
		await using ApplicationDbContext seed = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.TaxAmount = 0m;
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.Quantity = 1m;
		item.UnitPrice = item.TotalAmount = 10m;
		seed.AddRange(receipt, account, card, item);
		await seed.SaveChangesAsync();
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
		PaymentSaveGate gate = new();
		Factory createFactory = new(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(gate).Options);
		TransactionService service = new(new TransactionRepository(createFactory), new TransactionMapper(), new AccountMapper(),
			createFactory, new ReceiptMapper(), new ReceiptItemMapper(), new AdjustmentMapper());
		Transaction payment = new(Guid.NewGuid(), card.Id, new Money(10m), receipt.Date) { AccountId = account.Id };
		Task<List<Transaction>> create = new CreateTransactionCommandHandler(service).Handle(new([payment], receipt.Id), timeout.Token).AsTask();
		await gate.Ready.Task.WaitAsync(timeout.Token);
		Factory deleteFactory = new(fixture.CreateOptions());
		Task delete = new ReceiptRepository(deleteFactory).DeleteAsync([receipt.Id], timeout.Token);
		try
		{
			int pid = await deleteFactory.ProcessId.Task.WaitAsync(timeout.Token);
			await using ApplicationDbContext observer = fixture.CreateDbContext();
			while (!await observer.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {pid} AND wait_event_type = 'Lock') AS \"Value\"").SingleAsync(timeout.Token))
			{
				await Task.Delay(20, timeout.Token);
			}
		}
		catch
		{
			gate.Release.TrySetResult();
			try { await Task.WhenAll(create, delete); } catch { }
			throw;
		}
		finally { gate.Release.TrySetResult(); }
		List<Transaction> created = await create;
		await delete;
		await using ApplicationDbContext read = fixture.CreateDbContext();
		Guid paymentId = created.Single().Id;
		TransactionEntity stored = await read.Transactions.IgnoreQueryFilters().SingleAsync(t => t.Id == paymentId);
		stored.DeletedAt.Should().NotBeNull();
		stored.CascadeDeletedByParentId.Should().Be(receipt.Id);
		(await read.Receipts.IgnoreQueryFilters().SingleAsync(r => r.Id == receipt.Id)).DeletedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task RestorePaymentWhoseReceiptIsTrashed_RejectsWithoutCreatingActiveOrphan()
	{
		await using ApplicationDbContext seed = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		TransactionEntity payment = TransactionEntityGenerator.Generate(receipt.Id, account.Id, card.Id);
		seed.AddRange(receipt, account, card, payment);
		await seed.SaveChangesAsync();
		Factory factory = new(fixture.CreateOptions());
		await new ReceiptRepository(factory).DeleteAsync([receipt.Id], CancellationToken.None);

		TransactionService service = new(new TransactionRepository(factory), new TransactionMapper(), new AccountMapper(),
			factory, new ReceiptMapper(), new ReceiptItemMapper(), new AdjustmentMapper());
		Func<Task> restore = () => service.RestoreAsync(payment.Id, CancellationToken.None);
		await restore.Should().ThrowAsync<Application.Exceptions.ConcurrencyConflictException>();
		await using ApplicationDbContext read = fixture.CreateDbContext();
		TransactionEntity stored = await read.Transactions.IgnoreQueryFilters().SingleAsync(t => t.Id == payment.Id);
		stored.DeletedAt.Should().NotBeNull();
		stored.CascadeDeletedByParentId.Should().Be(receipt.Id);
	}

	[Theory]
	[InlineData("payment", false)]
	[InlineData("item", false)]
	[InlineData("adjustment", false)]
	[InlineData("payment", true)]
	[InlineData("item", true)]
	[InlineData("adjustment", true)]
	public async Task ActiveChildAdmission_RejectsTrashedParent(string kind, bool restore)
	{
		await using ApplicationDbContext seed = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		seed.AddRange(receipt, account, card);
		await seed.SaveChangesAsync();
		object child = kind switch
		{
			"payment" => TransactionEntityGenerator.Generate(receipt.Id, account.Id, card.Id),
			"item" => ReceiptItemEntityGenerator.Generate(receipt.Id),
			_ => AdjustmentEntityGenerator.Generate(),
		};
		if (child is AdjustmentEntity adjustment)
		{
			adjustment.ReceiptId = receipt.Id;
		}

		if (restore)
		{
			seed.Add(child);
			await seed.SaveChangesAsync();
		}
		seed.Receipts.Remove(receipt);
		await seed.SaveChangesAsync();
		if (restore)
		{
			((Infrastructure.Interfaces.ISoftDeletable)child).DeletedAt = null;
		}
		else
		{
			seed.Add(child);
		}

		Guid childId = (Guid)seed.Entry(child).Property("Id").CurrentValue!;
		string auditId = childId.ToString();
		int auditCount = await seed.AuditLogs.CountAsync(a => a.EntityId == auditId);
		Func<Task> save = () => seed.SaveChangesAsync();
		await save.Should().ThrowAsync<Application.Exceptions.ConcurrencyConflictException>();
		await using ApplicationDbContext read = fixture.CreateDbContext();
		int activeChildren = kind switch
		{
			"payment" => await read.Transactions.IgnoreQueryFilters().CountAsync(t => t.Id == childId && t.DeletedAt == null),
			"item" => await read.ReceiptItems.IgnoreQueryFilters().CountAsync(i => i.Id == childId && i.DeletedAt == null),
			_ => await read.Adjustments.IgnoreQueryFilters().CountAsync(a => a.Id == childId && a.DeletedAt == null),
		};
		activeChildren.Should().Be(0);
		(await read.AuditLogs.CountAsync(a => a.EntityId == auditId)).Should().Be(auditCount);
	}

	[Theory]
	[InlineData("payment")]
	[InlineData("item")]
	[InlineData("adjustment")]
	public async Task ReparentActiveChildToTrashedReceipt_RejectsAndPreservesOriginalParent(string kind)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity original = ReceiptEntityGenerator.Generate();
		ReceiptEntity trashed = ReceiptEntityGenerator.Generate();
		trashed.DeletedAt = DateTimeOffset.UtcNow;
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		object child = kind switch
		{
			"payment" => TransactionEntityGenerator.Generate(original.Id, account.Id, card.Id),
			"item" => ReceiptItemEntityGenerator.Generate(original.Id),
			_ => AdjustmentEntityGenerator.Generate(),
		};
		if (child is AdjustmentEntity adjustment)
		{
			adjustment.ReceiptId = original.Id;
		}

		context.AddRange(original, trashed, account, card, child);
		await context.SaveChangesAsync();
		context.Entry(child).Property("ReceiptId").CurrentValue = trashed.Id;
		// Exercise the synchronous save path as well as asynchronous admission above.
		Action save = () => context.SaveChanges();
		save.Should().Throw<Application.Exceptions.ConcurrencyConflictException>();
		await context.Entry(child).ReloadAsync();
		context.Entry(child).Property("ReceiptId").CurrentValue.Should().Be(original.Id);
	}

	[Fact]
	public async Task RestoreReceipt_ReactivatesItsOwnedPaymentAtomically()
	{
		await using ApplicationDbContext seed = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		TransactionEntity payment = TransactionEntityGenerator.Generate(receipt.Id, account.Id, card.Id);
		seed.AddRange(receipt, account, card, payment);
		await seed.SaveChangesAsync();
		ReceiptRepository repository = new(new Factory(fixture.CreateOptions()));
		await repository.DeleteAsync([receipt.Id], CancellationToken.None);
		(await repository.RestoreAsync(receipt.Id, CancellationToken.None)).EntityChanged.Should().BeTrue();
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.Receipts.AnyAsync(r => r.Id == receipt.Id)).Should().BeTrue();
		(await read.Transactions.AnyAsync(t => t.Id == payment.Id)).Should().BeTrue();
	}

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public TaskCompletionSource<int> ProcessId { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public ApplicationDbContext CreateDbContext()
		{
			ApplicationDbContext context = new(options);
			context.Database.OpenConnection();
			ProcessId.TrySetResult(((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID);
			return context;
		}
		public async Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
		{
			ApplicationDbContext context = new(options);
			await context.Database.OpenConnectionAsync(cancellationToken);
			ProcessId.TrySetResult(((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID);
			return context;
		}
	}

	private sealed class PaymentSaveGate : SaveChangesInterceptor
	{
		public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
			InterceptionResult<int> result, CancellationToken cancellationToken = default)
		{
			Ready.TrySetResult();
			await Release.Task.WaitAsync(cancellationToken);
			return result;
		}
	}

	private sealed class ChildSnapshotGate : DbCommandInterceptor
	{
		public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
			CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			// Delete has fully materialized its transaction snapshot before its next child query.
			if (command.CommandText.Contains("FROM receipts.\"Adjustments\"", StringComparison.Ordinal)
				|| command.CommandText.Contains("FROM \"receipts\".\"Adjustments\"", StringComparison.Ordinal))
			{
				Ready.TrySetResult();
				await Release.Task.WaitAsync(cancellationToken);
			}
			return result;
		}
	}
}
