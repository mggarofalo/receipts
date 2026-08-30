using Application.Exceptions;
using Application.Models;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Moq;

namespace Infrastructure.Tests.Services;

public class CategoryServiceTests
{
	private readonly Mock<ICategoryRepository> _mockRepository = new();
	private readonly CategoryService _service;

	public CategoryServiceTests()
	{
		_service = new CategoryService(_mockRepository.Object, new CategoryMapper());
	}

	[Fact]
	public async Task GetAllAsync_ForwardsSearchAndUsesFilteredTotal()
	{
		List<CategoryEntity> page = [new() { Id = Guid.NewGuid(), Name = "Food", IsActive = true }];
		_mockRepository.Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>(), true, "food")).ReturnsAsync(3);
		_mockRepository.Setup(r => r.GetAllAsync(1, 1, It.IsAny<SortParams>(), It.IsAny<CancellationToken>(), true, "food")).ReturnsAsync(page);

		PagedResult<Domain.Core.Category> result = await _service.GetAllAsync(1, 1, SortParams.Default, true, "food", CancellationToken.None);

		result.Data.Should().HaveCount(1);
		result.Total.Should().Be(3);
		_mockRepository.VerifyAll();
	}

	[Fact]
	public async Task RestoreAsync_WhenActiveNameConflict_ThrowsDuplicateEntityException_AndDoesNotRestore()
	{
		// Arrange — an active category already owns the trashed row's name (RECEIPTS-772).
		Guid id = Guid.NewGuid();
		_mockRepository
			.Setup(r => r.GetRestoreConflictNameAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync("Groceries");

		// Act
		Func<Task> act = async () => await _service.RestoreAsync(id, CancellationToken.None);

		// Assert — maps to a 409-mapped exception with the conflicting name, and never restores.
		(await act.Should().ThrowAsync<DuplicateEntityException>())
			.Which.Message.Should().Contain("Groceries");
		_mockRepository.Verify(r => r.RestoreAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task RestoreAsync_WhenNoConflict_DelegatesToRepository_ReturnsTrue()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mockRepository
			.Setup(r => r.GetRestoreConflictNameAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);
		_mockRepository
			.Setup(r => r.RestoreAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		bool actual = await _service.RestoreAsync(id, CancellationToken.None);

		// Assert
		actual.Should().BeTrue();
		_mockRepository.Verify(r => r.RestoreAsync(id, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task RestoreAsync_WhenNoConflictAndNotFound_ReturnsFalse()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mockRepository
			.Setup(r => r.GetRestoreConflictNameAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);
		_mockRepository
			.Setup(r => r.RestoreAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		bool actual = await _service.RestoreAsync(id, CancellationToken.None);

		// Assert
		actual.Should().BeFalse();
	}
}
