using Application.Models;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.Tests.Repositories;

public class TransactionRepositoryTests
{
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

	private async Task<(ReceiptEntity receipt, AccountEntity account)> CreateParentEntitiesAsync()
	{
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		await context.Receipts.AddAsync(receipt);

		AccountEntity account = AccountEntityGenerator.Generate();
		await context.Accounts.AddAsync(account);

		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		card.Id = account.Id;
		await context.Cards.AddAsync(card);

		await context.SaveChangesAsync(CancellationToken.None);
		return (receipt, account);
	}

	[Fact]
	public async Task GetByIdAsync_ExistingId_ReturnsTransaction()
	{
		// Arrange
		(ReceiptEntity receipt, AccountEntity account) = await CreateParentEntitiesAsync();
		using ApplicationDbContext context = _contextFactory.CreateDbContext();

		TransactionEntity entity = TransactionEntityGenerator.Generate(receipt.Id, account.Id);
		await context.Transactions.AddAsync(entity);
		await context.SaveChangesAsync(CancellationToken.None);

		TransactionRepository repository = new(_contextFactory);

		// Act
		TransactionEntity? actual = await repository.GetByIdAsync(entity.Id, CancellationToken.None);

		// Assert
		Assert.NotNull(actual);
		actual.Should().BeEquivalentTo(entity, opt => opt.Excluding(member => member.Name == nameof(TransactionEntity.Receipt) || member.Name == nameof(TransactionEntity.Account) || member.Name == nameof(TransactionEntity.Card)));

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetByIdAsync_NonExistingId_ReturnsNull()
	{
		// Arrange
		const int expectedCount = 0;
		TransactionRepository repository = new(_contextFactory);
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		(await context.Transactions.CountAsync()).Should().Be(expectedCount);

		// Act
		TransactionEntity? result = await repository.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

		// Assert
		Assert.Null(result);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetByReceiptIdAsync_ExistingReceiptId_ReturnsTransactions()
	{
		// Arrange
		const int expectedTransactionCount = 3;
		(ReceiptEntity receipt, AccountEntity account) = await CreateParentEntitiesAsync();
		using ApplicationDbContext context = _contextFactory.CreateDbContext();

		List<TransactionEntity> entities = TransactionEntityGenerator.GenerateList(expectedTransactionCount, receipt.Id, account.Id);
		await context.Transactions.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);

		TransactionRepository repository = new(_contextFactory);

		// Act
		List<TransactionEntity> actual = await repository.GetByReceiptIdAsync(receipt.Id, 0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		actual.Should().BeEquivalentTo(entities, opt => opt.Excluding(member => member.Name == nameof(TransactionEntity.Receipt) || member.Name == nameof(TransactionEntity.Account)));

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAllAsync_ReturnsAllTransactions()
	{
		// Arrange
		const int expectedTransactionCount = 3;
		(ReceiptEntity receipt, AccountEntity account) = await CreateParentEntitiesAsync();
		using ApplicationDbContext context = _contextFactory.CreateDbContext();

		List<TransactionEntity> entities = TransactionEntityGenerator.GenerateList(expectedTransactionCount, receipt.Id, account.Id);
		await context.Transactions.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);
		(await context.Transactions.CountAsync()).Should().Be(expectedTransactionCount);

		TransactionRepository repository = new(_contextFactory);

		// Act
		List<TransactionEntity> actual = await repository.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		actual.Should().BeEquivalentTo(entities, opt => opt.Excluding(member => member.Name == nameof(TransactionEntity.Receipt) || member.Name == nameof(TransactionEntity.Account)));

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task CreateAsync_ValidTransactions_ReturnsCreatedTransactions()
	{
		// Arrange
		const int expectedTransactionCount = 2;
		List<TransactionEntity> entities = TransactionEntityGenerator.GenerateList(expectedTransactionCount);
		entities.ForEach(e => e.Id = Guid.Empty);
		TransactionRepository repository = new(_contextFactory);

		// Act
		List<TransactionEntity> actual = await repository.CreateAsync(entities, CancellationToken.None);

		// Assert
		Assert.All(actual, t =>
		{
			Assert.NotEqual(Guid.Empty, t.Id);
		});

		actual.Should().BeEquivalentTo(entities, opt => opt.Excluding(x => x.Id));

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task UpdateAsync_ValidTransactions_UpdatesTransactions()
	{
		// Arrange
		const int expectedTransactionCount = 2;
		(ReceiptEntity receipt, AccountEntity account) = await CreateParentEntitiesAsync();
		using ApplicationDbContext context = _contextFactory.CreateDbContext();

		List<TransactionEntity> entities = TransactionEntityGenerator.GenerateList(expectedTransactionCount, receipt.Id, account.Id);
		await context.Transactions.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);
		(await context.Transactions.CountAsync()).Should().Be(expectedTransactionCount);

		TransactionRepository repository = new(_contextFactory);

		// Modify transactions
		entities.ForEach(e =>
		{
			e.Amount += 10.0m;
			e.Date = e.Date.AddDays(1);
		});

		// Act
		await repository.UpdateAsync(entities, CancellationToken.None);

		using ApplicationDbContext verifyContext = _contextFactory.CreateDbContext();
		List<TransactionEntity> updatedEntities = await verifyContext.Transactions.ToListAsync();

		// Assert
		updatedEntities.Should().BeEquivalentTo(entities, opt => opt.Excluding(member => member.Name == nameof(TransactionEntity.Receipt) || member.Name == nameof(TransactionEntity.Account) || member.Name == nameof(TransactionEntity.Card)));

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task DeleteAsync_ValidIds_DeletesTransactions()
	{
		// Arrange
		const int initialTransactionCount = 5;
		const int transactionsToDeleteCount = 2;
		const int expectedRemainingCount = 3;

		(ReceiptEntity receipt, AccountEntity account) = await CreateParentEntitiesAsync();
		using ApplicationDbContext context = _contextFactory.CreateDbContext();

		List<TransactionEntity> entities = TransactionEntityGenerator.GenerateList(initialTransactionCount, receipt.Id, account.Id);
		await context.Transactions.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);
		(await context.Transactions.CountAsync()).Should().Be(initialTransactionCount);

		List<Guid> idsToDelete = [.. entities.Take(transactionsToDeleteCount).Select(e => e.Id)];
		TransactionRepository repository = new(_contextFactory);

		// Act
		await repository.DeleteAsync(idsToDelete, CancellationToken.None);

		using ApplicationDbContext verifyContext = _contextFactory.CreateDbContext();
		List<TransactionEntity> remainingEntities = await verifyContext.Transactions.ToListAsync();

		// Assert
		remainingEntities.Count.Should().Be(expectedRemainingCount);
		Assert.DoesNotContain(remainingEntities, e => idsToDelete.Contains(e.Id));

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task DeleteAsync_SyncedTransaction_CascadeSoftDeletesActiveYnabSyncRecord()
	{
		// RECEIPTS-755 regression: deleting a synced transaction must cascade
		// soft-delete its ACTIVE YnabSyncRecord so no orphaned active record lingers
		// to later block Empty Trash on the NO ACTION LocalTransactionId FK.
		// Arrange — a transaction with an active sync record (the state after a YNAB push).
		(ReceiptEntity receipt, AccountEntity account) = await CreateParentEntitiesAsync();
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id);
		YnabSyncRecordEntity syncRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transaction.Id);

		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			await context.Transactions.AddAsync(transaction);
			await context.YnabSyncRecords.AddAsync(syncRecord);
			await context.SaveChangesAsync(CancellationToken.None);
		}

		TransactionRepository repository = new(_contextFactory);

		// Act — the repository must load the sync record so HandleSoftDelete cascades to it.
		await repository.DeleteAsync([transaction.Id], CancellationToken.None);

		// Assert — no active sync record survives; it was cascade soft-deleted and
		// tagged with the parent transaction id.
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		(await verify.YnabSyncRecords.AnyAsync()).Should().BeFalse("the active sync record must not linger after its transaction is deleted");

		YnabSyncRecordEntity deletedRecord = await verify.YnabSyncRecords
			.IgnoreQueryFilters()
			.SingleAsync(s => s.Id == syncRecord.Id);
		deletedRecord.DeletedAt.Should().NotBeNull();
		deletedRecord.CascadeDeletedByParentId.Should().Be(transaction.Id);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task DeleteThenRestore_SyncedTransaction_RevivesAllCascadeSoftDeletedYnabSyncRecords()
	{
		// RECEIPTS-755 restore symmetry: the unique index is per (LocalTransactionId, SyncType),
		// so one transaction can carry several active sync records. Deleting the transaction must
		// cascade-soft-delete ALL of them, and restoring it must revive every one — otherwise a
		// live transaction ends up with dead sync history.
		// Arrange — one transaction with TWO active sync records of different SyncType.
		(ReceiptEntity receipt, AccountEntity account) = await CreateParentEntitiesAsync();
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id);
		YnabSyncRecordEntity pushRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transaction.Id);
		YnabSyncRecordEntity memoRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transaction.Id, syncType: Common.YnabSyncType.MemoUpdate);

		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			await context.Transactions.AddAsync(transaction);
			await context.YnabSyncRecords.AddRangeAsync(pushRecord, memoRecord);
			await context.SaveChangesAsync(CancellationToken.None);
		}

		TransactionRepository repository = new(_contextFactory);

		// Act — delete the transaction; both sync records cascade-soft-delete.
		await repository.DeleteAsync([transaction.Id], CancellationToken.None);

		using (ApplicationDbContext afterDelete = _contextFactory.CreateDbContext())
		{
			(await afterDelete.YnabSyncRecords.AnyAsync()).Should().BeFalse("both sync records should be soft-deleted with their transaction");
			List<YnabSyncRecordEntity> deleted = await afterDelete.YnabSyncRecords.IgnoreQueryFilters().ToListAsync();
			deleted.Should().HaveCount(2);
			deleted.Should().AllSatisfy(r =>
			{
				r.DeletedAt.Should().NotBeNull();
				r.CascadeDeletedByParentId.Should().Be(transaction.Id);
			});
		}

		// Act — restore the transaction; both sync records must come back active.
		bool restored = await repository.RestoreAsync(transaction.Id, CancellationToken.None);

		// Assert
		restored.Should().BeTrue();
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		(await verify.Transactions.AnyAsync(t => t.Id == transaction.Id)).Should().BeTrue();

		List<YnabSyncRecordEntity> activeRecords = await verify.YnabSyncRecords.ToListAsync();
		activeRecords.Should().HaveCount(2, "both cascade-soft-deleted sync records must be revived on restore");
		activeRecords.Should().AllSatisfy(r =>
		{
			r.DeletedAt.Should().BeNull();
			r.CascadeDeletedByParentId.Should().BeNull();
		});
		activeRecords.Select(r => r.Id).Should().BeEquivalentTo([pushRecord.Id, memoRecord.Id]);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task RestoreAsync_SyncedTransaction_DoesNotReviveIndependentlySoftDeletedSyncRecord()
	{
		// Restore must be the exact inverse of the cascade delete: only sync records
		// cascade-soft-deleted BY the transaction (CascadeDeletedByParentId == tx id) are
		// revived. A sync record the user soft-deleted independently beforehand stays deleted.
		(ReceiptEntity receipt, AccountEntity account) = await CreateParentEntitiesAsync();
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id);
		YnabSyncRecordEntity cascadeRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transaction.Id);
		YnabSyncRecordEntity independentRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transaction.Id, syncType: Common.YnabSyncType.MemoUpdate);

		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			await context.Transactions.AddAsync(transaction);
			await context.YnabSyncRecords.AddRangeAsync(cascadeRecord, independentRecord);
			await context.SaveChangesAsync(CancellationToken.None);
		}

		// Independently soft-delete one sync record BEFORE the transaction is deleted.
		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			YnabSyncRecordEntity toDelete = await context.YnabSyncRecords.FirstAsync(r => r.Id == independentRecord.Id);
			context.YnabSyncRecords.Remove(toDelete);
			await context.SaveChangesAsync(CancellationToken.None);
		}

		TransactionRepository repository = new(_contextFactory);

		// Act — delete (cascades only the still-active cascadeRecord) then restore.
		await repository.DeleteAsync([transaction.Id], CancellationToken.None);
		await repository.RestoreAsync(transaction.Id, CancellationToken.None);

		// Assert — cascadeRecord revived; independentRecord stays soft-deleted.
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();

		YnabSyncRecordEntity revived = await verify.YnabSyncRecords.IgnoreQueryFilters().SingleAsync(r => r.Id == cascadeRecord.Id);
		revived.DeletedAt.Should().BeNull();
		revived.CascadeDeletedByParentId.Should().BeNull();

		YnabSyncRecordEntity stillDeleted = await verify.YnabSyncRecords.IgnoreQueryFilters().SingleAsync(r => r.Id == independentRecord.Id);
		stillDeleted.DeletedAt.Should().NotBeNull("an independently soft-deleted sync record must not be revived by restoring the transaction");

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task ExistsAsync_ExistingId_ReturnsTrue()
	{
		// Arrange
		const int expectedCount = 1;
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		TransactionEntity entity = TransactionEntityGenerator.Generate();
		await context.Transactions.AddAsync(entity);
		await context.SaveChangesAsync(CancellationToken.None);
		(await context.Transactions.CountAsync()).Should().Be(expectedCount);

		TransactionRepository repository = new(_contextFactory);

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
		TransactionRepository repository = new(_contextFactory);
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		(await context.Transactions.CountAsync()).Should().Be(expectedCount);

		// Act
		bool result = await repository.ExistsAsync(Guid.NewGuid(), CancellationToken.None);

		// Assert
		Assert.False(result);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetWithAccountByReceiptIdAsync_ReturnsTransactionsWithAccounts()
	{
		// Arrange
		const int expectedTransactionCount = 3;
		(ReceiptEntity receipt, AccountEntity account) = await CreateParentEntitiesAsync();
		using ApplicationDbContext context = _contextFactory.CreateDbContext();

		List<TransactionEntity> entities = TransactionEntityGenerator.GenerateList(expectedTransactionCount, receipt.Id, account.Id);
		await context.Transactions.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);

		TransactionRepository repository = new(_contextFactory);

		// Act
		List<TransactionEntity> actual = await repository.GetWithAccountByReceiptIdAsync(receipt.Id, CancellationToken.None);

		// Assert
		actual.Should().HaveCount(expectedTransactionCount);
		actual.Should().AllSatisfy(t =>
		{
			t.Account.Should().NotBeNull();
			t.Account!.Id.Should().Be(account.Id);
		});
		actual.Should().BeEquivalentTo(entities, opt => opt
			.Excluding(member => member.Name == nameof(TransactionEntity.Receipt))
			.Excluding(member => member.Name == nameof(TransactionEntity.Account)));

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetCountAsync_ReturnsCorrectCount()
	{
		// Arrange
		const int expectedTransactionCount = 3;
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		List<TransactionEntity> entities = TransactionEntityGenerator.GenerateList(expectedTransactionCount);
		await context.Transactions.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);
		(await context.Transactions.CountAsync()).Should().Be(expectedTransactionCount);

		TransactionRepository repository = new(_contextFactory);

		// Act
		int count = await repository.GetCountAsync(CancellationToken.None);

		// Assert
		count.Should().Be(expectedTransactionCount);

		_contextFactory.ResetDatabase();
	}
}
