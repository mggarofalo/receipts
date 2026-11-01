using Application.Commands.NormalizedDescription.PromotePendingNames;
using Application.Commands.NormalizedDescription.UndoPendingNamePromotion;
using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Application.Queries.NormalizedDescription.ListPendingNamePromotions;
using Application.Queries.NormalizedDescription.PreviewPendingNamePromotion;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.NormalizedDescription.PromotePendingNames;

public class PendingNamePromotionHandlerTests
{
	private readonly Mock<IPendingNamePromotionService> _service = new();

	[Fact]
	public async Task Preview_ReturnsTheServicePreview()
	{
		PendingNamePromotionPreview preview = new(2, 7, "digest", [new(Guid.NewGuid(), "Oat milk", 7)]);
		_service.Setup(s => s.PreviewAsync(It.IsAny<CancellationToken>())).ReturnsAsync(preview);

		PendingNamePromotionPreview result = await new PreviewPendingNamePromotionQueryHandler(_service.Object)
			.Handle(new PreviewPendingNamePromotionQuery(), CancellationToken.None);

		result.Should().BeSameAs(preview);
	}

	[Fact]
	public async Task Promote_PassesTheActorAndFingerprint()
	{
		PendingNamePromotionResult promoted = new(Guid.NewGuid(), 3, 12);
		_service.Setup(s => s.PromoteAsync("admin-1", "digest", It.IsAny<CancellationToken>())).ReturnsAsync(promoted);

		PendingNamePromotionResult? result = await new PromotePendingNamesCommandHandler(_service.Object)
			.Handle(new PromotePendingNamesCommand("admin-1", "digest"), CancellationToken.None);

		result.Should().BeSameAs(promoted);
	}

	[Fact]
	public async Task Promote_ChangedSet_ReturnsNull()
	{
		_service.Setup(s => s.PromoteAsync("admin-1", "stale", It.IsAny<CancellationToken>())).ReturnsAsync((PendingNamePromotionResult?)null);

		PendingNamePromotionResult? result = await new PromotePendingNamesCommandHandler(_service.Object)
			.Handle(new PromotePendingNamesCommand("admin-1", "stale"), CancellationToken.None);

		result.Should().BeNull();
	}

	[Fact]
	public async Task List_ReturnsTheServiceHistory()
	{
		PendingNamePromotion[] history = [new(Guid.NewGuid(), "admin-1", DateTimeOffset.UnixEpoch, 3, 12, null, null, null, null)];
		_service.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(history);

		IReadOnlyList<PendingNamePromotion> result = await new ListPendingNamePromotionsQueryHandler(_service.Object)
			.Handle(new ListPendingNamePromotionsQuery(), CancellationToken.None);

		result.Should().Equal(history);
	}

	[Fact]
	public async Task Undo_PassesTheActorAndPromotion()
	{
		Guid id = Guid.NewGuid();
		PendingNamePromotionUndo undo = new(PendingNamePromotionUndoOutcome.Undone, 2, 1);
		_service.Setup(s => s.UndoAsync("admin-1", id, It.IsAny<CancellationToken>())).ReturnsAsync(undo);

		PendingNamePromotionUndo result = await new UndoPendingNamePromotionCommandHandler(_service.Object)
			.Handle(new UndoPendingNamePromotionCommand("admin-1", id), CancellationToken.None);

		result.Should().Be(undo);
	}
}
