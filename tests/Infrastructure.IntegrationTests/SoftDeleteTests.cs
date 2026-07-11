using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Extensions;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SoftDeleteTests(PostgresFixture fixture)
{
	[Fact]
	public async Task SoftDelete_Receipt_SetsDeletedAt()
	{
		// Arrange
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();

		// Act — remove triggers soft delete via HandleSoftDelete()
		context.Receipts.Remove(receipt);
		await context.SaveChangesAsync();

		// Assert — query filter hides soft-deleted entities
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		ReceiptEntity? hidden = await readContext.Receipts.FirstOrDefaultAsync(r => r.Id == receipt.Id);
		hidden.Should().BeNull("query filter should exclude soft-deleted receipts");

		// Assert — IgnoreQueryFilters reveals the soft-deleted entity
		ReceiptEntity? deleted = await readContext.Receipts
			.IgnoreQueryFilters()
			.FirstOrDefaultAsync(r => r.Id == receipt.Id);
		deleted.Should().NotBeNull();
		deleted!.DeletedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task SoftDelete_Receipt_CascadesToTransactions()
	{
		// Arrange — receipt with a child transaction, saved on one context.
		// Transaction now requires both AccountId and CardId FKs (RECEIPTS-574/575).
		Guid receiptId;
		Guid transactionId;
		{
			await using ApplicationDbContext setupContext = fixture.CreateDbContext();
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			AccountEntity account = AccountEntityGenerator.Generate();
			CardEntity card = CardEntityGenerator.Generate();
			card.AccountId = account.Id;
			setupContext.Receipts.Add(receipt);
			setupContext.Accounts.Add(account);
			setupContext.Cards.Add(card);
			await setupContext.SaveChangesAsync();

			TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id, card.Id);
			setupContext.Transactions.Add(transaction);
			await setupContext.SaveChangesAsync();

			receiptId = receipt.Id;
			transactionId = transaction.Id;
		}

		// Act — soft-delete the receipt after loading owned children so the
		// cascade in HandleSoftDelete can see them (matches ReceiptRepository.DeleteAsync).
		{
			await using ApplicationDbContext deleteContext = fixture.CreateDbContext();
			ReceiptEntity receiptToDelete = await deleteContext.Receipts.FirstAsync(r => r.Id == receiptId);
			await deleteContext.Transactions.IgnoreAutoIncludes()
				.Where(t => t.ReceiptId == receiptId).LoadAsync();
			deleteContext.Receipts.Remove(receiptToDelete);
			await deleteContext.SaveChangesAsync();
		}

		// Assert — transaction should also be soft-deleted (cascade)
		await using ApplicationDbContext readContext = fixture.CreateDbContext();

		TransactionEntity? hiddenTx = await readContext.Transactions
			.FirstOrDefaultAsync(t => t.Id == transactionId);
		hiddenTx.Should().BeNull("query filter should exclude cascade-soft-deleted transactions");

		TransactionEntity? deletedTx = await readContext.Transactions
			.IgnoreQueryFilters()
			.FirstOrDefaultAsync(t => t.Id == transactionId);
		deletedTx.Should().NotBeNull();
		deletedTx!.DeletedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task SoftDelete_Receipt_CascadesToReceiptItems()
	{
		// Arrange — receipt with a child receipt item, saved on one context
		Guid receiptId;
		Guid itemId;
		{
			await using ApplicationDbContext setupContext = fixture.CreateDbContext();
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			setupContext.Receipts.Add(receipt);
			await setupContext.SaveChangesAsync();

			ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
			setupContext.ReceiptItems.Add(item);
			await setupContext.SaveChangesAsync();

			receiptId = receipt.Id;
			itemId = item.Id;
		}

		// Act — soft-delete the receipt after loading owned children so the
		// cascade in HandleSoftDelete can see them (matches ReceiptRepository.DeleteAsync).
		{
			await using ApplicationDbContext deleteContext = fixture.CreateDbContext();
			ReceiptEntity receiptToDelete = await deleteContext.Receipts.FirstAsync(r => r.Id == receiptId);
			await deleteContext.ReceiptItems.IgnoreAutoIncludes()
				.Where(i => i.ReceiptId == receiptId).LoadAsync();
			deleteContext.Receipts.Remove(receiptToDelete);
			await deleteContext.SaveChangesAsync();
		}

		// Assert — receipt item should be cascade soft-deleted
		await using ApplicationDbContext readContext = fixture.CreateDbContext();

		ReceiptItemEntity? hiddenItem = await readContext.ReceiptItems
			.FirstOrDefaultAsync(i => i.Id == itemId);
		hiddenItem.Should().BeNull("query filter should exclude cascade-soft-deleted receipt items");

		ReceiptItemEntity? deletedItem = await readContext.ReceiptItems
			.IgnoreQueryFilters()
			.FirstOrDefaultAsync(i => i.Id == itemId);
		deletedItem.Should().NotBeNull();
		deletedItem!.DeletedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task SoftDelete_Receipt_CascadesToAdjustments()
	{
		// Arrange — receipt with a child adjustment, saved on one context
		Guid receiptId;
		Guid adjustmentId;
		{
			await using ApplicationDbContext setupContext = fixture.CreateDbContext();
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			setupContext.Receipts.Add(receipt);
			await setupContext.SaveChangesAsync();

			AdjustmentEntity adjustment = AdjustmentEntityGenerator.Generate();
			adjustment.ReceiptId = receipt.Id;
			setupContext.Adjustments.Add(adjustment);
			await setupContext.SaveChangesAsync();

			receiptId = receipt.Id;
			adjustmentId = adjustment.Id;
		}

		// Act — soft-delete the receipt after loading owned children so the
		// cascade in HandleSoftDelete can see them (matches ReceiptRepository.DeleteAsync).
		{
			await using ApplicationDbContext deleteContext = fixture.CreateDbContext();
			ReceiptEntity receiptToDelete = await deleteContext.Receipts.FirstAsync(r => r.Id == receiptId);
			await deleteContext.Adjustments.IgnoreAutoIncludes()
				.Where(a => a.ReceiptId == receiptId).LoadAsync();
			deleteContext.Receipts.Remove(receiptToDelete);
			await deleteContext.SaveChangesAsync();
		}

		// Assert — adjustment should be cascade soft-deleted
		await using ApplicationDbContext readContext = fixture.CreateDbContext();

		AdjustmentEntity? hiddenAdj = await readContext.Adjustments
			.FirstOrDefaultAsync(a => a.Id == adjustmentId);
		hiddenAdj.Should().BeNull("query filter should exclude cascade-soft-deleted adjustments");

		AdjustmentEntity? deletedAdj = await readContext.Adjustments
			.IgnoreQueryFilters()
			.FirstOrDefaultAsync(a => a.Id == adjustmentId);
		deletedAdj.Should().NotBeNull();
		deletedAdj!.DeletedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task QueryFilter_ExcludesSoftDeletedEntities_InLinqQueries()
	{
		// Arrange — create two receipts, soft-delete one
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity activeReceipt = ReceiptEntityGenerator.Generate();
		ReceiptEntity deletedReceipt = ReceiptEntityGenerator.Generate();

		context.Receipts.AddRange(activeReceipt, deletedReceipt);
		await context.SaveChangesAsync();

		context.Receipts.Remove(deletedReceipt);
		await context.SaveChangesAsync();

		// Act — query with filter
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		List<ReceiptEntity> filtered = await readContext.Receipts
			.Where(r => r.Id == activeReceipt.Id || r.Id == deletedReceipt.Id)
			.ToListAsync();

		// Assert — only the active receipt should appear
		filtered.Should().ContainSingle()
			.Which.Id.Should().Be(activeReceipt.Id);
	}

	[Fact]
	public async Task OnlyDeleted_ReturnsOnlySoftDeletedEntities()
	{
		// Arrange — create a receipt and soft-delete it
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();

		context.Receipts.Remove(receipt);
		await context.SaveChangesAsync();

		// Act
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		ReceiptEntity? onlyDeleted = await readContext.Receipts
			.OnlyDeleted()
			.FirstOrDefaultAsync(r => r.Id == receipt.Id);

		// Assert
		onlyDeleted.Should().NotBeNull();
		onlyDeleted!.DeletedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task SoftDelete_Transaction_CascadesToYnabSyncRecords()
	{
		// Arrange — transaction with a child YnabSyncRecord.
		// Transaction now requires both AccountId and CardId FKs (RECEIPTS-574/575).
		Guid transactionId;
		Guid syncRecordId;
		{
			await using ApplicationDbContext setupContext = fixture.CreateDbContext();
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			AccountEntity account = AccountEntityGenerator.Generate();
			CardEntity card = CardEntityGenerator.Generate();
			card.AccountId = account.Id;
			setupContext.Receipts.Add(receipt);
			setupContext.Accounts.Add(account);
			setupContext.Cards.Add(card);
			await setupContext.SaveChangesAsync();

			TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id, card.Id);
			setupContext.Transactions.Add(transaction);
			await setupContext.SaveChangesAsync();

			YnabSyncRecordEntity syncRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transaction.Id);
			setupContext.YnabSyncRecords.Add(syncRecord);
			await setupContext.SaveChangesAsync();

			transactionId = transaction.Id;
			syncRecordId = syncRecord.Id;
		}

		// Act — soft-delete the transaction after loading owned children so the
		// cascade in HandleSoftDelete can see them.
		{
			await using ApplicationDbContext deleteContext = fixture.CreateDbContext();
			TransactionEntity txToDelete = await deleteContext.Transactions.FirstAsync(t => t.Id == transactionId);
			await deleteContext.YnabSyncRecords.IgnoreAutoIncludes()
				.Where(s => s.LocalTransactionId == transactionId).LoadAsync();
			deleteContext.Transactions.Remove(txToDelete);
			await deleteContext.SaveChangesAsync();
		}

		// Assert — YnabSyncRecord should also be cascade soft-deleted
		await using ApplicationDbContext readContext = fixture.CreateDbContext();

		YnabSyncRecordEntity? hiddenRecord = await readContext.YnabSyncRecords
			.FirstOrDefaultAsync(s => s.Id == syncRecordId);
		hiddenRecord.Should().BeNull("query filter should exclude cascade-soft-deleted sync records");

		YnabSyncRecordEntity? deletedRecord = await readContext.YnabSyncRecords
			.IgnoreQueryFilters()
			.FirstOrDefaultAsync(s => s.Id == syncRecordId);
		deletedRecord.Should().NotBeNull();
		deletedRecord!.DeletedAt.Should().NotBeNull();
		deletedRecord.CascadeDeletedByParentId.Should().Be(transactionId);
	}

	[Fact]
	public async Task SoftDelete_Receipt_CascadesToAllOwnedChildrenAndGrandchildren_InOneSave()
	{
		// Regression for the de-quadratic soft-delete cascade rewrite (the cascade is the
		// correctness path RECEIPTS-755 depends on): deleting a single receipt that owns multiple
		// children of every owned type — plus a grandchild owned by one of those children
		// (Transaction -> YnabSyncRecord) — must still soft-delete the entire graph in one save
		// and stamp each cascade target with its DIRECT parent id.
		Guid receiptId;
		Guid[] itemIds;
		Guid transactionId;
		Guid adjustmentId;
		Guid syncRecordId;
		{
			await using ApplicationDbContext setupContext = fixture.CreateDbContext();
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			AccountEntity account = AccountEntityGenerator.Generate();
			CardEntity card = CardEntityGenerator.Generate();
			card.AccountId = account.Id;
			setupContext.Receipts.Add(receipt);
			setupContext.Accounts.Add(account);
			setupContext.Cards.Add(card);
			await setupContext.SaveChangesAsync();

			ReceiptItemEntity item1 = ReceiptItemEntityGenerator.Generate(receipt.Id);
			ReceiptItemEntity item2 = ReceiptItemEntityGenerator.Generate(receipt.Id);
			TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id, card.Id);
			AdjustmentEntity adjustment = AdjustmentEntityGenerator.Generate();
			adjustment.ReceiptId = receipt.Id;
			setupContext.ReceiptItems.AddRange(item1, item2);
			setupContext.Transactions.Add(transaction);
			setupContext.Adjustments.Add(adjustment);
			await setupContext.SaveChangesAsync();

			YnabSyncRecordEntity syncRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transaction.Id);
			setupContext.YnabSyncRecords.Add(syncRecord);
			await setupContext.SaveChangesAsync();

			receiptId = receipt.Id;
			itemIds = [item1.Id, item2.Id];
			transactionId = transaction.Id;
			adjustmentId = adjustment.Id;
			syncRecordId = syncRecord.Id;
		}

		// Act — load the whole owned graph into the tracker, then remove only the receipt.
		{
			await using ApplicationDbContext deleteContext = fixture.CreateDbContext();
			ReceiptEntity receiptToDelete = await deleteContext.Receipts.FirstAsync(r => r.Id == receiptId);
			await deleteContext.ReceiptItems.IgnoreAutoIncludes().Where(i => i.ReceiptId == receiptId).LoadAsync();
			await deleteContext.Adjustments.IgnoreAutoIncludes().Where(a => a.ReceiptId == receiptId).LoadAsync();
			await deleteContext.Transactions.IgnoreAutoIncludes().Where(t => t.ReceiptId == receiptId).LoadAsync();
			await deleteContext.YnabSyncRecords.IgnoreAutoIncludes().Where(s => s.LocalTransactionId == transactionId).LoadAsync();
			deleteContext.Receipts.Remove(receiptToDelete);
			await deleteContext.SaveChangesAsync();
		}

		// Assert — every child and the grandchild is soft-deleted, stamped with its DIRECT parent.
		await using ApplicationDbContext readContext = fixture.CreateDbContext();

		foreach (Guid itemId in itemIds)
		{
			ReceiptItemEntity? item = await readContext.ReceiptItems.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == itemId);
			item.Should().NotBeNull();
			item!.DeletedAt.Should().NotBeNull();
			item.CascadeDeletedByParentId.Should().Be(receiptId);
		}

		AdjustmentEntity? adj = await readContext.Adjustments.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == adjustmentId);
		adj.Should().NotBeNull();
		adj!.DeletedAt.Should().NotBeNull();
		adj.CascadeDeletedByParentId.Should().Be(receiptId);

		TransactionEntity? tx = await readContext.Transactions.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == transactionId);
		tx.Should().NotBeNull();
		tx!.DeletedAt.Should().NotBeNull();
		tx.CascadeDeletedByParentId.Should().Be(receiptId);

		// Grandchild: owned by the Transaction, so its parent stamp is the transaction id.
		YnabSyncRecordEntity? sync = await readContext.YnabSyncRecords.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == syncRecordId);
		sync.Should().NotBeNull();
		sync!.DeletedAt.Should().NotBeNull();
		sync.CascadeDeletedByParentId.Should().Be(transactionId);
	}

	[Fact]
	public async Task SoftDelete_YnabSyncRecord_AllowsReCreationAfterSoftDelete()
	{
		// Arrange — this test validates the filtered unique index on (LocalTransactionId, SyncType)
		// against a real PostgreSQL instance where unique indexes are enforced.
		// Transaction now requires both AccountId and CardId FKs (RECEIPTS-574/575).
		Guid transactionId;
		{
			await using ApplicationDbContext setupContext = fixture.CreateDbContext();
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			AccountEntity account = AccountEntityGenerator.Generate();
			CardEntity card = CardEntityGenerator.Generate();
			card.AccountId = account.Id;
			setupContext.Receipts.Add(receipt);
			setupContext.Accounts.Add(account);
			setupContext.Cards.Add(card);
			await setupContext.SaveChangesAsync();

			TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id, card.Id);
			setupContext.Transactions.Add(transaction);
			await setupContext.SaveChangesAsync();

			transactionId = transaction.Id;
		}

		// Create and soft-delete a sync record
		{
			await using ApplicationDbContext context = fixture.CreateDbContext();
			YnabSyncRecordEntity syncRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transactionId);
			context.YnabSyncRecords.Add(syncRecord);
			await context.SaveChangesAsync();

			context.YnabSyncRecords.Remove(syncRecord);
			await context.SaveChangesAsync();
		}

		// Act — create a new sync record with the same (LocalTransactionId, SyncType)
		// This should succeed because the filtered unique index excludes soft-deleted rows
		{
			await using ApplicationDbContext context = fixture.CreateDbContext();
			YnabSyncRecordEntity newRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transactionId);
			context.YnabSyncRecords.Add(newRecord);

			// This would throw if the unique index is not filtered on DeletedAt IS NULL
			Func<Task> act = async () => await context.SaveChangesAsync();
			await act.Should().NotThrowAsync();
		}
	}
}
