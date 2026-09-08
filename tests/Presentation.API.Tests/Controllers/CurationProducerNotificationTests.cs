using API.Controllers;
using API.Controllers.Aggregates;
using API.Generated.Dtos;
using API.Services;
using Application.Commands.NormalizedDescription.LinkTemplate;
using Application.Commands.NormalizedDescription.Merge;
using Application.Commands.NormalizedDescription.Rename;
using Application.Commands.NormalizedDescription.RequeuePending;
using Application.Commands.NormalizedDescription.Split;
using Application.Commands.NormalizedDescription.UpdateSettings;
using Application.Commands.NormalizedDescription.UpdateStatus;
using Application.Commands.Reports;
using Application.Models.NormalizedDescriptions;
using Application.Queries.NormalizedDescription.GetById;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using DomainStatus = Domain.NormalizedDescriptions.NormalizedDescriptionStatus;
using DtoStatus = API.Generated.Dtos.NormalizedDescriptionStatus;

namespace Presentation.API.Tests.Controllers;

public class CurationProducerNotificationTests
{
	private readonly Mock<IMediator> _mediator = new();
	private readonly Mock<IEntityChangeNotifier> _notifier = new();
	private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource<bool> _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Guid _id = Guid.NewGuid();
	private bool _commandCompleted;
	private IResult? _response;

	private IStatusCodeHttpResult ResponseStatus() =>
		(IStatusCodeHttpResult)(_response is INestedHttpResult nested ? nested.Result : _response!);

	private async ValueTask<T> CompleteCommand<T>(T value)
	{
		_started.TrySetResult(true);
		await _complete.Task;
		_commandCompleted = true;
		return value;
	}

	private Func<Task> Configure(string operation, bool invalidProjection = false)
	{
		ServiceCollection services = new();
		services.AddSingleton(_mediator.Object);
		services.AddSingleton(_notifier.Object);
		using ServiceProvider provider = services.BuildServiceProvider();
		// DI activation lets this regression compile against both the old constructor
		// and the notifier-injected constructor without a test-only production seam.
		NormalizedDescriptionsController normalized = ActivatorUtilities.CreateInstance<NormalizedDescriptionsController>(provider);
		ReportsController reports = ActivatorUtilities.CreateInstance<ReportsController>(provider);
		NormalizedDescriptionDetail detail = new(
			new NormalizedDescription(_id, "Milk", invalidProjection ? (DomainStatus)999 : DomainStatus.Active, DateTimeOffset.UtcNow),
			1, null, ["MILK"]);
		_notifier.Setup(n => n.NotifyAllChanged(It.IsAny<string>(), It.IsAny<string>()))
			.Callback(() => _commandCompleted.Should().BeTrue("the producer must complete before publishing repair"))
			.Returns(Task.CompletedTask);

		switch (operation)
		{
			case "merge":
				_mediator.Setup(m => m.Send(It.IsAny<MergeNormalizedDescriptionsCommand>(), It.IsAny<CancellationToken>()))
					.Returns(() => CompleteCommand(1));
				return async () => _response = await normalized.MergeNormalizedDescriptions(_id, new MergeNormalizedDescriptionRequest { DiscardId = Guid.NewGuid() }, CancellationToken.None);
			case "split":
				_mediator.Setup(m => m.Send(It.IsAny<SplitNormalizedDescriptionCommand>(), It.IsAny<CancellationToken>()))
					.Returns(() => CompleteCommand(detail));
				return async () => _response = await normalized.SplitNormalizedDescription(_id, new SplitNormalizedDescriptionRequest { ReceiptItemIds = [Guid.NewGuid()], CanonicalName = "Milk" }, CancellationToken.None);
			case "rename":
				_mediator.Setup(m => m.Send(It.IsAny<RenameNormalizedDescriptionCommand>(), It.IsAny<CancellationToken>()))
					.Returns(() => CompleteCommand(detail));
				return async () => _response = await normalized.RenameNormalizedDescription(_id, new RenameNormalizedDescriptionRequest { DisplayLabel = "Whole Milk" }, CancellationToken.None);
			case "link":
				_mediator.Setup(m => m.Send(It.IsAny<LinkItemTemplateCommand>(), It.IsAny<CancellationToken>()))
					.Returns(() => CompleteCommand(new LinkTemplateResult(detail, 1, true)));
				return async () => _response = await normalized.LinkItemTemplateToNormalizedDescription(_id, new LinkItemTemplateRequest { ItemTemplateId = Guid.NewGuid() }, CancellationToken.None);
			case "status":
				_mediator.Setup(m => m.Send(It.IsAny<GetNormalizedDescriptionByIdQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(detail);
				_mediator.Setup(m => m.Send(It.IsAny<UpdateNormalizedDescriptionStatusCommand>(), It.IsAny<CancellationToken>()))
					.Returns(() => CompleteCommand(true));
				return async () => _response = await normalized.UpdateNormalizedDescriptionStatus(_id, new UpdateNormalizedDescriptionStatusRequest { Status = DtoStatus.Rejected }, CancellationToken.None);
			case "settings":
				_mediator.Setup(m => m.Send(It.IsAny<UpdateNormalizedDescriptionSettingsCommand>(), It.IsAny<CancellationToken>()))
					.Returns(() => CompleteCommand(new NormalizedDescriptionSettings(_id, 0.9, 0.5, DateTimeOffset.UtcNow)));
				return async () => _response = await normalized.UpdateSettings(new UpdateNormalizedDescriptionSettingsRequest { AutoAcceptThreshold = 0.9, PendingReviewThreshold = 0.5 }, CancellationToken.None);
			case "requeue":
				_mediator.Setup(m => m.Send(It.IsAny<RequeuePendingCommand>(), It.IsAny<CancellationToken>()))
					.Returns(() => CompleteCommand<RequeuePendingResult?>(new(1, 1, 1)));
				return async () => _response = await normalized.RequeuePending(new RequeuePendingRequest { ExpectedFingerprint = "snapshot" }, CancellationToken.None);
			case "accept":
				_mediator.Setup(m => m.Send(It.IsAny<AcceptDuplicateGroupCommand>(), It.IsAny<CancellationToken>()))
					.Returns(() => CompleteCommand(1));
				return async () => _response = await reports.AcceptDuplicateGroup(new AcceptDuplicateGroupRequest { ReceiptIds = [_id, Guid.NewGuid()] }, CancellationToken.None);
			case "unaccept":
				_mediator.Setup(m => m.Send(It.IsAny<UnacceptDuplicateGroupCommand>(), It.IsAny<CancellationToken>()))
					.Returns(() => CompleteCommand(1));
				return async () => _response = await reports.UnacceptDuplicateGroup(new UnacceptDuplicateGroupRequest { ReceiptIds = [_id, Guid.NewGuid()] }, CancellationToken.None);
			default:
				throw new ArgumentOutOfRangeException(nameof(operation));
		}
	}

	[Theory]
	[InlineData("merge")]
	[InlineData("split")]
	[InlineData("rename")]
	[InlineData("link")]
	[InlineData("status")]
	[InlineData("settings")]
	[InlineData("requeue")]
	[InlineData("accept")]
	[InlineData("unaccept")]
	public async Task SuccessfulProducer_QueuesOneNeutralRepairOnlyAfterCommandCompletion(string operation)
	{
		Func<Task> invoke = Configure(operation);
		Task running = invoke();
		await _started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		_notifier.Invocations.Should().BeEmpty();
		running.IsCompleted.Should().BeFalse();
		_complete.SetResult(true);
		await running;
		_commandCompleted.Should().BeTrue();
		IStatusCodeHttpResult result = ResponseStatus();
		result.StatusCode.Should().BeOneOf(200, 204);
		string domain = operation switch
		{
			"accept" or "unaccept" => "duplicate-acceptance",
			"settings" => "normalized-description-settings",
			_ => "normalized-description",
		};
		_notifier.Verify(n => n.NotifyAllChanged(domain, "updated"), Times.Once);
		_notifier.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData("split")]
	[InlineData("rename")]
	[InlineData("link")]
	public async Task CompletedCommand_ResponseProjectionFails_StillQueuesRepair(string operation)
	{
		Func<Task> invoke = Configure(operation, invalidProjection: true);
		Task running = invoke();
		await _started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		_notifier.Invocations.Should().BeEmpty();
		_complete.SetResult(true);
		Exception? error = await Record.ExceptionAsync(() => running);
		_commandCompleted.Should().BeTrue();
		if (error is null)
		{
			IStatusCodeHttpResult result = ResponseStatus();
			result.StatusCode.Should().BeOneOf(400, 409);
		}
		else
		{
			error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("Unhandled status value");
		}
		_notifier.Verify(n => n.NotifyAllChanged("normalized-description", "updated"), Times.Once);
		_notifier.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData("merge")]
	[InlineData("accept")]
	public async Task RejectedCommand_DoesNotQueueRepair(string operation)
	{
		Func<Task> invoke = Configure(operation);
		Task running = invoke();
		await _started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		_complete.SetException(new KeyNotFoundException("No committed change"));
		await running;
		_commandCompleted.Should().BeFalse();
		IStatusCodeHttpResult result = ResponseStatus();
		result.StatusCode.Should().Be(404);
		_notifier.Invocations.Should().BeEmpty();
	}
}
