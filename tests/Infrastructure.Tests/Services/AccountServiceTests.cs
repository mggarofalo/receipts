using Application.Models;
using Domain.Core;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Moq;
using SampleData.Domain.Core;
using SampleData.Entities;

namespace Infrastructure.Tests.Services;

public class AccountServiceTests
{
	private readonly Mock<IAccountRepository> _mockRepository;
	private readonly AccountMapper _mapper;
	private readonly AccountService _service;

	public AccountServiceTests()
	{
		_mockRepository = new Mock<IAccountRepository>();
		_mapper = new AccountMapper();
		_service = new AccountService(_mockRepository.Object, _mapper);
	}

	[Fact]
	public async Task CreateAsync_ValidAccounts_CallsRepositoryCreateAsyncAndReturnsCreatedAccounts()
	{
		// Arrange
		List<Account> models = AccountGenerator.GenerateList(2);
		List<AccountEntity> createdEntities = AccountEntityGenerator.GenerateList(2);

		_mockRepository.Setup(r => r.CreateAsync(It.IsAny<List<AccountEntity>>(), It.IsAny<CancellationToken>())).ReturnsAsync(createdEntities);

		// Act
		List<Account> actual = await _service.CreateAsync(models, CancellationToken.None);

		// Assert
		Assert.Equal(createdEntities.Count, actual.Count);
		_mockRepository.Verify(r => r.CreateAsync(It.IsAny<List<AccountEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
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
	public async Task GetAllAsync_ReturnsAllAccounts()
	{
		// Arrange
		List<AccountEntity> entities = AccountEntityGenerator.GenerateList(3);

		_mockRepository.Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entities.Count);
		_mockRepository.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>())).ReturnsAsync(entities);

		// Act
		PagedResult<Account> actual = await _service.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		Assert.Equal(entities.Count, actual.Data.Count);
		Assert.Equal(entities.Count, actual.Total);
		Assert.Equal(0, actual.Offset);
		Assert.Equal(50, actual.Limit);
	}

	[Fact]
	public async Task GetAllAsync_WithIsActive_FiltersAndReturnsCorrectCount()
	{
		// Arrange
		List<AccountEntity> entities = AccountEntityGenerator.GenerateList(2);

		_mockRepository.Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>(), true)).ReturnsAsync(entities.Count);
		_mockRepository.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>(), true)).ReturnsAsync(entities);

		// Act
		PagedResult<Account> actual = await _service.GetAllAsync(0, 50, SortParams.Default, true, CancellationToken.None);

		// Assert
		Assert.Equal(entities.Count, actual.Data.Count);
		Assert.Equal(entities.Count, actual.Total);
		_mockRepository.Verify(r => r.GetCountAsync(It.IsAny<CancellationToken>(), true), Times.Once);
		_mockRepository.Verify(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>(), true), Times.Once);
	}

	[Fact]
	public async Task GetByIdAsync_ExistingId_ReturnsAccount()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		AccountEntity entity = AccountEntityGenerator.Generate();

		_mockRepository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);

		// Act
		Account? actual = await _service.GetByIdAsync(id, CancellationToken.None);

		// Assert
		Assert.NotNull(actual);
		actual.Id.Should().Be(entity.Id);
	}

	[Fact]
	public async Task GetByIdAsync_NonExistingId_ReturnsNull()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mockRepository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((AccountEntity?)null);

		// Act
		Account? actual = await _service.GetByIdAsync(id, CancellationToken.None);

		// Assert
		Assert.Null(actual);
	}

	[Fact]
	public async Task GetByTransactionIdAsync_ExistingTransactionId_ReturnsAccount()
	{
		// Arrange
		Guid transactionId = Guid.NewGuid();
		AccountEntity entity = AccountEntityGenerator.Generate();

		_mockRepository.Setup(r => r.GetByTransactionIdAsync(transactionId, It.IsAny<CancellationToken>())).ReturnsAsync(entity);

		// Act
		Account? actual = await _service.GetByTransactionIdAsync(transactionId, CancellationToken.None);

		// Assert
		Assert.NotNull(actual);
		actual.Id.Should().Be(entity.Id);
	}

	[Fact]
	public async Task GetByTransactionIdAsync_NonExistingTransactionId_ReturnsNull()
	{
		// Arrange
		Guid transactionId = Guid.NewGuid();
		_mockRepository.Setup(r => r.GetByTransactionIdAsync(transactionId, It.IsAny<CancellationToken>())).ReturnsAsync((AccountEntity?)null);

		// Act
		Account? actual = await _service.GetByTransactionIdAsync(transactionId, CancellationToken.None);

		// Assert
		Assert.Null(actual);
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
	public async Task UpdateAsync_ValidAccounts_CallsRepositoryUpdateAsync()
	{
		// Arrange
		List<Account> models = AccountGenerator.GenerateList(2);

		// Act
		await _service.UpdateAsync(models, CancellationToken.None);

		// Assert
		_mockRepository.Verify(r => r.UpdateAsync(It.IsAny<List<AccountEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task DeleteAsync_ValidId_CallsRepositoryDeleteAsync()
	{
		// Arrange
		Guid id = Guid.NewGuid();

		// Act
		await _service.DeleteAsync(id, CancellationToken.None);

		// Assert
		_mockRepository.Verify(r => r.DeleteAsync(id, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetCardCountByAccountIdAsync_ReturnsCorrectCount()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		int expected = 3;
		_mockRepository.Setup(r => r.GetCardCountByAccountIdAsync(accountId, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		int actual = await _service.GetCardCountByAccountIdAsync(accountId, CancellationToken.None);

		// Assert
		Assert.Equal(expected, actual);
	}

	[Fact]
	public async Task GetTransactionCountByAccountIdAsync_DelegatesToRepositoryAndReturnsCount()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		int expected = 4;
		_mockRepository.Setup(r => r.GetTransactionCountByAccountIdAsync(accountId, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		int actual = await _service.GetTransactionCountByAccountIdAsync(accountId, CancellationToken.None);

		// Assert
		Assert.Equal(expected, actual);
		_mockRepository.Verify(r => r.GetTransactionCountByAccountIdAsync(accountId, It.IsAny<CancellationToken>()), Times.Once);
	}
}
