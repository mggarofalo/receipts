using Application.Commands.Subcategory.Update;
using Application.Interfaces.Services;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Commands.Subcategory;

public class UpdateSubcategoryCommandHandlerTests
{
	[Fact]
	public async Task UpdateSubcategoryCommandHandler_WithValidCommand_ReturnsTrueAndCallsUpdateAndSaveChanges()
	{
		Mock<ISubcategoryService> mockService = new();
		UpdateSubcategoryCommandHandler handler = new(mockService.Object);

		List<Domain.Core.Subcategory> input = SubcategoryGenerator.GenerateList(2);

		mockService.Setup(r => r
			.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		mockService.Setup(r => r
			.UpdateAsync(It.IsAny<List<Domain.Core.Subcategory>>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		UpdateSubcategoryCommand command = new(input);
		bool result = await handler.Handle(command, CancellationToken.None);

		Assert.True(result);
	}

	[Fact]
	public async Task UpdateSubcategoryCommandHandler_WithMissingSubcategory_ReturnsFalseAndNeverPersists()
	{
		Mock<ISubcategoryService> mockService = new();
		UpdateSubcategoryCommandHandler handler = new(mockService.Object);

		List<Domain.Core.Subcategory> input = SubcategoryGenerator.GenerateList(1);

		mockService.Setup(r => r
			.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		UpdateSubcategoryCommand command = new(input);
		bool result = await handler.Handle(command, CancellationToken.None);

		Assert.False(result);
		mockService.Verify(r => r.UpdateAsync(It.IsAny<List<Domain.Core.Subcategory>>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}
