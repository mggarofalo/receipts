using Application.Interfaces.Services;
using Application.Models;
using Application.Models.CommittedChanges;
using Application.Models.Images;
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
	private readonly Mock<ICommittedChangePublisher> _publisher;
	private readonly ReceiptService _service;

	public ReceiptServiceTests()
	{
		_mockRepository = new Mock<IReceiptRepository>();
		_mapper = new ReceiptMapper();
		_publisher = new Mock<ICommittedChangePublisher>(MockBehavior.Strict);
		_service = new ReceiptService(_mockRepository.Object, _mapper, _publisher.Object);
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
	public async Task DeleteAsync_CascadeChangedSyncRecords_PublishesSilentCollectionChangeAfterRepositorySuccess()
	{
		// Arrange
		List<Guid> ids = [Guid.NewGuid(), Guid.NewGuid()];
		bool repositoryCompleted = false;
		_mockRepository.Setup(r => r.DeleteAsync(ids, It.IsAny<CancellationToken>()))
			.Callback(() => repositoryCompleted = true).ReturnsAsync(new CascadeMutationResult(true, 2));
		_publisher.Setup(p => p.PublishAsync(It.IsAny<CommittedEntityChange>()))
			.Callback(() => repositoryCompleted.Should().BeTrue()).Returns(Task.CompletedTask);

		// Act
		await _service.DeleteAsync(ids, CancellationToken.None);

		// Assert
		_mockRepository.Verify(r => r.DeleteAsync(ids, It.IsAny<CancellationToken>()), Times.Once);
		VerifyPublished(CommittedChangeType.Deleted, Times.Once());
	}

	[Fact]
	public async Task DeleteAsync_NoChangedSyncRecords_DoesNotPublish()
	{
		_mockRepository.Setup(r => r.DeleteAsync(It.IsAny<List<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CascadeMutationResult(false, 0));

		await _service.DeleteAsync([Guid.NewGuid()], CancellationToken.None);

		_publisher.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task DeleteAsync_RepositoryRollsBack_DoesNotPublish()
	{
		_mockRepository.Setup(r => r.DeleteAsync(It.IsAny<List<Guid>>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("rolled back"));

		Func<Task> act = async () => await _service.DeleteAsync([Guid.NewGuid()], CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>();
		_publisher.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData(true, 1, true)]
	[InlineData(true, 0, false)]
	[InlineData(false, 0, false)]
	public async Task RestoreAsync_PublishesOnlyWhenDurableCascadeChangedSyncRecords(
		bool restored, int changed, bool shouldPublish)
	{
		Guid id = Guid.NewGuid();
		_mockRepository.Setup(r => r.RestoreAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CascadeMutationResult(restored, changed));
		if (shouldPublish)
		{
			_publisher.Setup(p => p.PublishAsync(It.IsAny<CommittedEntityChange>())).Returns(Task.CompletedTask);
		}

		(await _service.RestoreAsync(id, CancellationToken.None)).Should().Be(restored);

		VerifyPublished(CommittedChangeType.Updated, shouldPublish ? Times.Once() : Times.Never());
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

		_mockRepository.Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entities.Count);
		_mockRepository.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>())).ReturnsAsync(entities);

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
		List<ReceiptListItem> items = CreateListItems(1);

		_mockRepository.Setup(r => r.GetCountAsync(accountId, cardId, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(items.Count);
		_mockRepository.Setup(r => r.GetListAsync(0, 50, It.IsAny<SortParams>(), accountId, cardId, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(items);

		// Act
		PagedResult<ReceiptListItem> actual = await _service.GetAllAsync(0, 50, SortParams.Default, accountId, cardId, null, null, CancellationToken.None);

		// Assert
		Assert.Equal(items.Count, actual.Data.Count);
		_mockRepository.Verify(r => r.GetListAsync(0, 50, It.IsAny<SortParams>(), accountId, cardId, null, null, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetAllAsync_WithLocationFilter_PassesThroughToRepositoryGetCountAndGetAll()
	{
		// Arrange
		const string location = "Target";
		List<ReceiptListItem> items = CreateListItems(1);

		_mockRepository.Setup(r => r.GetCountAsync(null, null, null, location, It.IsAny<CancellationToken>())).ReturnsAsync(items.Count);
		_mockRepository.Setup(r => r.GetListAsync(0, 50, It.IsAny<SortParams>(), null, null, null, location, It.IsAny<CancellationToken>())).ReturnsAsync(items);

		// Act
		PagedResult<ReceiptListItem> actual = await _service.GetAllAsync(0, 50, SortParams.Default, null, null, null, location, CancellationToken.None);

		// Assert
		Assert.Equal(items.Count, actual.Data.Count);
		_mockRepository.Verify(r => r.GetCountAsync(null, null, null, location, It.IsAny<CancellationToken>()), Times.Once);
		_mockRepository.Verify(r => r.GetListAsync(0, 50, It.IsAny<SortParams>(), null, null, null, location, It.IsAny<CancellationToken>()), Times.Once);
	}

	private static List<ReceiptListItem> CreateListItems(int count) =>
		[.. Enumerable.Range(0, count).Select(index => new ReceiptListItem(
			Guid.NewGuid(), $"Location {index}", new DateOnly(2026, 8, 30), 1m,
			2m, 3m, 6m, 6m, "balanced", 1, "Food", "Checking · Visa"))];

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

	[Fact]
	public async Task ReplaceImagePathsAsync_ReturnsRepositoryPreviousSet()
	{
		Guid id = Guid.NewGuid();
		ReceiptImageSet replacement = new("new/original.jpg", "new/processed.png");
		ReceiptImageSet previous = new("old/original.jpg", "old/processed.png");
		_mockRepository.Setup(r => r.ReplaceImagePathsAsync(id, replacement, It.IsAny<CancellationToken>()))
			.ReturnsAsync(previous);

		ReceiptImageSet? result = await _service.ReplaceImagePathsAsync(id, replacement, CancellationToken.None);

		result.Should().Be(previous);
	}

	private void VerifyPublished(CommittedChangeType changeType, Times times) =>
		_publisher.Verify(p => p.PublishAsync(It.Is<CommittedEntityChange>(change =>
			change.EntityType == CommittedEntityType.YnabSyncRecord
			&& change.ChangeType == changeType
			&& change.EntityId == null
			&& change.SuppressToast)), times);
}
