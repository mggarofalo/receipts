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

public class CardServiceTests
{
	private readonly Mock<ICardRepository> _mockRepository;
	private readonly CardMapper _mapper;
	private readonly CardService _service;

	public CardServiceTests()
	{
		_mockRepository = new Mock<ICardRepository>();
		_mapper = new CardMapper();
		_service = new CardService(_mockRepository.Object, _mapper);
	}

	[Fact]
	public async Task CreateAsync_ValidAccounts_CallsRepositoryCreateAsyncAndReturnsCreatedAccounts()
	{
		// Arrange
		List<Card> models = CardGenerator.GenerateList(2);
		List<CardEntity> createdEntities = CardEntityGenerator.GenerateList(2);

		_mockRepository.Setup(r => r.CreateAsync(It.IsAny<List<CardEntity>>(), It.IsAny<CancellationToken>())).ReturnsAsync(createdEntities);

		// Act
		List<Card> actual = await _service.CreateAsync(models, CancellationToken.None);

		// Assert
		Assert.Equal(createdEntities.Count, actual.Count);
		_mockRepository.Verify(r => r.CreateAsync(It.IsAny<List<CardEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
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
		List<CardEntity> entities = CardEntityGenerator.GenerateList(3);

		_mockRepository.Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entities.Count);
		_mockRepository.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>())).ReturnsAsync(entities);

		// Act
		PagedResult<Card> actual = await _service.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None);

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
		List<CardEntity> entities = CardEntityGenerator.GenerateList(2);

		_mockRepository.Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>(), true, "4242")).ReturnsAsync(entities.Count);
		_mockRepository.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>(), true, "4242")).ReturnsAsync(entities);

		// Act
		PagedResult<Card> actual = await _service.GetAllAsync(0, 50, SortParams.Default, true, "4242", CancellationToken.None);

		// Assert
		Assert.Equal(entities.Count, actual.Data.Count);
		Assert.Equal(entities.Count, actual.Total);
		_mockRepository.Verify(r => r.GetCountAsync(It.IsAny<CancellationToken>(), true, "4242"), Times.Once);
		_mockRepository.Verify(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>(), true, "4242"), Times.Once);
	}

	[Fact]
	public async Task GetByIdAsync_ExistingId_ReturnsAccount()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		CardEntity entity = CardEntityGenerator.Generate();

		_mockRepository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);

		// Act
		Card? actual = await _service.GetByIdAsync(id, CancellationToken.None);

		// Assert
		Assert.NotNull(actual);
		actual.Id.Should().Be(entity.Id);
	}

	[Fact]
	public async Task GetByIdAsync_NonExistingId_ReturnsNull()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mockRepository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((CardEntity?)null);

		// Act
		Card? actual = await _service.GetByIdAsync(id, CancellationToken.None);

		// Assert
		Assert.Null(actual);
	}

	[Fact]
	public async Task GetByTransactionIdAsync_ExistingTransactionId_ReturnsAccount()
	{
		// Arrange
		Guid transactionId = Guid.NewGuid();
		CardEntity entity = CardEntityGenerator.Generate();

		_mockRepository.Setup(r => r.GetByTransactionIdAsync(transactionId, It.IsAny<CancellationToken>())).ReturnsAsync(entity);

		// Act
		Card? actual = await _service.GetByTransactionIdAsync(transactionId, CancellationToken.None);

		// Assert
		Assert.NotNull(actual);
		actual.Id.Should().Be(entity.Id);
	}

	[Fact]
	public async Task GetByTransactionIdAsync_NonExistingTransactionId_ReturnsNull()
	{
		// Arrange
		Guid transactionId = Guid.NewGuid();
		_mockRepository.Setup(r => r.GetByTransactionIdAsync(transactionId, It.IsAny<CancellationToken>())).ReturnsAsync((CardEntity?)null);

		// Act
		Card? actual = await _service.GetByTransactionIdAsync(transactionId, CancellationToken.None);

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
		List<Card> models = CardGenerator.GenerateList(2);

		// Act
		await _service.UpdateAsync(models, CancellationToken.None);

		// Assert
		_mockRepository.Verify(r => r.UpdateAsync(It.IsAny<List<CardEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
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
	public async Task GetTransactionCountByCardIdAsync_ReturnsCorrectCount()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		int expected = 3;
		_mockRepository.Setup(r => r.GetTransactionCountByCardIdAsync(accountId, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		int actual = await _service.GetTransactionCountByCardIdAsync(accountId, CancellationToken.None);

		// Assert
		Assert.Equal(expected, actual);
	}

	[Fact]
	public async Task GetByAccountIdAsync_ReturnsMappedCards()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		List<CardEntity> entities = CardEntityGenerator.GenerateList(2);
		_mockRepository.Setup(r => r.GetByAccountIdAsync(accountId, It.IsAny<CancellationToken>())).ReturnsAsync(entities);

		// Act
		List<Card> actual = await _service.GetByAccountIdAsync(accountId, CancellationToken.None);

		// Assert
		actual.Should().HaveCount(entities.Count);
		actual.Select(c => c.Id).Should().BeEquivalentTo(entities.Select(e => e.Id));
		_mockRepository.Verify(r => r.GetByAccountIdAsync(accountId, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetByAccountIdAsync_ReturnsEmptyList_WhenNoCards()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		_mockRepository.Setup(r => r.GetByAccountIdAsync(accountId, It.IsAny<CancellationToken>())).ReturnsAsync([]);

		// Act
		List<Card> actual = await _service.GetByAccountIdAsync(accountId, CancellationToken.None);

		// Assert
		actual.Should().BeEmpty();
	}
}
