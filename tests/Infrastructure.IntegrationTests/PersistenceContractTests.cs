using System.Data.Common;
using System.Transactions;
using Application.Interfaces.Services;
using Common;
using Domain;
using Domain.Core;
using FluentAssertions;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class PersistenceContractTests(PostgresFixture fixture)
{
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(5)]
	public async Task SaveOverloads_PersistGeneratedKeyAndSemanticAudit_PreserveRequestedAcceptance(int overload)
	{
		CommandPath commands = new();
		await using ApplicationDbContext context = new(Options(commands));
		ReceiptEntity receipt = Receipt();
		context.Receipts.Add(receipt);
		receipt.Id.Should().NotBeEmpty();
		context.Entry(receipt).Property(row => row.Id).IsTemporary.Should().BeFalse();
		AuditLogEntity semantic = SemanticAudit(receipt.Id);
		context.AuditLogs.Add(semantic);

		int affected = await Save(context, overload);

		affected.Should().Be(2, "caller-authored semantic audit rows count, automatic rows do not");
		bool accepts = overload is not (2 or 5);
		context.Entry(receipt).State.Should().Be(accepts ? EntityState.Unchanged : EntityState.Added);
		context.Entry(semantic).State.Should().Be(accepts ? EntityState.Unchanged : EntityState.Added);
		context.ChangeTracker.Entries<AuditLogEntity>().Count().Should().Be(accepts ? 2 : 1);
		if (overload < 3)
		{
			commands.SyncWrites.Should().BeGreaterThan(0);
			commands.AsyncWrites.Should().Be(0, "synchronous saves must use synchronous provider APIs");
		}
		else
		{
			commands.AsyncWrites.Should().BeGreaterThan(0);
			commands.SyncWrites.Should().Be(0);
		}
		context.ChangeTracker.AcceptAllChanges();
		(await context.SaveChangesAsync()).Should().Be(0, "accepting the caller's pending state must not replay internal audits");
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.Receipts.AnyAsync(row => row.Id == receipt.Id)).Should().BeTrue();
		List<AuditLogEntity> audits = await read.AuditLogs.Where(row => row.EntityId == receipt.Id.ToString()).ToListAsync();
		audits.Should().HaveCount(2);
		audits.Should().ContainSingle(row => row.Action == AuditAction.Create && row.EntityType == "Receipt");
		audits.Should().ContainSingle(row => row.Id == semantic.Id);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public async Task ReconciliationFailure_AfterBusinessAndAuditWrites_RollsBackEverything(bool synchronous, bool cancel)
	{
		using CancellationTokenSource cancellation = new();
		ReconciliationFault fault = new() { Armed = true, Cancellation = cancel ? cancellation : null };
		Mock<IDescriptionChangeSignal> signal = new();
		await using ApplicationDbContext context = new(Options(fault), Mock.Of<ICurrentUserAccessor>(), signal.Object);
		ReceiptEntity receipt = Receipt();
		ReceiptItemEntity item = Item(receipt);
		context.Receipts.Add(receipt);
		context.ReceiptItems.Add(item);
		Func<Task> save = synchronous
			? () => { context.SaveChanges(); return Task.CompletedTask; }
		: () => context.SaveChangesAsync(cancellation.Token);

		if (cancel)
		{
			await save.Should().ThrowAsync<OperationCanceledException>();
		}
		else
		{
			await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected reconciliation failure");
		}

		fault.Fired.Should().BeTrue();
		fault.InsertCompleted.Should().BeTrue("the description INSERT and preceding business/audit writes must have executed before the fault");
		context.Entry(item).State.Should().Be(EntityState.Added);
		context.ChangeTracker.Entries<AuditLogEntity>().Should().BeEmpty();
		signal.Verify(row => row.NotifyDirty(), Times.Never);
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.Receipts.AnyAsync(row => row.Id == receipt.Id)).Should().BeFalse();
		(await read.ReceiptItems.AnyAsync(row => row.Id == item.Id)).Should().BeFalse();
		(await read.AuditLogs.AnyAsync(row => row.EntityId == receipt.Id.ToString() || row.EntityId == item.Id.ToString())).Should().BeFalse();
		(await read.DistinctDescriptions.AnyAsync(row => row.Description == item.Description)).Should().BeFalse();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CallerTransaction_FailedSavePreservesPriorWrites_AndAllowsRecoveryCommit(bool cancel)
	{
		using CancellationTokenSource cancellation = new();
		ReconciliationFault fault = new() { Cancellation = cancel ? cancellation : null };
		await using ApplicationDbContext context = new(Options(fault));
		await using var transaction = await context.Database.BeginTransactionAsync();
		ReceiptEntity prior = Receipt();
		context.Receipts.Add(prior);
		await context.SaveChangesAsync();
		ReceiptItemEntity failed = Item(prior);
		AuditLogEntity semantic = SemanticAudit(prior.Id);
		context.ReceiptItems.Add(failed);
		context.AuditLogs.Add(semantic);
		fault.Armed = true;

		Func<Task> save = () => context.SaveChangesAsync(cancellation.Token);
		if (cancel)
		{
			await save.Should().ThrowAsync<OperationCanceledException>();
		}
		else
		{
			await save.Should().ThrowAsync<InvalidOperationException>();
		}

		context.Database.CurrentTransaction.Should().BeSameAs(transaction);
		(await context.Receipts.AsNoTracking().AnyAsync(row => row.Id == prior.Id)).Should().BeTrue();
		(await context.ReceiptItems.AsNoTracking().AnyAsync(row => row.Id == failed.Id)).Should().BeFalse();
		context.Entry(semantic).State.Should().Be(EntityState.Added, "the failed save must only detach automatically generated audits");
		context.ChangeTracker.Entries<AuditLogEntity>().Where(row => row.State == EntityState.Added).Should().ContainSingle(row => row.Entity == semantic);
		context.Entry(failed).State = EntityState.Detached;
		fault.Armed = false;
		ReceiptEntity recovery = Receipt();
		context.Receipts.Add(recovery);
		(await context.SaveChangesAsync()).Should().Be(2);
		await transaction.CommitAsync();

		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.Receipts.CountAsync(row => row.Id == prior.Id || row.Id == recovery.Id)).Should().Be(2);
		(await read.AuditLogs.CountAsync(row => row.EntityId == prior.Id.ToString())).Should().Be(2);
		(await read.AuditLogs.CountAsync(row => row.EntityId == recovery.Id.ToString())).Should().Be(1);
		(await read.AuditLogs.AnyAsync(row => row.EntityId == failed.Id.ToString())).Should().BeFalse();
		(await read.DistinctDescriptions.AnyAsync(row => row.Description == failed.Description)).Should().BeFalse();
	}

	[Fact]
	public async Task Split_LosingConcurrentNormalizedInsert_RecoversWithoutGhostAudit()
	{
		ReceiptEntity receipt = Receipt();
		ReceiptItemEntity item = Item(receipt);
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Receipts.Add(receipt);
			seed.ReceiptItems.Add(item);
			await seed.SaveChangesAsync();
		}
		string canonical = $"Split winner {Guid.NewGuid():N}";
		InsertRace race = new(fixture.CreateOptions(), canonical);
		NormalizedDescriptionService service = new(new Factory(Options(race)), Mock.Of<IEmbeddingService>(), new NormalizedDescriptionMapper(), new NormalizedDescriptionSettingsMapper());

		await service.SplitAsync([item.Id], canonical, CancellationToken.None);

		race.LoserId.Should().NotBeEmpty("the competing insert must occur after the service's last lookup");
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.ReceiptItems.SingleAsync(row => row.Id == item.Id)).NormalizedDescriptionId.Should().Be(race.WinnerId);
		(await read.NormalizedDescriptions.AnyAsync(row => row.Id == race.LoserId)).Should().BeFalse();
		(await read.AuditLogs.AnyAsync(row => row.EntityId == race.LoserId.ToString())).Should().BeFalse("the service reuses its context for the split after detaching the losing insert");
		(await read.AuditLogs.CountAsync(row => row.EntityId == race.WinnerId.ToString() && row.Action == AuditAction.Split)).Should().Be(1);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CompleteReceipt_ReconciliationBoundary_CoversEntireAggregate(bool fail)
	{
		ReconciliationFault fault = new() { Armed = fail };
		string location = $"Complete {Guid.NewGuid():N}";
		string description = $"Complete item {Guid.NewGuid():N}";
		CompleteReceiptService service = new(new Factory(Options(fault)), new ReceiptMapper(), new TransactionMapper(), new ReceiptItemMapper(), new AdjustmentMapper());
		Func<Task> save = async () => await service.CreateAsync(
			new Receipt(Guid.NewGuid(), location, new(2024, 1, 1), new Money(0)), [],
			[new ReceiptItem(Guid.NewGuid(), null, description, 1, new Money(10), new Money(10), "Food", null)],
			[new Adjustment(Guid.NewGuid(), AdjustmentType.Discount, new Money(-2), "Discount")], CancellationToken.None);
		if (fail)
		{
			await save.Should().ThrowAsync<InvalidOperationException>();
		}
		else
		{
			await save();
		}
		await using ApplicationDbContext read = fixture.CreateDbContext();
		ReceiptEntity? stored = await read.Receipts.SingleOrDefaultAsync(row => row.Location == location);
		if (fail)
		{
			stored.Should().BeNull();
			(await read.ReceiptItems.AnyAsync(row => row.Description == description)).Should().BeFalse();
		}
		else
		{
			stored.Should().NotBeNull();
			(await read.ReceiptItems.CountAsync(row => row.ReceiptId == stored!.Id)).Should().Be(1);
			(await read.Adjustments.CountAsync(row => row.ReceiptId == stored!.Id)).Should().Be(1);
			(await read.AuditLogs.CountAsync(row => row.EntityId == stored!.Id.ToString())).Should().Be(1);
		}
		(await read.DistinctDescriptions.AnyAsync(row => row.Description == description)).Should().Be(!fail);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task FailedDescriptionEditOrSoftDelete_RestoresPreviousProjectionAndAudit(bool softDelete)
	{
		ReceiptEntity receipt = Receipt();
		ReceiptItemEntity item = Item(receipt);
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Receipts.Add(receipt);
			seed.ReceiptItems.Add(item);
			await seed.SaveChangesAsync();
		}
		string replacement = $"Replacement {Guid.NewGuid():N}";
		ReconciliationFault fault = new() { Armed = true };
		await using (ApplicationDbContext write = new(Options(fault)))
		{
			ReceiptItemEntity edit = await write.ReceiptItems.SingleAsync(row => row.Id == item.Id);
			if (softDelete)
			{
				write.ReceiptItems.Remove(edit);
			}
			else
			{
				edit.Description = replacement;
			}
			Func<Task> save = () => write.SaveChangesAsync();
			await save.Should().ThrowAsync<InvalidOperationException>();
		}
		fault.InsertCompleted.Should().BeTrue();
		await using ApplicationDbContext read = fixture.CreateDbContext();
		ReceiptItemEntity stored = await read.ReceiptItems.SingleAsync(row => row.Id == item.Id);
		stored.Description.Should().Be(item.Description);
		stored.DeletedAt.Should().BeNull();
		(await read.DistinctDescriptions.AnyAsync(row => row.Description == item.Description)).Should().BeTrue();
		(await read.DistinctDescriptions.AnyAsync(row => row.Description == replacement)).Should().BeFalse();
		(await read.AuditLogs.CountAsync(row => row.EntityId == item.Id.ToString())).Should().Be(1);
	}

	[Fact]
	public async Task SuccessfulDescriptionSave_NotifiesOnlyAfterCommittedProjectionIsVisible()
	{
		ReceiptEntity receipt = Receipt();
		ReceiptItemEntity item = Item(receipt);
		bool observedCommittedProjection = false;
		Mock<IDescriptionChangeSignal> signal = new();
		signal.Setup(row => row.NotifyDirty()).Callback(() =>
		{
			using ApplicationDbContext observer = fixture.CreateDbContext();
			observedCommittedProjection = observer.ReceiptItems.Any(row => row.Id == item.Id)
				&& observer.DistinctDescriptions.Any(row => row.Description == item.Description)
				&& observer.AuditLogs.Any(row => row.EntityId == item.Id.ToString());
		});
		await using ApplicationDbContext context = new(fixture.CreateOptions(), Mock.Of<ICurrentUserAccessor>(), signal.Object);
		context.Receipts.Add(receipt);
		context.ReceiptItems.Add(item);

		(await context.SaveChangesAsync()).Should().Be(2);

		observedCommittedProjection.Should().BeTrue("a separate connection must see business, audit and projection before the wake-up");
		signal.Verify(row => row.NotifyDirty(), Times.Once);
		item.Quantity = 2;
		item.TotalAmount = 2;
		await context.SaveChangesAsync();
		signal.Verify(row => row.NotifyDirty(), Times.Once, "an unchanged description does not invalidate the projection");
	}

	[Fact]
	public async Task TemporaryAuditedKey_IsRejectedBeforePersistence()
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = Receipt();
		context.Receipts.Add(receipt);
		context.Entry(receipt).Property(row => row.Id).IsTemporary = true;
		Func<Task> save = () => context.SaveChangesAsync();
		await save.Should().ThrowAsync<NotSupportedException>().WithMessage("*non-temporary ID*");
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.Receipts.AnyAsync(row => row.Id == receipt.Id)).Should().BeFalse();
		(await read.AuditLogs.AnyAsync(row => row.EntityId == receipt.Id.ToString())).Should().BeFalse();
	}

	[Fact]
	public async Task AmbientTransaction_IsRejectedBeforePersistence()
	{
		ReceiptEntity receipt = Receipt();
		using (TransactionScope scope = new(TransactionScopeAsyncFlowOption.Enabled))
		{
			await using ApplicationDbContext context = fixture.CreateDbContext();
			context.Receipts.Add(receipt);
			Func<Task> save = () => context.SaveChangesAsync();
			await save.Should().ThrowAsync<NotSupportedException>().WithMessage("*ambient and enlisted*");
		}
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.Receipts.AnyAsync(row => row.Id == receipt.Id)).Should().BeFalse();
	}

	private DbContextOptions<ApplicationDbContext> Options(IInterceptor interceptor) => new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(interceptor).Options;
	private static ReceiptEntity Receipt() => new() { Location = "Atomic receipt", Date = new(2024, 1, 1) };
	private static ReceiptItemEntity Item(ReceiptEntity receipt) => new() { Receipt = receipt, Description = $"Atomic item {Guid.NewGuid():N}", Quantity = 1, UnitPrice = 1, TotalAmount = 1, Category = "Food" };
	private static AuditLogEntity SemanticAudit(Guid receiptId) => new() { Id = Guid.NewGuid(), EntityType = "Receipt", EntityId = receiptId.ToString(), Action = AuditAction.Merge, ChangedAt = DateTimeOffset.UtcNow };
	private static Task<int> Save(ApplicationDbContext context, int overload) => overload switch
	{
		0 => Task.FromResult(context.SaveChanges()),
		1 => Task.FromResult(context.SaveChanges(true)),
		2 => Task.FromResult(context.SaveChanges(false)),
		3 => context.SaveChangesAsync(),
		4 => context.SaveChangesAsync(true),
		5 => context.SaveChangesAsync(false),
		_ => throw new ArgumentOutOfRangeException(nameof(overload))
	};

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
	}

	private sealed class InsertRace(DbContextOptions<ApplicationDbContext> seedOptions, string canonical) : DbCommandInterceptor
	{
		public Guid WinnerId { get; } = Guid.NewGuid();
		public Guid LoserId { get; private set; }
		public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			if (LoserId == Guid.Empty && command.CommandText.Contains("INSERT INTO", StringComparison.Ordinal) && command.CommandText.Contains("NormalizedDescriptions", StringComparison.Ordinal))
			{
				LoserId = eventData.Context!.ChangeTracker.Entries<NormalizedDescriptionEntity>().Single(row => row.State == EntityState.Added).Entity.Id;
				await using ApplicationDbContext winner = new(seedOptions);
				winner.NormalizedDescriptions.Add(new() { Id = WinnerId, CanonicalName = canonical, CreatedAt = DateTimeOffset.UtcNow });
				await winner.SaveChangesAsync(cancellationToken);
			}
			return result;
		}
	}

	private sealed class CommandPath : DbCommandInterceptor
	{
		public int SyncWrites { get; private set; }
		public int AsyncWrites { get; private set; }
		public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
		{
			SyncWrites++;
			return result;
		}
		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			AsyncWrites++;
			return ValueTask.FromResult(result);
		}
	}

	private sealed class ReconciliationFault : DbCommandInterceptor
	{
		public bool Armed { get; set; }
		public bool Fired { get; private set; }
		public bool InsertCompleted { get; private set; }
		public CancellationTokenSource? Cancellation { get; init; }
		private void Before(DbCommand command)
		{
			if (Armed && command.CommandText.Contains("DELETE FROM \"matching\".\"DistinctDescriptions\"", StringComparison.Ordinal))
			{
				Fired = true;
				if (Cancellation is not null)
				{
					Cancellation.Cancel();
					Cancellation.Token.ThrowIfCancellationRequested();
				}
				throw new InvalidOperationException("Injected reconciliation failure");
			}
		}
		private void After(DbCommand command)
		{
			if (command.CommandText.Contains("INSERT INTO \"matching\".\"DistinctDescriptions\"", StringComparison.Ordinal))
			{
				InsertCompleted = true;
			}
		}
		public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result) { Before(command); return result; }
		public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { Before(command); return ValueTask.FromResult(result); }
		public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result) { After(command); return result; }
		public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default) { After(command); return ValueTask.FromResult(result); }
	}
}
