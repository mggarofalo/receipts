using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PurgeTrashServiceTests(PostgresFixture fixture)
{
	[Fact]
	public async Task PurgeAllDeletedAsync_RemovesSoftDeletedRowsFromEverySoftDeletableTable_AndPreservesActiveRows()
	{
		// Arrange — for every ISoftDeletable entity, seed one deleted row and
		// one active row. This guards against regression: if a new entity
		// becomes soft-deletable and the purge doesn't learn about it, this
		// test fails. It also catches the reverse case — a purge that
		// incorrectly deletes active rows.
		DateTimeOffset deletedAt = DateTimeOffset.UtcNow;

		// Parents (needed for FK-valid child inserts)
		AccountEntity account = AccountEntityGenerator.Generate();
		// Transactions default their CardId to the Account's Id
		// (TransactionEntityGenerator sets CardId = accountId when no explicit
		// cardId is given). Since RECEIPTS-574 enforced FK_Transactions_Cards_CardId,
		// that Card must exist — seed one whose Id mirrors the 1:1 Account:Card
		// backfill so the transaction inserts below satisfy the FK.
		CardEntity card = CardEntityGenerator.Generate();
		card.Id = account.Id;
		card.AccountId = account.Id;
		ReceiptEntity activeReceipt = ReceiptEntityGenerator.Generate();
		ReceiptEntity deletedReceipt = ReceiptEntityGenerator.Generate();
		deletedReceipt.DeletedAt = deletedAt;
		CategoryEntity activeCategory = CategoryEntityGenerator.Generate();
		CategoryEntity deletedCategory = CategoryEntityGenerator.Generate();
		deletedCategory.DeletedAt = deletedAt;

		// Children — most reference the active parents so FKs are satisfied.
		// `orphanedActiveSubcategory` specifically references the soft-deleted
		// Category to exercise the cascade-destruction guard: without the
		// defensive delete in TrashService, Postgres' OnDelete(Cascade) FK
		// would silently destroy this active row when its parent Category is
		// purged.
		SubcategoryEntity activeSubcategory = SubcategoryEntityGenerator.Generate();
		activeSubcategory.CategoryId = activeCategory.Id;
		SubcategoryEntity deletedSubcategory = SubcategoryEntityGenerator.Generate();
		deletedSubcategory.CategoryId = activeCategory.Id;
		deletedSubcategory.DeletedAt = deletedAt;
		SubcategoryEntity orphanedActiveSubcategory = SubcategoryEntityGenerator.Generate();
		orphanedActiveSubcategory.CategoryId = deletedCategory.Id;

		ReceiptItemEntity activeReceiptItem = ReceiptItemEntityGenerator.Generate(activeReceipt.Id);
		ReceiptItemEntity deletedReceiptItem = ReceiptItemEntityGenerator.Generate(activeReceipt.Id);
		deletedReceiptItem.DeletedAt = deletedAt;

		TransactionEntity activeTransaction = TransactionEntityGenerator.Generate(activeReceipt.Id, account.Id);
		TransactionEntity deletedTransaction = TransactionEntityGenerator.Generate(activeReceipt.Id, account.Id);
		deletedTransaction.DeletedAt = deletedAt;

		AdjustmentEntity activeAdjustment = AdjustmentEntityGenerator.Generate();
		activeAdjustment.ReceiptId = activeReceipt.Id;
		AdjustmentEntity deletedAdjustment = AdjustmentEntityGenerator.Generate();
		deletedAdjustment.ReceiptId = activeReceipt.Id;
		deletedAdjustment.DeletedAt = deletedAt;

		ItemTemplateEntity activeTemplate = ItemTemplateEntityGenerator.Generate();
		ItemTemplateEntity deletedTemplate = ItemTemplateEntityGenerator.Generate();
		deletedTemplate.DeletedAt = deletedAt;

		YnabSyncRecordEntity activeSync = YnabSyncRecordEntityGenerator.Generate(localTransactionId: activeTransaction.Id);
		YnabSyncRecordEntity deletedSync = YnabSyncRecordEntityGenerator.Generate(localTransactionId: activeTransaction.Id, syncType: Common.YnabSyncType.MemoUpdate);
		deletedSync.DeletedAt = deletedAt;

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.Accounts.Add(account);
			setup.Cards.Add(card);
			setup.Receipts.AddRange(activeReceipt, deletedReceipt);
			setup.Categories.AddRange(activeCategory, deletedCategory);
			await setup.SaveChangesAsync();

			setup.Subcategories.AddRange(activeSubcategory, deletedSubcategory, orphanedActiveSubcategory);
			setup.ReceiptItems.AddRange(activeReceiptItem, deletedReceiptItem);
			setup.Transactions.AddRange(activeTransaction, deletedTransaction);
			setup.Adjustments.AddRange(activeAdjustment, deletedAdjustment);
			setup.ItemTemplates.AddRange(activeTemplate, deletedTemplate);
			await setup.SaveChangesAsync();

			setup.YnabSyncRecords.AddRange(activeSync, deletedSync);
			await setup.SaveChangesAsync();
		}

		// Act
		await using (ApplicationDbContext act = fixture.CreateDbContext())
		{
			TrashService service = new(act);
			await service.PurgeAllDeletedAsync(CancellationToken.None);
		}

		// Assert — every soft-deleted row this test created is gone; every
		// active row survives. Assert by ID so we do not interfere with other
		// tests sharing the Postgres collection fixture.
		await using ApplicationDbContext verify = fixture.CreateDbContext();

		(await verify.Categories.IgnoreQueryFilters().AnyAsync(e => e.Id == deletedCategory.Id)).Should().BeFalse();
		(await verify.Categories.IgnoreQueryFilters().AnyAsync(e => e.Id == activeCategory.Id)).Should().BeTrue();

		(await verify.Subcategories.IgnoreQueryFilters().AnyAsync(e => e.Id == deletedSubcategory.Id)).Should().BeFalse();
		(await verify.Subcategories.IgnoreQueryFilters().AnyAsync(e => e.Id == activeSubcategory.Id)).Should().BeTrue();
		// Active subcategory whose parent Category was soft-deleted must also
		// be purged — the explicit delete prevents silent cascade destruction.
		(await verify.Subcategories.IgnoreQueryFilters().AnyAsync(e => e.Id == orphanedActiveSubcategory.Id)).Should().BeFalse();

		(await verify.Receipts.IgnoreQueryFilters().AnyAsync(e => e.Id == deletedReceipt.Id)).Should().BeFalse();
		(await verify.Receipts.IgnoreQueryFilters().AnyAsync(e => e.Id == activeReceipt.Id)).Should().BeTrue();

		(await verify.ReceiptItems.IgnoreQueryFilters().AnyAsync(e => e.Id == deletedReceiptItem.Id)).Should().BeFalse();
		(await verify.ReceiptItems.IgnoreQueryFilters().AnyAsync(e => e.Id == activeReceiptItem.Id)).Should().BeTrue();

		(await verify.Transactions.IgnoreQueryFilters().AnyAsync(e => e.Id == deletedTransaction.Id)).Should().BeFalse();
		(await verify.Transactions.IgnoreQueryFilters().AnyAsync(e => e.Id == activeTransaction.Id)).Should().BeTrue();

		(await verify.Adjustments.IgnoreQueryFilters().AnyAsync(e => e.Id == deletedAdjustment.Id)).Should().BeFalse();
		(await verify.Adjustments.IgnoreQueryFilters().AnyAsync(e => e.Id == activeAdjustment.Id)).Should().BeTrue();

		(await verify.ItemTemplates.IgnoreQueryFilters().AnyAsync(e => e.Id == deletedTemplate.Id)).Should().BeFalse();
		(await verify.ItemTemplates.IgnoreQueryFilters().AnyAsync(e => e.Id == activeTemplate.Id)).Should().BeTrue();

		(await verify.YnabSyncRecords.IgnoreQueryFilters().AnyAsync(e => e.Id == deletedSync.Id)).Should().BeFalse();
		(await verify.YnabSyncRecords.IgnoreQueryFilters().AnyAsync(e => e.Id == activeSync.Id)).Should().BeTrue();
	}
}
