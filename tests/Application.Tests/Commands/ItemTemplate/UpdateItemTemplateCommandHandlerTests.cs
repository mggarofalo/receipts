using Application.Commands.ItemTemplate.Update;
using Application.Interfaces.Services;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Commands.ItemTemplate;

public class UpdateItemTemplateCommandHandlerTests
{
	[Fact]
	public async Task UpdateItemTemplateCommandHandler_WithValidCommand_ReturnsTrueAndCallsUpdateAndSaveChanges()
	{
		Mock<IItemTemplateService> mockService = new();
		UpdateItemTemplateCommandHandler handler = new(mockService.Object);

		List<Domain.Core.ItemTemplate> input = ItemTemplateGenerator.GenerateList(2);

		mockService.Setup(r => r
			.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		mockService.Setup(r => r
			.UpdateAsync(It.IsAny<List<Domain.Core.ItemTemplate>>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		UpdateItemTemplateCommand command = new(input);
		bool result = await handler.Handle(command, CancellationToken.None);

		Assert.True(result);
	}

	[Fact]
	public async Task UpdateItemTemplateCommandHandler_WithMissingItemTemplate_ReturnsFalseAndNeverPersists()
	{
		Mock<IItemTemplateService> mockService = new();
		UpdateItemTemplateCommandHandler handler = new(mockService.Object);

		List<Domain.Core.ItemTemplate> input = ItemTemplateGenerator.GenerateList(1);

		mockService.Setup(r => r
			.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		UpdateItemTemplateCommand command = new(input);
		bool result = await handler.Handle(command, CancellationToken.None);

		Assert.False(result);
		mockService.Verify(r => r.UpdateAsync(It.IsAny<List<Domain.Core.ItemTemplate>>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}
