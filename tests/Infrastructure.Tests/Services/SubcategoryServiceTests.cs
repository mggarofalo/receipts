using Application.Exceptions;
using FluentAssertions;
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
