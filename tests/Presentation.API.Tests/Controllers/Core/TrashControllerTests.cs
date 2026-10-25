using API.Controllers.Core;
using API.Services;
using Application.Commands.Trash.Purge;
using Application.Interfaces.Services;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Moq;

namespace Presentation.API.Tests.Controllers.Core;

public class TrashControllerTests
{
	private readonly Mock<IMediator> _mediatorMock;
	private readonly Mock<ILogger<TrashController>> _loggerMock;
	private readonly Mock<IEntityChangeNotifier> _notifierMock;
	private readonly TrashController _controller;

	public TrashControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_loggerMock = ControllerTestHelpers.GetLoggerMock<TrashController>();
		_notifierMock = new Mock<IEntityChangeNotifier>();
		_controller = new TrashController(_mediatorMock.Object, _loggerMock.Object, _notifierMock.Object);
	}

	[Fact]
	public async Task PurgeTrash_ReturnsRetainedCountsAndReason()
	{
		PurgeTrashResult retained = new(0, 0, "Classification history retained", 1);
		_mediatorMock.Setup(m => m.Send(It.IsAny<PurgeTrashCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(retained);
		var result = await _controller.PurgeTrash();
		Assert.Equal(retained, Assert.IsType<Ok<PurgeTrashResult>>(result.Result).Value);
	}

	[Fact]
	public async Task PurgeTrash_ReturnsNoContent_AndNotifiesAllEntityTypes()
	{
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<PurgeTrashCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PurgeTrashResult(0, 0, ""));

		var result = await _controller.PurgeTrash();

		Assert.IsType<NoContent>(result.Result);

		string[] expectedEntityTypes = ["receipt", "receipt-item", "transaction", "adjustment", "account", "category", "subcategory", "item-template"];
		foreach (string entityType in expectedEntityTypes)
		{
			_notifierMock.Verify(n => n.NotifyAllChanged(entityType, "deleted"), Times.Once);
		}
	}

	[Fact]
	public async Task PurgeTrash_ThrowsException_WhenMediatorFails()
	{
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<PurgeTrashCommand>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.PurgeTrash();

		await act.Should().ThrowAsync<Exception>();
	}
}
