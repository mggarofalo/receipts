using Application.Models;
using Domain.Aggregates;
using Domain.Core;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using SampleData.Domain.Core;
using SampleData.Entities;

namespace Infrastructure.Tests.Services;

public class TransactionServiceTests
{
	private readonly Mock<ITransactionRepository> _mockRepository;
	private readonly TransactionMapper _mapper;
	private readonly AccountMapper _accountMapper;
	private readonly TransactionService _service;

	public TransactionServiceTests()
	{
		_mockRepository = new Mock<ITransactionRepository>();
		_mapper = new TransactionMapper();
		_accountMapper = new AccountMapper();

		// The balance-validation methods (CreateWithBalanceValidationAsync / UpdateWith...) require
		// a DbContext factory and the child-entity mappers, but every test in this class exercises
		// the repository-delegating members only, so the factory is never invoked. The DB-level
		// balance-validation behaviour (row lock, existence guard, serialization) is covered against
		// real PostgreSQL in Infrastructure.IntegrationTests.TransactionBalanceValidationTests —
		// InMemory cannot model the row lock and running that path here crashed the test host under
		// coverage on CI (RECEIPTS-764).
		Mock<IDbContextFactory<ApplicationDbContext>> contextFactory = new();
		_service = new TransactionService(
			_mockRepository.Object,
			_mapper,
			_accountMapper,
			contextFactory.Object,
			new ReceiptMapper(),
			new ReceiptItemMapper(),
			new AdjustmentMapper());
	}

	[Fact]
	public async Task CreateAsync_ValidTransactions_CallsRepositoryCreateAsyncAndReturnsCreatedTransactions()
	{
		// Arrange
		List<Transaction> models = TransactionGenerator.GenerateList(2);
		Guid receiptId = Guid.NewGuid();
		List<TransactionEntity> createdEntities = TransactionEntityGenerator.GenerateList(2);

		_mockRepository.Setup(r => r.CreateAsync(It.IsAny<List<TransactionEntity>>(), It.IsAny<CancellationToken>())).ReturnsAsync(createdEntities);

		// Act
		List<Transaction> actual = await _service.CreateAsync(models, receiptId, CancellationToken.None);

		// Assert
		Assert.Equal(createdEntities.Count, actual.Count);
		_mockRepository.Verify(r => r.CreateAsync(It.IsAny<List<TransactionEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task DeleteAsync_ValidIds_CallsRepositoryDeleteAsync()
	{
		// Arrange
		List<Guid> ids = [Guid.NewGuid(), Guid.NewGuid()];

		// Act
		await _service.DeleteAsync(ids, CancellationToken.None);

		// Assert
		_mockRepository.Verify(r => r.DeleteAsync(ids, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task ExistsAsync_ValidId_ReturnsExpectedResult()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		bool expected = true;
		_mockRepository.Setup(r => r.ExistsAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		bool actual = await _service.ExistsAsync(id, CancellationToken.None);

		// Assert
		Assert.Equal(expected, actual);
	}

	[Fact]
	public async Task GetAllAsync_ReturnsAllTransactions()
	{
		// Arrange
		List<TransactionEntity> entities = TransactionEntityGenerator.GenerateList(3);

		_mockRepository.Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entities.Count);
		_mockRepository.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>())).ReturnsAsync(entities);

		// Act
		PagedResult<Transaction> actual = await _service.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		Assert.Equal(entities.Count, actual.Data.Count);
		Assert.Equal(entities.Count, actual.Total);
		Assert.Equal(0, actual.Offset);
		Assert.Equal(50, actual.Limit);
	}

	[Fact]
	public async Task GetByIdAsync_ExistingId_ReturnsTransaction()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		TransactionEntity entity = TransactionEntityGenerator.Generate();

		_mockRepository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);

		// Act
		Transaction? actual = await _service.GetByIdAsync(id, CancellationToken.None);

		// Assert
		Assert.NotNull(actual);
		actual.Id.Should().Be(entity.Id);
	}

	[Fact]
	public async Task GetByIdAsync_NonExistingId_ReturnsNull()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mockRepository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((TransactionEntity?)null);

		// Act
		Transaction? actual = await _service.GetByIdAsync(id, CancellationToken.None);

		// Assert
		Assert.Null(actual);
	}

	[Fact]
	public async Task GetByReceiptIdAsync_ExistingReceiptId_ReturnsTransactions()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<TransactionEntity> entities = TransactionEntityGenerator.GenerateList(2);

		_mockRepository.Setup(r => r.GetByReceiptIdCountAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(entities.Count);
		_mockRepository.Setup(r => r.GetByReceiptIdAsync(receiptId, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>())).ReturnsAsync(entities);

		// Act
		PagedResult<Transaction> actual = await _service.GetByReceiptIdAsync(receiptId, 0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		Assert.Equal(entities.Count, actual.Data.Count);
		Assert.Equal(entities.Count, actual.Total);
		Assert.Equal(0, actual.Offset);
		Assert.Equal(50, actual.Limit);
	}

	[Fact]
	public async Task GetByReceiptIdAsync_NonExistingReceiptId_ReturnsEmptyPagedResult()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		_mockRepository.Setup(r => r.GetByReceiptIdCountAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(0);
		_mockRepository.Setup(r => r.GetByReceiptIdAsync(receiptId, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

		// Act
		PagedResult<Transaction> actual = await _service.GetByReceiptIdAsync(receiptId, 0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		Assert.Empty(actual.Data);
		Assert.Equal(0, actual.Total);
	}

	[Fact]
	public async Task GetCountAsync_ReturnsCorrectCount()
	{
		// Arrange
		int expected = 5;
		_mockRepository.Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		int actual = await _service.GetCountAsync(CancellationToken.None);

		// Assert
		Assert.Equal(expected, actual);
	}

	[Fact]
	public async Task UpdateAsync_ValidTransactions_CallsRepositoryUpdateAsync()
	{
		// Arrange
		List<Transaction> models = TransactionGenerator.GenerateList(2);
		Guid receiptId = Guid.NewGuid();

		// Act
		await _service.UpdateAsync(models, receiptId, CancellationToken.None);

		// Assert
		_mockRepository.Verify(r => r.UpdateAsync(It.Is<List<TransactionEntity>>(e =>
			e.All(t => t.ReceiptId == receiptId)),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetTransactionAccountsByReceiptIdAsync_ReturnsTransactionAccounts()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<AccountEntity> accountEntities = AccountEntityGenerator.GenerateList(3);
		List<TransactionEntity> transactionEntities = TransactionEntityGenerator.GenerateList(3, receiptId);
		for (int i = 0; i < transactionEntities.Count; i++)
		{
			transactionEntities[i].AccountId = accountEntities[i].Id;
			transactionEntities[i].Account = accountEntities[i];
		}

		_mockRepository.Setup(r => r.GetWithAccountByReceiptIdAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(transactionEntities);

		// Act
		List<TransactionAccount> result = await _service.GetTransactionAccountsByReceiptIdAsync(receiptId, CancellationToken.None);

		// Assert
		result.Should().HaveCount(3);
		for (int i = 0; i < result.Count; i++)
		{
			result[i].Transaction.Id.Should().Be(transactionEntities[i].Id);
			result[i].Account.Id.Should().Be(accountEntities[i].Id);
		}
	}

	[Fact]
	public async Task GetTransactionAccountsByReceiptIdAsync_SkipsTransactionsWithNullAccount()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<TransactionEntity> transactionEntities = TransactionEntityGenerator.GenerateList(2, receiptId);
		AccountEntity account = AccountEntityGenerator.Generate();
		transactionEntities[0].Account = account;
		transactionEntities[0].AccountId = account.Id;
		transactionEntities[1].Account = null;

		_mockRepository.Setup(r => r.GetWithAccountByReceiptIdAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(transactionEntities);

		// Act
		List<TransactionAccount> result = await _service.GetTransactionAccountsByReceiptIdAsync(receiptId, CancellationToken.None);

		// Assert
		result.Should().HaveCount(1);
		result[0].Transaction.Id.Should().Be(transactionEntities[0].Id);
	}

	[Fact]
	public async Task GetTransactionAccountsByReceiptIdAsync_EmptyList_ReturnsEmpty()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		_mockRepository.Setup(r => r.GetWithAccountByReceiptIdAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		// Act
		List<TransactionAccount> result = await _service.GetTransactionAccountsByReceiptIdAsync(receiptId, CancellationToken.None);

		// Assert
		result.Should().BeEmpty();
	}
}
