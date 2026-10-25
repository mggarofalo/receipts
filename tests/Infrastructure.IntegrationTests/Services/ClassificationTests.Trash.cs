using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task PurgeRetainsHistoricalReceiptAndRestoreDependenciesWhileDeletingUnrelatedTrash()
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		Guid receiptId = await db.ReceiptItems.Where(x => x.Id == seed.Item).Select(x => x.ReceiptId).SingleAsync();
		var account = AccountEntityGenerator.Generate();
		var card = CardEntityGenerator.Generate(); card.AccountId = account.Id;
		var payment = TransactionEntityGenerator.Generate(receiptId, account.Id, card.Id);
		var adjustment = AdjustmentEntityGenerator.Generate(); adjustment.ReceiptId = receiptId;
		var sync = new YnabSyncRecordEntity { Id = Guid.NewGuid(), LocalTransactionId = payment.Id, YnabBudgetId = "trash-history", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
		var unrelated = ReceiptEntityGenerator.Generate();
		db.AddRange(account, card, payment, adjustment, sync, unrelated);
		await db.SaveChangesAsync();
		var repository = new ReceiptRepository(new ActorFactory(fixture));
		// A separately deleted historical item must protect its parent as well.
		await new ReceiptItemRepository(new ActorFactory(fixture)).DeleteAsync([seed.Item], default);
		await repository.DeleteAsync([receiptId, unrelated.Id], default);
		var retained = await new TrashService(db).PurgeAllDeletedAsync(default);
		retained.RetainedReceipts.Should().BeGreaterThanOrEqualTo(1);
		retained.RetainedItems.Should().BeGreaterThanOrEqualTo(1);
		retained.Reason.Should().Contain("history");
		db.ChangeTracker.Clear();
		(await db.Receipts.IgnoreQueryFilters().AnyAsync(x => x.Id == unrelated.Id)).Should().BeFalse();
		(await db.Classifications.AnyAsync(x => x.Id == seed.Assignment)).Should().BeTrue();
		(await db.Transactions.IgnoreQueryFilters().AnyAsync(x => x.Id == payment.Id)).Should().BeTrue();
		(await db.Adjustments.IgnoreQueryFilters().AnyAsync(x => x.Id == adjustment.Id)).Should().BeTrue();
		(await db.YnabSyncRecords.IgnoreQueryFilters().AnyAsync(x => x.Id == sync.Id)).Should().BeTrue();
		await repository.RestoreAsync(receiptId, default);
		await new ReceiptItemRepository(new ActorFactory(fixture)).RestoreAsync(seed.Item, default);
		(await db.Receipts.AnyAsync(x => x.Id == receiptId)).Should().BeTrue();
		(await db.ReceiptItems.AnyAsync(x => x.Id == seed.Item)).Should().BeTrue();
		(await db.Transactions.AnyAsync(x => x.Id == payment.Id && x.CardId == card.Id)).Should().BeTrue();
		(await db.Cards.AnyAsync(x => x.Id == card.Id && x.AccountId == account.Id)).Should().BeTrue();
		(await db.Adjustments.AnyAsync(x => x.Id == adjustment.Id)).Should().BeTrue();
		(await db.YnabSyncRecords.AnyAsync(x => x.Id == sync.Id)).Should().BeTrue();
		(await Service().HistoryAsync(seed.Item, null, 10)).Items.Should().Contain(x => x.Id == seed.Assignment);
	}
}
