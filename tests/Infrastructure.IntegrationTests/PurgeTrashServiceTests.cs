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
		// Arrange — for every ISoftDeletable entity, seed one deleted row and one active row, then
		// assert the purge removed exactly the deleted ones. It also catches the reverse case — a
		// purge that incorrectly deletes active rows.
		//
		// The entity list below is hand-enumerated, so on its own it CANNOT notice a newly
		// soft-deletable entity that the purge never learned about — it would simply keep passing.
		// EverySoftDeletableEntity_IsCoveredByThisTest at the bottom of this file is the guard that
		// makes that true: it reflects over the EF model and fails when this list falls behind.
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

		// RECEIPTS-834. Both pairs point at active receipts; only the DeletedAt stamp differs, so this
		// isolates the purge's own tombstone sweep from the Receipts FK cascade.
		ReceiptEntity pairReceiptA = ReceiptEntityGenerator.Generate();
		ReceiptEntity pairReceiptB = ReceiptEntityGenerator.Generate();
		Guid pairLow = pairReceiptA.Id < pairReceiptB.Id ? pairReceiptA.Id : pairReceiptB.Id;
		Guid pairHigh = pairReceiptA.Id < pairReceiptB.Id ? pairReceiptB.Id : pairReceiptA.Id;
		AcceptedDuplicatePairEntity activeAcceptedPair = new()
		{
			Id = Guid.NewGuid(),
			ReceiptIdA = pairLow,
			ReceiptIdB = pairHigh,
			AcceptedAt = DateTimeOffset.UtcNow,
		};
		AcceptedDuplicatePairEntity deletedAcceptedPair = new()
		{
			Id = Guid.NewGuid(),
			ReceiptIdA = pairLow,
			ReceiptIdB = activeReceipt.Id < pairLow ? pairLow : activeReceipt.Id,
			AcceptedAt = DateTimeOffset.UtcNow,
			DeletedAt = deletedAt,
		};
		deletedAcceptedPair.ReceiptIdA = activeReceipt.Id < pairLow ? activeReceipt.Id : pairLow;

		YnabSyncRecordEntity activeSync = YnabSyncRecordEntityGenerator.Generate(localTransactionId: activeTransaction.Id);
		YnabSyncRecordEntity deletedSync = YnabSyncRecordEntityGenerator.Generate(localTransactionId: activeTransaction.Id, syncType: Common.YnabSyncType.MemoUpdate);
		deletedSync.DeletedAt = deletedAt;

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.Accounts.Add(account);
			setup.Cards.Add(card);
			setup.Receipts.AddRange(activeReceipt, deletedReceipt, pairReceiptA, pairReceiptB);
			setup.Categories.AddRange(activeCategory, deletedCategory);
			await setup.SaveChangesAsync();

			setup.Subcategories.AddRange(activeSubcategory, deletedSubcategory, orphanedActiveSubcategory);
			setup.ReceiptItems.AddRange(activeReceiptItem, deletedReceiptItem);
			setup.Transactions.AddRange(activeTransaction, deletedTransaction);
			setup.Adjustments.AddRange(activeAdjustment, deletedAdjustment);
			setup.ItemTemplates.AddRange(activeTemplate, deletedTemplate);
			await setup.SaveChangesAsync();

			setup.YnabSyncRecords.AddRange(activeSync, deletedSync);
			setup.AcceptedDuplicatePairs.AddRange(activeAcceptedPair, deletedAcceptedPair);
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

		(await verify.AcceptedDuplicatePairs.IgnoreQueryFilters().AnyAsync(e => e.Id == deletedAcceptedPair.Id)).Should().BeFalse();
		(await verify.AcceptedDuplicatePairs.IgnoreQueryFilters().AnyAsync(e => e.Id == activeAcceptedPair.Id)).Should().BeTrue();

		(await verify.YnabSyncRecords.IgnoreQueryFilters().AnyAsync(e => e.Id == deletedSync.Id)).Should().BeFalse();
		(await verify.YnabSyncRecords.IgnoreQueryFilters().AnyAsync(e => e.Id == activeSync.Id)).Should().BeTrue();
	}

	[Fact]
	public async Task PurgeAllDeletedAsync_SoftDeletedTransactionWithActiveSyncRecord_PurgesBothWithoutFkViolation()
	{
		// RECEIPTS-755 regression against a real PostgreSQL instance where the
		// YnabSyncRecords -> Transactions FK is enforced as NO ACTION.
		//
		// Scenario: a synced transaction was soft-deleted while its YnabSyncRecord
		// stayed ACTIVE (DeletedAt IS NULL) — the orphan that broke Empty Trash.
		// Purging the soft-deleted transaction while an active sync record still
		// references it would throw a 23503 FK violation and roll the entire purge
		// back, permanently deadlocking Empty Trash for every trash item. The purge
		// must instead delete the orphaned active sync record first, in FK order.
		DateTimeOffset deletedAt = DateTimeOffset.UtcNow;

		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.Id = account.Id;
		card.AccountId = account.Id;
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();

		TransactionEntity deletedTransaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id);
		deletedTransaction.DeletedAt = deletedAt;

		// The orphaned ACTIVE sync record pointing at the soft-deleted transaction.
		YnabSyncRecordEntity orphanedActiveSync = YnabSyncRecordEntityGenerator.Generate(localTransactionId: deletedTransaction.Id);

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.Accounts.Add(account);
			setup.Cards.Add(card);
			setup.Receipts.Add(receipt);
			await setup.SaveChangesAsync();

			setup.Transactions.Add(deletedTransaction);
			await setup.SaveChangesAsync();

			setup.YnabSyncRecords.Add(orphanedActiveSync);
			await setup.SaveChangesAsync();
		}

		// Act — must not throw an FK violation.
		await using (ApplicationDbContext act = fixture.CreateDbContext())
		{
			TrashService service = new(act);
			Func<Task> purge = async () => await service.PurgeAllDeletedAsync(CancellationToken.None);
			await purge.Should().NotThrowAsync();
		}

		// Assert — the soft-deleted transaction and its orphaned active sync record are
		// both gone; the active parent rows survive.
		await using ApplicationDbContext verify = fixture.CreateDbContext();

		(await verify.Transactions.IgnoreQueryFilters().AnyAsync(e => e.Id == deletedTransaction.Id)).Should().BeFalse();
		(await verify.YnabSyncRecords.IgnoreQueryFilters().AnyAsync(e => e.Id == orphanedActiveSync.Id))
			.Should().BeFalse("no orphaned active sync record may survive the purge");
		(await verify.Accounts.IgnoreQueryFilters().AnyAsync(e => e.Id == account.Id)).Should().BeTrue();
		(await verify.Receipts.IgnoreQueryFilters().AnyAsync(e => e.Id == receipt.Id)).Should().BeTrue();
	}
	/// <summary>
	/// Makes the coverage claim in the test above enforceable.
	///
	/// That test seeds a hand-written list of entities. A hand-written list silently rots: add a new
	/// ISoftDeletable entity, forget to teach TrashService about it, and the suite stays green while
	/// its tombstones accumulate forever with no way to purge them. That is exactly what happened to
	/// AcceptedDuplicatePairs in RECEIPTS-834.
	///
	/// This reflects over the EF model instead, so the failure arrives at the moment the entity is
	/// added rather than whenever someone next audits the purge.
	/// </summary>
	[Fact]
	public void EverySoftDeletableEntity_IsCoveredByThisTest()
	{
		// Arrange — entities the purge test above seeds and asserts on.
		HashSet<string> coveredByTheTestAbove =
		[
			nameof(AcceptedDuplicatePairEntity),
			nameof(AdjustmentEntity),
			nameof(CategoryEntity),
			nameof(ItemTemplateEntity),
			nameof(ReceiptEntity),
			nameof(ReceiptItemEntity),
			nameof(SubcategoryEntity),
			nameof(TransactionEntity),
			nameof(YnabSyncRecordEntity),
		];

		// Act
		using ApplicationDbContext context = fixture.CreateDbContext();
		string[] softDeletable = [.. context.Model
			.GetEntityTypes()
			.Select(entityType => entityType.ClrType)
			.Where(clrType => typeof(Infrastructure.Interfaces.ISoftDeletable).IsAssignableFrom(clrType))
			.Select(clrType => clrType.Name)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)];

		// Assert
		softDeletable.Should().NotBeEmpty("the model should map at least one soft-deletable entity");
		softDeletable.Should().BeSubsetOf(
			coveredByTheTestAbove,
			"""
			every ISoftDeletable entity must be seeded (one active row + one soft-deleted row) and
			asserted in PurgeAllDeletedAsync_RemovesSoftDeletedRowsFromEverySoftDeletableTable_AndPreservesActiveRows.

			If this failed because you ADDED a soft-deletable entity: add an ExecuteDeleteAsync step for it
			in TrashService.PurgeAllDeletedAsync (in FK dependency order, children first), then seed and
			assert it in that test and list it here. Without the purge step its tombstones are unreachable
			— nothing surfaces them in the recycle bin, so they accumulate with no way to clear them.
			""");
	}
}
