using Application.Exceptions;
using Application.Models;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Moq;

namespace Infrastructure.Tests.Services;

public class SubcategoryServiceTests
{
	private readonly Mock<ISubcategoryRepository> _mockRepository = new();
	private readonly SubcategoryService _service;

	public SubcategoryServiceTests()
	{
		_service = new SubcategoryService(_mockRepository.Object, new SubcategoryMapper());
	}

	[Fact]
	public async Task GetByCategoryIdAsync_ForwardsSearchAndUsesFilteredTotal()
	{
		Guid categoryId = Guid.NewGuid();
		List<SubcategoryEntity> page = [new() { Id = Guid.NewGuid(), CategoryId = categoryId, Name = "Dairy", IsActive = true }];
		_mockRepository.Setup(r => r.GetByCategoryIdCountAsync(categoryId, It.IsAny<CancellationToken>(), true, "dairy")).ReturnsAsync(4);
		_mockRepository.Setup(r => r.GetByCategoryIdAsync(categoryId, 2, 1, It.IsAny<SortParams>(), It.IsAny<CancellationToken>(), true, "dairy")).ReturnsAsync(page);

		PagedResult<Domain.Core.Subcategory> result = await _service.GetByCategoryIdAsync(categoryId, 2, 1, SortParams.Default, true, "dairy", CancellationToken.None);

		result.Data.Should().HaveCount(1);
		result.Total.Should().Be(4);
		_mockRepository.VerifyAll();
	}

	[Fact]
	public async Task RestoreAsync_WhenActiveNameConflict_ThrowsDuplicateEntityException_AndDoesNotRestore()
	{
		// Arrange — an active subcategory already owns the trashed row's (CategoryId, Name)
		// natural key (RECEIPTS-772).
		Guid id = Guid.NewGuid();
		_mockRepository
			.Setup(r => r.GetRestoreConflictNameAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync("Produce");

		// Act
		Func<Task> act = async () => await _service.RestoreAsync(id, CancellationToken.None);

		// Assert
		(await act.Should().ThrowAsync<DuplicateEntityException>())
			.Which.Message.Should().Contain("Produce");
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
