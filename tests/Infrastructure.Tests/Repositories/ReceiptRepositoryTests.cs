using Application.Models;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.Tests.Repositories;

public class ReceiptRepositoryTests
{
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

	[Fact]
	public async Task GetByIdAsync_ExistingId_ReturnsReceipt()
	{
		// Arrange
		const int expectedCount = 1;
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		ReceiptEntity entity = ReceiptEntityGenerator.Generate();
		await context.Receipts.AddAsync(entity);
		await context.SaveChangesAsync(CancellationToken.None);
		(await context.Receipts.CountAsync()).Should().Be(expectedCount);

		ReceiptRepository repository = new(_contextFactory);

		// Act
		ReceiptEntity? actual = await repository.GetByIdAsync(entity.Id, CancellationToken.None);

		// Assert
		Assert.NotNull(actual);
		actual.Should().BeEquivalentTo(entity);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetByIdAsync_NonExistingId_ReturnsNull()
	{
		// Arrange
		const int expectedCount = 0;
		ReceiptRepository repository = new(_contextFactory);
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		(await context.Receipts.CountAsync()).Should().Be(expectedCount);

		// Act
		ReceiptEntity? result = await repository.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

		// Assert
		Assert.Null(result);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAllAsync_ReturnsAllReceipts()
	{
		// Arrange
		const int expectedReceiptCount = 2;
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		List<ReceiptEntity> entities = ReceiptEntityGenerator.GenerateList(expectedReceiptCount);
		await context.Receipts.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);
		(await context.Receipts.CountAsync()).Should().Be(expectedReceiptCount);

		ReceiptRepository repository = new(_contextFactory);

		// Act
		List<ReceiptEntity> actual = await repository.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		actual.Should().BeEquivalentTo(entities);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetListAsync_PopulatedReceipt_ProjectsTotalsBalanceAndConciseSummaries()
	{
		// Arrange
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.TaxAmount = 1.005m;

		string[] categories = ["Snacks", "Bakery", "Produce", "Dairy", "  Bakery  ", "   "];
		decimal[] itemAmounts = [1m, 2m, 3m, 4m, 0m, 0m];
		List<ReceiptItemEntity> items = [.. categories.Select((category, index) =>
		{
			ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
			item.Category = category;
			item.TotalAmount = itemAmounts[index];
			return item;
		})];

		AdjustmentEntity adjustment = AdjustmentEntityGenerator.Generate();
		adjustment.ReceiptId = receipt.Id;
		adjustment.Amount = 2.25m;

		AccountEntity account = AccountEntityGenerator.Generate();
		account.Name = "Checking";
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		card.Name = "Visa 4321";
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id, card.Id);
		transaction.Amount = 13.26m;

		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			context.AddRange(receipt, account, card, adjustment, transaction);
			context.ReceiptItems.AddRange(items);
			await context.SaveChangesAsync();
		}

		ReceiptRepository repository = new(_contextFactory);

		// Act
		ReceiptListItem actual = (await repository.GetListAsync(
			0, 50, SortParams.Default, null, null, null, null, CancellationToken.None)).Single();

		// Assert
		actual.ItemSubtotal.Should().Be(10m);
		actual.AdjustmentTotal.Should().Be(2.25m);
		actual.ExpectedTotal.Should().Be(13.26m, "money totals round midpoint values away from zero to two decimals");
		actual.TransactionTotal.Should().Be(13.26m);
		actual.BalanceState.Should().Be("balanced");
		actual.ItemCount.Should().Be(6);
		actual.CategorySummary.Should().Be("Bakery, Dairy, Produce +1");
		actual.PaymentSummary.Should().Be("Checking · Visa 4321");

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetListAsync_ReceiptWithoutRelatedRows_ReturnsDeterministicEmptyMetadata()
	{
		// Arrange
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.TaxAmount = 1.23m;
		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			context.Receipts.Add(receipt);
			await context.SaveChangesAsync();
		}

		ReceiptRepository repository = new(_contextFactory);

		// Act
		ReceiptListItem actual = (await repository.GetListAsync(
			0, 50, SortParams.Default, null, null, null, null, CancellationToken.None)).Single();

		// Assert
		actual.ItemSubtotal.Should().Be(0m);
		actual.AdjustmentTotal.Should().Be(0m);
		actual.ExpectedTotal.Should().Be(1.23m);
		actual.TransactionTotal.Should().Be(0m);
		actual.BalanceState.Should().Be("no-transactions");
		actual.ItemCount.Should().Be(0);
		actual.CategorySummary.Should().BeEmpty();
		actual.PaymentSummary.Should().BeEmpty();

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetListAsync_TransactionThatDoesNotMatchExpectedTotal_IsOutOfBalance()
	{
		// Arrange
		ReceiptEntity receipt = CreateReceipt("Mismatch", 1m);
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id, card.Id);
		transaction.Amount = 99m;

		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			context.AddRange(receipt, account, card, transaction);
			context.ReceiptItems.Add(CreateItem(receipt.Id, 9m));
			await context.SaveChangesAsync();
		}

		ReceiptRepository repository = new(_contextFactory);

		// Act
		ReceiptListItem actual = (await repository.GetListAsync(
			0, 50, SortParams.Default, null, null, null, null, CancellationToken.None)).Single();

		// Assert
		actual.ExpectedTotal.Should().Be(10m);
		actual.TransactionTotal.Should().Be(99m);
		actual.BalanceState.Should().Be("out-of-balance");

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetListAsync_ExpectedTotalSortAndPagination_AreAppliedToAggregateValues()
	{
		// Arrange
		ReceiptEntity low = CreateReceipt("Low", taxAmount: 1m);
		ReceiptEntity middle = CreateReceipt("Middle", taxAmount: 2m);
		ReceiptEntity high = CreateReceipt("High", taxAmount: 3m);

		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			context.Receipts.AddRange(low, middle, high);
			context.ReceiptItems.AddRange(
				CreateItem(low.Id, 4m),
				CreateItem(middle.Id, 8m),
				CreateItem(high.Id, 17m));
			await context.SaveChangesAsync();
		}

		ReceiptRepository repository = new(_contextFactory);

		// Act
		List<ReceiptListItem> page = await repository.GetListAsync(
			1, 1, new SortParams("expectedTotal", "asc"), null, null, null, null, CancellationToken.None);

		// Assert
		page.Should().ContainSingle();
		page[0].Id.Should().Be(middle.Id);
		page[0].ExpectedTotal.Should().Be(10m);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetListAsync_ExistingFilters_RestrictProjectionBeforePagination()
	{
		// Arrange
		AccountEntity wantedAccount = AccountEntityGenerator.Generate();
		CardEntity wantedCard = CardEntityGenerator.Generate();
		wantedCard.AccountId = wantedAccount.Id;
		AccountEntity otherAccount = AccountEntityGenerator.Generate();
		CardEntity otherCard = CardEntityGenerator.Generate();
		otherCard.AccountId = otherAccount.Id;

		ReceiptEntity wanted = CreateReceipt("Target", 0m);
		ReceiptEntity wrongCard = CreateReceipt("Target", 0m);
		ReceiptEntity wrongLocation = CreateReceipt("Other", 0m);

		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			context.AddRange(wantedAccount, wantedCard, otherAccount, otherCard, wanted, wrongCard, wrongLocation);
			context.Transactions.AddRange(
				TransactionEntityGenerator.Generate(wanted.Id, wantedAccount.Id, wantedCard.Id),
				TransactionEntityGenerator.Generate(wrongCard.Id, otherAccount.Id, otherCard.Id),
				TransactionEntityGenerator.Generate(wrongLocation.Id, wantedAccount.Id, wantedCard.Id));
			await context.SaveChangesAsync();
		}

		ReceiptRepository repository = new(_contextFactory);

		// Act
		List<ReceiptListItem> result = await repository.GetListAsync(
			0, 1, SortParams.Default, wantedAccount.Id, wantedCard.Id, null, "Target", CancellationToken.None);

		// Assert
		result.Should().ContainSingle();
		result[0].Id.Should().Be(wanted.Id);

		_contextFactory.ResetDatabase();
	}

	private static ReceiptEntity CreateReceipt(string location, decimal taxAmount)
	{
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.Location = location;
		receipt.TaxAmount = taxAmount;
		return receipt;
	}

	private static ReceiptItemEntity CreateItem(Guid receiptId, decimal totalAmount)
	{
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receiptId);
		item.TotalAmount = totalAmount;
		return item;
	}

	[Fact]
	public async Task CreateAsync_ValidReceipts_ReturnsCreatedReceipts()
	{
		// Arrange
		const int expectedReceiptCount = 2;
		List<ReceiptEntity> entities = ReceiptEntityGenerator.GenerateList(expectedReceiptCount);
		entities.ForEach(e => e.Id = Guid.Empty);
		ReceiptRepository repository = new(_contextFactory);

		// Act
		List<ReceiptEntity> actual = await repository.CreateAsync(entities, CancellationToken.None);

		// Assert
		Assert.All(actual, r =>
		{
			Assert.NotEqual(Guid.Empty, r.Id);
		});

		actual.Should().BeEquivalentTo(entities, opt => opt.Excluding(x => x.Id));

		using ApplicationDbContext verifyContext = _contextFactory.CreateDbContext();
		(await verifyContext.Receipts.CountAsync()).Should().Be(expectedReceiptCount);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task UpdateAsync_ValidReceipt_UpdatesReceipt()
	{
		// Arrange
		const int expectedReceiptCount = 2;
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		List<ReceiptEntity> entities = ReceiptEntityGenerator.GenerateList(expectedReceiptCount);
		await context.Receipts.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);
		(await context.Receipts.CountAsync()).Should().Be(expectedReceiptCount);

		ReceiptRepository repository = new(_contextFactory);

		// Modify receipt
		entities.ForEach(e =>
		{
			e.TaxAmount += 1.0m;
		});

		// Act
		await repository.UpdateAsync(entities, CancellationToken.None);

		using ApplicationDbContext verifyContext = _contextFactory.CreateDbContext();
		List<ReceiptEntity> updatedEntities = await verifyContext.Receipts.ToListAsync();

		// Assert
		updatedEntities.Should().BeEquivalentTo(entities);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task DeleteAsync_ValidIds_DeletesReceipts()
	{
		// Arrange
		const int initialReceiptCount = 5;
		const int deleteCount = 2;
		const int expectedRemainingCount = 3;
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		List<ReceiptEntity> entities = ReceiptEntityGenerator.GenerateList(initialReceiptCount);
		await context.Receipts.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);
		(await context.Receipts.CountAsync()).Should().Be(initialReceiptCount);

		List<Guid> idsToDelete = [.. entities.Take(deleteCount).Select(e => e.Id)];
		ReceiptRepository repository = new(_contextFactory);

		// Act
		await repository.DeleteAsync(idsToDelete, CancellationToken.None);

		using ApplicationDbContext verifyContext = _contextFactory.CreateDbContext();
		List<ReceiptEntity> remainingEntities = await verifyContext.Receipts.ToListAsync();

		// Assert
		remainingEntities.Count.Should().Be(expectedRemainingCount);
		Assert.DoesNotContain(remainingEntities, e => idsToDelete.Contains(e.Id));

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task DeleteAsync_ReceiptWithSyncedTransaction_CascadeSoftDeletesYnabSyncRecord()
	{
		// RECEIPTS-755 regression: deleting a receipt must cascade two levels
		// (Receipt -> Transaction -> YnabSyncRecord) so a synced transaction's ACTIVE
		// sync record does not linger and later block Empty Trash on the NO ACTION FK.
		// Arrange — receipt -> transaction -> active sync record.
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		card.Id = account.Id;
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id);
		YnabSyncRecordEntity syncRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transaction.Id);

		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			await context.Receipts.AddAsync(receipt);
			await context.Accounts.AddAsync(account);
			await context.Cards.AddAsync(card);
			await context.Transactions.AddAsync(transaction);
			await context.YnabSyncRecords.AddAsync(syncRecord);
			await context.SaveChangesAsync(CancellationToken.None);
		}

		ReceiptRepository repository = new(_contextFactory);

		// Act — deleting the receipt must reach the transaction's sync record.
		await repository.DeleteAsync([receipt.Id], CancellationToken.None);

		// Assert — no active sync record survives; it was cascade soft-deleted.
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		(await verify.YnabSyncRecords.AnyAsync()).Should().BeFalse("the active sync record must not linger after its receipt is deleted");

		YnabSyncRecordEntity deletedRecord = await verify.YnabSyncRecords
			.IgnoreQueryFilters()
			.SingleAsync(s => s.Id == syncRecord.Id);
		deletedRecord.DeletedAt.Should().NotBeNull();
		deletedRecord.CascadeDeletedByParentId.Should().Be(transaction.Id);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task DeleteThenRestore_ReceiptWithSyncedTransaction_RevivesYnabSyncRecord()
	{
		// RECEIPTS-755 restore symmetry across two levels: restoring a receipt revives its
		// transactions AND the YnabSyncRecords those transactions cascade-soft-deleted (which
		// are tagged with the transaction id, not the receipt id).
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		card.Id = account.Id;
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id);
		YnabSyncRecordEntity syncRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transaction.Id);

		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			await context.Receipts.AddAsync(receipt);
			await context.Accounts.AddAsync(account);
			await context.Cards.AddAsync(card);
			await context.Transactions.AddAsync(transaction);
			await context.YnabSyncRecords.AddAsync(syncRecord);
			await context.SaveChangesAsync(CancellationToken.None);
		}

		ReceiptRepository repository = new(_contextFactory);

		// Act — delete then restore the receipt.
		await repository.DeleteAsync([receipt.Id], CancellationToken.None);
		bool restored = await repository.RestoreAsync(receipt.Id, CancellationToken.None);

		// Assert — receipt, transaction, and sync record all active again.
		restored.Should().BeTrue();
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		(await verify.Receipts.AnyAsync(r => r.Id == receipt.Id)).Should().BeTrue();
		(await verify.Transactions.AnyAsync(t => t.Id == transaction.Id)).Should().BeTrue();

		YnabSyncRecordEntity revived = await verify.YnabSyncRecords.IgnoreQueryFilters().SingleAsync(s => s.Id == syncRecord.Id);
		revived.DeletedAt.Should().BeNull("restoring the receipt must revive its transaction's cascade-soft-deleted sync record");
		revived.CascadeDeletedByParentId.Should().BeNull();

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task ExistsAsync_ExistingId_ReturnsTrue()
	{
		// Arrange
		const int expectedCount = 1;
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		ReceiptEntity entity = ReceiptEntityGenerator.Generate();
		await context.Receipts.AddAsync(entity);
		await context.SaveChangesAsync(CancellationToken.None);
		(await context.Receipts.CountAsync()).Should().Be(expectedCount);

		ReceiptRepository repository = new(_contextFactory);

		// Act
		bool result = await repository.ExistsAsync(entity.Id, CancellationToken.None);

		// Assert
		Assert.True(result);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task ExistsAsync_NonExistingId_ReturnsFalse()
	{
		// Arrange
		const int expectedCount = 0;
		ReceiptRepository repository = new(_contextFactory);
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		(await context.Receipts.CountAsync()).Should().Be(expectedCount);

		// Act
		bool result = await repository.ExistsAsync(Guid.NewGuid(), CancellationToken.None);

		// Assert
		Assert.False(result);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task ExistsAsync_SoftDeletedId_ReturnsFalse()
	{
		// Arrange — a soft-deleted receipt must read as absent so child-create paths reject it
		// with 404 instead of orphaning children under a trashed receipt (RECEIPTS-763).
		ReceiptEntity entity = ReceiptEntityGenerator.Generate();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			await seed.Receipts.AddAsync(entity);
			await seed.SaveChangesAsync(CancellationToken.None);

			// Remove() on a soft-deletable entity is intercepted as a soft delete (sets DeletedAt).
			seed.Receipts.Remove(entity);
			await seed.SaveChangesAsync(CancellationToken.None);
		}

		ReceiptRepository repository = new(_contextFactory);

		// Act
		bool result = await repository.ExistsAsync(entity.Id, CancellationToken.None);

		// Assert
		Assert.False(result);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetCountAsync_ReturnsCorrectCount()
	{
		// Arrange
		const int expectedReceiptCount = 3;
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		List<ReceiptEntity> entities = ReceiptEntityGenerator.GenerateList(expectedReceiptCount);
		await context.Receipts.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);
		(await context.Receipts.CountAsync()).Should().Be(expectedReceiptCount);

		ReceiptRepository repository = new(_contextFactory);

		// Act
		int count = await repository.GetCountAsync(CancellationToken.None);

		// Assert
		count.Should().Be(expectedReceiptCount);

		_contextFactory.ResetDatabase();
	}
}
