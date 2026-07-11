using Application.Commands.Category.Update;
using Application.Interfaces.Services;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Commands.Category;

public class UpdateCategoryCommandHandlerTests
{
	[Fact]
	public async Task UpdateCategoryCommandHandler_WithValidCommand_ReturnsTrueAndCallsUpdateAndSaveChanges()
	{
		Mock<ICategoryService> mockService = new();
		UpdateCategoryCommandHandler handler = new(mockService.Object);

		List<Domain.Core.Category> input = CategoryGenerator.GenerateList(2);

		mockService.Setup(r => r
			.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		mockService.Setup(r => r
			.UpdateAsync(It.IsAny<List<Domain.Core.Category>>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		UpdateCategoryCommand command = new(input);
		bool result = await handler.Handle(command, CancellationToken.None);

		Assert.True(result);
	}

	[Fact]
	public async Task UpdateCategoryCommandHandler_WithMissingCategory_ReturnsFalseAndNeverPersists()
	{
		Mock<ICategoryService> mockService = new();
		UpdateCategoryCommandHandler handler = new(mockService.Object);

		List<Domain.Core.Category> input = CategoryGenerator.GenerateList(1);

		mockService.Setup(r => r
			.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		UpdateCategoryCommand command = new(input);
		bool result = await handler.Handle(command, CancellationToken.None);

		Assert.False(result);
		mockService.Verify(r => r.UpdateAsync(It.IsAny<List<Domain.Core.Category>>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}
