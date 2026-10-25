using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

internal sealed class TrashRetentionFactory(PostgresFixture fixture, Func<ApplicationDbContext>? create = null)
	: IDbContextFactory<ApplicationDbContext>
{
	public ApplicationDbContext CreateDbContext() => create?.Invoke() ?? fixture.CreateDbContext();
}

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class PaymentOnlyTrashRetentionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Fact]
	public async Task DeletedPaymentAndAdjustmentOnActiveHistoricalReceiptReportRetentionWithoutReceiptOrItemTrash()
	{
		await using var db = fixture.CreateDbContext();
		var receipt = ReceiptEntityGenerator.Generate();
		var item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		var account = AccountEntityGenerator.Generate();
		var card = CardEntityGenerator.Generate(); card.AccountId = account.Id;
		var payment = TransactionEntityGenerator.Generate(receipt.Id, account.Id, card.Id);
		var adjustment = AdjustmentEntityGenerator.Generate(); adjustment.ReceiptId = receipt.Id;
		db.AddRange(receipt, item, account, card, payment, adjustment);
		await db.SaveChangesAsync();
		var migration = new LegacyTaxonomyMigrationService(new TrashRetentionFactory(fixture));
		var plan = await migration.PreflightAsync(new("payment-trash", Guid.NewGuid(), "maintenance-admin", DateTimeOffset.UtcNow));
		await migration.ApplyAsync(LegacyMigrationFreeze.Source(plan));
		DateTimeOffset deletedAt = DateTimeOffset.UtcNow;
		await db.Transactions.Where(x => x.Id == payment.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, deletedAt));
		await db.Adjustments.Where(x => x.Id == adjustment.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, deletedAt));

		var result = await new TrashService(db).PurgeAllDeletedAsync(default);

		result.RetainedReceipts.Should().Be(0);
		result.RetainedItems.Should().Be(0);
		result.RetainedRecords.Should().Be(2);
		result.Reason.Should().Contain("history");
		db.ChangeTracker.Clear();
		(await db.Receipts.AnyAsync(x => x.Id == receipt.Id)).Should().BeTrue();
		(await db.ReceiptItems.AnyAsync(x => x.Id == item.Id)).Should().BeTrue();
		(await db.Transactions.IgnoreQueryFilters().SingleAsync(x => x.Id == payment.Id)).DeletedAt.Should().NotBeNull();
		(await db.Adjustments.IgnoreQueryFilters().SingleAsync(x => x.Id == adjustment.Id)).DeletedAt.Should().NotBeNull();
	}
}

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class MigratedTrashRetentionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	private sealed class PausedMigrationContext(DbContextOptions<ApplicationDbContext> options, Func<Task> afterSave)
		: ApplicationDbContext(options)
	{
		private bool observed;
		public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
		{
			int result = await base.SaveChangesAsync(cancellationToken);
			if (!observed) { observed = true; await afterSave(); }
			return result;
		}
	}

	[Fact]
	public async Task PurgeWaitsForInitialMigrationThenRetainsPreexistingTrashAndTransitiveDuplicateDependencies()
	{
		await using var db = fixture.CreateDbContext();
		var historical = ReceiptEntityGenerator.Generate();
		var item = ReceiptItemEntityGenerator.Generate(historical.Id);
		var middle = ReceiptEntityGenerator.Generate();
		var last = ReceiptEntityGenerator.Generate();
		var unrelated = ReceiptEntityGenerator.Generate();
		var account = AccountEntityGenerator.Generate();
		var card = CardEntityGenerator.Generate(); card.AccountId = account.Id;
		var payment = TransactionEntityGenerator.Generate(last.Id, account.Id, card.Id);
		var adjustment = AdjustmentEntityGenerator.Generate(); adjustment.ReceiptId = last.Id;
		var firstPair = Pair(historical.Id, middle.Id);
		var secondPair = Pair(middle.Id, last.Id);
		db.AddRange(historical, item, middle, last, unrelated, account, card, payment, adjustment, firstPair, secondPair);
		await db.SaveChangesAsync();
		var repository = new ReceiptRepository(new TrashRetentionFactory(fixture));
		await repository.DeleteAsync([historical.Id, middle.Id, last.Id, unrelated.Id], default);
		// A removed annotation must still carry the dependency closure.
		await db.AcceptedDuplicatePairs.Where(x => x.Id == firstPair.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow));
		var sourceService = new LegacyTaxonomyMigrationService(new TrashRetentionFactory(fixture));
		var plan = await sourceService.PreflightAsync(new("preexisting-trash", Guid.NewGuid(), "maintenance-admin", DateTimeOffset.UtcNow));
		plan.Counts.TrashedItems.Should().Be(1);
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var migration = new LegacyTaxonomyMigrationService(new TrashRetentionFactory(fixture,
			() => new PausedMigrationContext(fixture.CreateOptions(), async () =>
			{
				entered.TrySetResult();
				await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
			})));
		Task<LegacyMigrationPlan> apply = migration.ApplyAsync(LegacyMigrationFreeze.Source(plan));
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
		await using var purgeDb = fixture.CreateDbContext();
		await purgeDb.Database.OpenConnectionAsync();
		int purgePid = ((NpgsqlConnection)purgeDb.Database.GetDbConnection()).ProcessID;
		Task<Application.Interfaces.Services.PurgeTrashResult> purge = new TrashService(purgeDb).PurgeAllDeletedAsync(default);
		try
		{
			using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
			// Observe the actual PostgreSQL advisory wait before releasing migration.
			while (!await db.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {purgePid} AND wait_event_type = 'Lock' AND wait_event = 'advisory') AS \"Value\"").SingleAsync(deadline.Token))
			{
				purge.IsCompleted.Should().BeFalse("purge must wait for migration's normalization gate");
			}
			purge.IsCompleted.Should().BeFalse();
		}
		finally { release.TrySetResult(); }
		await apply;
		var result = await purge;
		result.RetainedReceipts.Should().Be(3);
		result.RetainedItems.Should().Be(1);
		db.ChangeTracker.Clear();
		(await db.Receipts.IgnoreQueryFilters().AnyAsync(x => x.Id == unrelated.Id)).Should().BeFalse();
		(await db.Receipts.IgnoreQueryFilters().CountAsync(x => x.Id == historical.Id || x.Id == middle.Id || x.Id == last.Id)).Should().Be(3);
		(await db.AcceptedDuplicatePairs.IgnoreQueryFilters().CountAsync(x => x.Id == firstPair.Id || x.Id == secondPair.Id)).Should().Be(2);
		var assignment = await db.Classifications.SingleAsync(x => x.ReceiptItemId == item.Id);
		assignment.Source.Should().Be("legacy_import");
		(await db.ReceiptItems.IgnoreQueryFilters().SingleAsync(x => x.Id == item.Id)).DeletedAt.Should().NotBeNull();
		await repository.RestoreAsync(historical.Id, default);
		await repository.RestoreAsync(last.Id, default);
		(await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == item.Id)).TotalAmount.Should().Be(item.TotalAmount);
		(await db.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == item.Id)).AssignmentId.Should().Be(assignment.Id);
		(await db.Transactions.AnyAsync(x => x.Id == payment.Id && x.CardId == card.Id)).Should().BeTrue();
		(await db.Cards.AnyAsync(x => x.Id == card.Id && x.AccountId == account.Id)).Should().BeTrue();
		(await db.Adjustments.AnyAsync(x => x.Id == adjustment.Id)).Should().BeTrue();
	}

	private static AcceptedDuplicatePairEntity Pair(Guid first, Guid second) => new()
	{
		Id = Guid.NewGuid(),
		ReceiptIdA = first.CompareTo(second) < 0 ? first : second,
		ReceiptIdB = first.CompareTo(second) < 0 ? second : first,
		AcceptedAt = DateTimeOffset.UtcNow,
	};
}
