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

public class ReceiptItemServiceTests
{
	private readonly Mock<IReceiptItemRepository> _mockRepository;
	private readonly ReceiptItemMapper _mapper;
	private readonly ReceiptItemService _service;

	public ReceiptItemServiceTests()
	{
		_mockRepository = new Mock<IReceiptItemRepository>();
		_mapper = new ReceiptItemMapper();
		_service = new ReceiptItemService(_mockRepository.Object, _mapper);
	}

	[Fact]
	public async Task CreateAsync_ValidReceiptItems_CallsRepositoryCreateAsyncAndReturnsCreatedReceiptItems()
	{
		// Arrange
		List<ReceiptItem> models = ReceiptItemGenerator.GenerateList(2);
		Guid receiptId = Guid.NewGuid();
		List<ReceiptItemEntity> createdEntities = ReceiptItemEntityGenerator.GenerateList(2);

		_mockRepository.Setup(r => r.CreateAsync(It.IsAny<List<ReceiptItemEntity>>(), It.IsAny<CancellationToken>())).ReturnsAsync(createdEntities);

		// Act
		List<ReceiptItem> actual = await _service.CreateAsync(models, receiptId, CancellationToken.None);

		// Assert
		Assert.Equal(createdEntities.Count, actual.Count);
		_mockRepository.Verify(r => r.CreateAsync(It.IsAny<List<ReceiptItemEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
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
	public async Task GetAllAsync_ReturnsAllReceiptItems()
	{
		// Arrange
		List<ReceiptItemEntity> entities = ReceiptItemEntityGenerator.GenerateList(3);

		_mockRepository.Setup(r => r.GetCountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(entities.Count);
		_mockRepository.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, It.IsAny<CancellationToken>())).ReturnsAsync(entities);

		// Act
		PagedResult<ReceiptItem> actual = await _service.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		Assert.Equal(entities.Count, actual.Data.Count);
		Assert.Equal(entities.Count, actual.Total);
		Assert.Equal(0, actual.Offset);
		Assert.Equal(50, actual.Limit);
	}

	[Fact]
	public async Task GetByIdAsync_ExistingId_ReturnsReceiptItem()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		ReceiptItemEntity entity = ReceiptItemEntityGenerator.Generate();

		_mockRepository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);

		// Act
		ReceiptItem? actual = await _service.GetByIdAsync(id, CancellationToken.None);

		// Assert
		Assert.NotNull(actual);
		actual.Id.Should().Be(entity.Id);
	}

	[Fact]
	public async Task GetByIdAsync_NonExistingId_ReturnsNull()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mockRepository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((ReceiptItemEntity?)null);

		// Act
		ReceiptItem? actual = await _service.GetByIdAsync(id, CancellationToken.None);

		// Assert
		Assert.Null(actual);
	}

	[Fact]
	public async Task GetByReceiptIdAsync_ExistingReceiptId_ReturnsReceiptItems()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<ReceiptItemEntity> entities = ReceiptItemEntityGenerator.GenerateList(2);

		_mockRepository.Setup(r => r.GetByReceiptIdCountAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(entities.Count);
		_mockRepository.Setup(r => r.GetByReceiptIdAsync(receiptId, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>())).ReturnsAsync(entities);

		// Act
		PagedResult<ReceiptItem> actual = await _service.GetByReceiptIdAsync(receiptId, 0, 50, SortParams.Default, CancellationToken.None);

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
		PagedResult<ReceiptItem> actual = await _service.GetByReceiptIdAsync(receiptId, 0, 50, SortParams.Default, CancellationToken.None);

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
	public async Task UpdateAsync_ValidReceiptItems_CallsRepositoryUpdateAsync()
	{
		// Arrange
		List<ReceiptItem> models = ReceiptItemGenerator.GenerateList(2);
		Guid receiptId = Guid.NewGuid();

		// Act
		await _service.UpdateAsync(models, receiptId, CancellationToken.None);

		// Assert
		_mockRepository.Verify(r => r.UpdateAsync(It.Is<List<ReceiptItemEntity>>(e => e.All(ri => ri.ReceiptId == receiptId)), It.IsAny<CancellationToken>()), Times.Once);
	}
}
