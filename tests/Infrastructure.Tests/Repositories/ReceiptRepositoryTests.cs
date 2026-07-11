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
