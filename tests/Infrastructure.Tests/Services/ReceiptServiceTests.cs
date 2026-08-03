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

public class ReceiptServiceTests
{
	private readonly Mock<IReceiptRepository> _mockRepository;
	private readonly ReceiptMapper _mapper;
	private readonly ReceiptService _service;

	public ReceiptServiceTests()
	{
		_mockRepository = new Mock<IReceiptRepository>();
		_mapper = new ReceiptMapper();
		_service = new ReceiptService(_mockRepository.Object, _mapper);
	}

	[Fact]
	public async Task CreateAsync_ValidReceipts_CallsRepositoryCreateAsyncAndReturnsCreatedReceipts()
	{
		// Arrange
		List<Receipt> models = ReceiptGenerator.GenerateList(2);
		List<ReceiptEntity> createdEntities = ReceiptEntityGenerator.GenerateList(2);

		_mockRepository.Setup(r => r.CreateAsync(It.IsAny<List<ReceiptEntity>>(), It.IsAny<CancellationToken>())).ReturnsAsync(createdEntities);

		// Act
		List<Receipt> actual = await _service.CreateAsync(models, CancellationToken.None);

		// Assert
		Assert.Equal(createdEntities.Count, actual.Count);
		_mockRepository.Verify(r => r.CreateAsync(It.IsAny<List<ReceiptEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
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
	public async Task GetAllAsync_ReturnsAllReceipts()
	{
		// Arrange
		List<ReceiptEntity> entities = ReceiptEntityGenerator.GenerateList(3);

		_mockRepository.Setup(r => r.GetCountAsync(null, null, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(entities.Count);
		_mockRepository.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(entities);

		// Act
		PagedResult<Receipt> actual = await _service.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		Assert.Equal(entities.Count, actual.Data.Count);
		Assert.Equal(entities.Count, actual.Total);
		Assert.Equal(0, actual.Offset);
		Assert.Equal(50, actual.Limit);
	}

	[Fact]
	public async Task GetAllAsync_WithAccountAndCardFilters_PassesThroughToRepository()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		List<ReceiptEntity> entities = ReceiptEntityGenerator.GenerateList(1);

		_mockRepository.Setup(r => r.GetCountAsync(accountId, cardId, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(entities.Count);
		_mockRepository.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), accountId, cardId, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(entities);

		// Act
		PagedResult<Receipt> actual = await _service.GetAllAsync(0, 50, SortParams.Default, accountId, cardId, null, null, CancellationToken.None);

		// Assert
		Assert.Equal(entities.Count, actual.Data.Count);
		_mockRepository.Verify(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), accountId, cardId, null, null, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetAllAsync_WithLocationFilter_PassesThroughToRepositoryGetCountAndGetAll()
	{
		// Arrange
		const string location = "Target";
		List<ReceiptEntity> entities = ReceiptEntityGenerator.GenerateList(1);

		_mockRepository.Setup(r => r.GetCountAsync(null, null, null, location, It.IsAny<CancellationToken>())).ReturnsAsync(entities.Count);
		_mockRepository.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, null, location, It.IsAny<CancellationToken>())).ReturnsAsync(entities);

		// Act
		PagedResult<Receipt> actual = await _service.GetAllAsync(0, 50, SortParams.Default, null, null, null, location, CancellationToken.None);

		// Assert
		Assert.Equal(entities.Count, actual.Data.Count);
		_mockRepository.Verify(r => r.GetCountAsync(null, null, null, location, It.IsAny<CancellationToken>()), Times.Once);
		_mockRepository.Verify(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, null, location, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetByIdAsync_ExistingId_ReturnsReceipt()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		ReceiptEntity entity = ReceiptEntityGenerator.Generate();

		_mockRepository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);

		// Act
		Receipt? actual = await _service.GetByIdAsync(id, CancellationToken.None);

		// Assert
		Assert.NotNull(actual);
		actual.Id.Should().Be(entity.Id);
	}

	[Fact]
	public async Task GetByIdAsync_NonExistingId_ReturnsNull()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mockRepository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((ReceiptEntity?)null);

		// Act
		Receipt? actual = await _service.GetByIdAsync(id, CancellationToken.None);

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
	public async Task UpdateAsync_ValidReceipts_CallsRepositoryUpdateAsync()
	{
		// Arrange
		List<Receipt> models = ReceiptGenerator.GenerateList(2);

		// Act
		await _service.UpdateAsync(models, CancellationToken.None);

		// Assert
		_mockRepository.Verify(r => r.UpdateAsync(It.IsAny<List<ReceiptEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
	}
}
