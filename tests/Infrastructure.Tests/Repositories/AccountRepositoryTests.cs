using Application.Models;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.Tests.Repositories;

public class AccountRepositoryTests
{
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

	[Fact]
	public async Task GetByIdAsync_ExistingId_ReturnsAccount()
	{
		// Arrange
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		AccountEntity entity = AccountEntityGenerator.Generate();
		await context.Accounts.AddAsync(entity);
		await context.SaveChangesAsync(CancellationToken.None);

		AccountRepository repository = new(_contextFactory);

		// Act
		AccountEntity? actual = await repository.GetByIdAsync(entity.Id, CancellationToken.None);

		// Assert
		Assert.NotNull(actual);
		actual.Should().BeEquivalentTo(entity);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetByIdAsync_NonExistingId_ReturnsNull()
	{
		// Arrange
		AccountRepository repository = new(_contextFactory);

		// Act
		AccountEntity? result = await repository.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

		// Assert
		Assert.Null(result);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetByTransactionIdAsync_ExistingTransactionId_ReturnsCorrectAccount()
	{
		// Arrange
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		AccountEntity accountEntity = AccountEntityGenerator.Generate();
		await context.Accounts.AddAsync(accountEntity);
		TransactionEntity transactionEntity = TransactionEntityGenerator.Generate(accountId: accountEntity.Id);
		await context.Transactions.AddAsync(transactionEntity);
		await context.SaveChangesAsync(CancellationToken.None);

		AccountRepository repository = new(_contextFactory);

		// Act
		AccountEntity? actual = await repository.GetByTransactionIdAsync(transactionEntity.Id, CancellationToken.None);

		// Assert
		Assert.NotNull(actual);
		actual.Should().BeEquivalentTo(accountEntity);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetByTransactionIdAsync_NonExistingTransactionId_ReturnsNull()
	{
		// Arrange
		AccountRepository repository = new(_contextFactory);

		// Act
		AccountEntity? result = await repository.GetByTransactionIdAsync(Guid.NewGuid(), CancellationToken.None);

		// Assert
		Assert.Null(result);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAllAsync_ReturnsAllAccounts()
	{
		// Arrange
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		List<AccountEntity> entities = AccountEntityGenerator.GenerateList(2);
		await context.Accounts.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);

		AccountRepository repository = new(_contextFactory);

		// Act
		List<AccountEntity> actual = await repository.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		actual.Should().BeEquivalentTo(entities);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAllAsync_WithIsActiveTrue_ReturnsOnlyActiveAccounts()
	{
		// Arrange
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		AccountEntity activeAccount = AccountEntityGenerator.Generate();
		activeAccount.IsActive = true;
		AccountEntity inactiveAccount = AccountEntityGenerator.Generate();
		inactiveAccount.IsActive = false;
		await context.Accounts.AddRangeAsync([activeAccount, inactiveAccount]);
		await context.SaveChangesAsync(CancellationToken.None);

		AccountRepository repository = new(_contextFactory);

		// Act
		List<AccountEntity> actual = await repository.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None, isActive: true);

		// Assert
		actual.Should().HaveCount(1);
		actual[0].Id.Should().Be(activeAccount.Id);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAllAsync_WithIsActiveFalse_ReturnsOnlyInactiveAccounts()
	{
		// Arrange
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		AccountEntity activeAccount = AccountEntityGenerator.Generate();
		activeAccount.IsActive = true;
		AccountEntity inactiveAccount = AccountEntityGenerator.Generate();
		inactiveAccount.IsActive = false;
		await context.Accounts.AddRangeAsync([activeAccount, inactiveAccount]);
		await context.SaveChangesAsync(CancellationToken.None);

		AccountRepository repository = new(_contextFactory);

		// Act
		List<AccountEntity> actual = await repository.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None, isActive: false);

		// Assert
		actual.Should().HaveCount(1);
		actual[0].Id.Should().Be(inactiveAccount.Id);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAllAsync_WithIsActiveNull_ReturnsAllAccounts()
	{
		// Arrange
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		AccountEntity activeAccount = AccountEntityGenerator.Generate();
		activeAccount.IsActive = true;
		AccountEntity inactiveAccount = AccountEntityGenerator.Generate();
		inactiveAccount.IsActive = false;
		await context.Accounts.AddRangeAsync([activeAccount, inactiveAccount]);
		await context.SaveChangesAsync(CancellationToken.None);

		AccountRepository repository = new(_contextFactory);

		// Act
		List<AccountEntity> actual = await repository.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None, isActive: null);

		// Assert
		actual.Should().HaveCount(2);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetCountAsync_WithIsActive_ReturnsFilteredCount()
	{
		// Arrange
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		AccountEntity activeAccount1 = AccountEntityGenerator.Generate();
		activeAccount1.IsActive = true;
		AccountEntity activeAccount2 = AccountEntityGenerator.Generate();
		activeAccount2.IsActive = true;
		AccountEntity inactiveAccount = AccountEntityGenerator.Generate();
		inactiveAccount.IsActive = false;
		await context.Accounts.AddRangeAsync([activeAccount1, activeAccount2, inactiveAccount]);
		await context.SaveChangesAsync(CancellationToken.None);

		AccountRepository repository = new(_contextFactory);

		// Act
		int activeCount = await repository.GetCountAsync(CancellationToken.None, isActive: true);
		int inactiveCount = await repository.GetCountAsync(CancellationToken.None, isActive: false);
		int allCount = await repository.GetCountAsync(CancellationToken.None, isActive: null);

		// Assert
		activeCount.Should().Be(2);
		inactiveCount.Should().Be(1);
		allCount.Should().Be(3);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task CreateAsync_ValidAccounts_ReturnsCreatedAccounts()
	{
		// Arrange
		List<AccountEntity> entities = AccountEntityGenerator.GenerateList(2);
		entities.ForEach(e => e.Id = Guid.Empty);
		AccountRepository repository = new(_contextFactory);

		// Act
		List<AccountEntity> actual = await repository.CreateAsync(entities, CancellationToken.None);

		// Assert
		Assert.All(actual, a =>
		{
			Assert.NotEqual(Guid.Empty, a.Id);
		});

		actual.Should().BeEquivalentTo(entities, opt => opt.Excluding(x => x.Id));

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task UpdateAsync_ValidAccount_UpdatesAccount()
	{
		// Arrange
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		List<AccountEntity> entities = AccountEntityGenerator.GenerateList(2);
		await context.Accounts.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);

		AccountRepository repository = new(_contextFactory);

		// Modify account
		entities.ForEach(e =>
		{
			e.Name = "Updated " + e.Name;
			e.IsActive = !e.IsActive;
		});

		// Act
		await repository.UpdateAsync(entities, CancellationToken.None);

		using ApplicationDbContext verifyContext = _contextFactory.CreateDbContext();
		List<AccountEntity> updatedEntities = await verifyContext.Accounts.ToListAsync();

		// Assert
		updatedEntities.Should().BeEquivalentTo(entities);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task ExistsAsync_ExistingId_ReturnsTrue()
	{
		// Arrange
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		AccountEntity entity = AccountEntityGenerator.Generate();
		await context.Accounts.AddAsync(entity);
		await context.SaveChangesAsync(CancellationToken.None);

		AccountRepository repository = new(_contextFactory);

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
		AccountRepository repository = new(_contextFactory);

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
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		List<AccountEntity> entities = AccountEntityGenerator.GenerateList(3);
		await context.Accounts.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);

		AccountRepository repository = new(_contextFactory);

		// Act
		int count = await repository.GetCountAsync(CancellationToken.None);

		// Assert
		count.Should().Be(entities.Count);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetTransactionCountByAccountIdAsync_CountsActiveAndSoftDeletedTransactions()
	{
		// RECEIPTS-754: the delete guard must see soft-deleted (trashed) transactions too,
		// so this count ignores the global soft-delete query filter.
		// Arrange
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		AccountEntity account = AccountEntityGenerator.Generate();
		AccountEntity otherAccount = AccountEntityGenerator.Generate();

		TransactionEntity activeTx = TransactionEntityGenerator.Generate(accountId: account.Id);
		TransactionEntity softDeletedTx = TransactionEntityGenerator.Generate(accountId: account.Id);
		softDeletedTx.DeletedAt = DateTimeOffset.UtcNow;
		TransactionEntity txOnOtherAccount = TransactionEntityGenerator.Generate(accountId: otherAccount.Id);

		await context.Accounts.AddRangeAsync(account, otherAccount);
		await context.Transactions.AddRangeAsync(activeTx, softDeletedTx, txOnOtherAccount);
		await context.SaveChangesAsync(CancellationToken.None);

		AccountRepository repository = new(_contextFactory);

		// Act
		int count = await repository.GetTransactionCountByAccountIdAsync(account.Id, CancellationToken.None);

		// Assert: both the active and the soft-deleted transaction on this account are counted,
		// but the transaction on the other account is not.
		count.Should().Be(2);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetTransactionCountByAccountIdAsync_ReturnsZero_WhenNoTransactionsReferenceAccount()
	{
		// Arrange
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		AccountEntity account = AccountEntityGenerator.Generate();
		await context.Accounts.AddAsync(account);
		await context.SaveChangesAsync(CancellationToken.None);

		AccountRepository repository = new(_contextFactory);

		// Act
		int count = await repository.GetTransactionCountByAccountIdAsync(account.Id, CancellationToken.None);

		// Assert
		count.Should().Be(0);

		_contextFactory.ResetDatabase();
	}
}
