using System.Security.Claims;
using API.Controllers;
using API.Generated.Dtos;
using API.Http;
using Application.Commands.NormalizedDescription.PromotePendingNames;
using Application.Commands.NormalizedDescription.UndoPendingNamePromotion;
using Application.Models.NormalizedDescriptions;
using Application.Queries.NormalizedDescription.ListPendingNamePromotions;
using Application.Queries.NormalizedDescription.PreviewPendingNamePromotion;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Presentation.API.Tests.Controllers;

// RECEIPTS-1107: bulk promotion of consistent pending-review names.
public class NormalizedDescriptionsControllerPromotionTests
{
	private const string Admin = "admin-1";
	private readonly Mock<IMediator> _mediator = new();
	private readonly NormalizedDescriptionsController _controller;

	public NormalizedDescriptionsControllerPromotionTests()
	{
		_controller = new NormalizedDescriptionsController(_mediator.Object)
		{
			ControllerContext = new ControllerContext
			{
				HttpContext = new DefaultHttpContext
				{
					User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Admin)], "test")),
				},
			},
		};
	}

	[Fact]
	public async Task Preview_MapsTheEligibleNames()
	{
		Guid id = Guid.NewGuid();
		_mediator.Setup(m => m.Send(It.IsAny<PreviewPendingNamePromotionQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PendingNamePromotionPreview(2, 9, "digest", [new(id, "Oat milk", 9)]));

		Ok<PendingNamePromotionPreviewResponse> result = await _controller.PreviewPendingNamePromotion(CancellationToken.None);

		result.Value!.MinimumPurchases.Should().Be(2);
		result.Value.NameCount.Should().Be(1);
		result.Value.PurchaseCount.Should().Be(9);
		result.Value.Fingerprint.Should().Be("digest");
		result.Value.Names.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = id, CanonicalName = "Oat milk", PurchaseCount = 9 });
	}

	[Fact]
	public async Task Promote_SendsTheSignedInActorAndMapsTheResult()
	{
		Guid promotionId = Guid.NewGuid();
		_mediator.Setup(m => m.Send(It.Is<PromotePendingNamesCommand>(c => c.Actor == Admin && c.ExpectedFingerprint == "digest"), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PendingNamePromotionResult(promotionId, 3, 12));

		var result = await _controller.PromotePendingNames(new PromotePendingNamesRequest { ExpectedFingerprint = "digest" }, CancellationToken.None);

		Ok<PromotePendingNamesResponse> ok = Assert.IsType<Ok<PromotePendingNamesResponse>>(result.Result);
		ok.Value!.PromotionId.Should().Be(promotionId);
		ok.Value.PromotedCount.Should().Be(3);
		ok.Value.PurchaseCount.Should().Be(12);
	}

	[Fact]
	public async Task Promote_ChangedSet_ReturnsConflict()
	{
		_mediator.Setup(m => m.Send(It.IsAny<PromotePendingNamesCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((PendingNamePromotionResult?)null);

		var result = await _controller.PromotePendingNames(new PromotePendingNamesRequest { ExpectedFingerprint = "digest" }, CancellationToken.None);

		ConflictProblem conflict = Assert.IsType<ConflictProblem>(result.Result);
		conflict.Value!.Detail.Should().Be(NormalizedDescriptionsController.PromotableSetChanged);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task Promote_MissingFingerprint_ReturnsBadRequestWithoutDispatching(string fingerprint)
	{
		var result = await _controller.PromotePendingNames(new PromotePendingNamesRequest { ExpectedFingerprint = fingerprint }, CancellationToken.None);

		BadRequestProblem bad = Assert.IsType<BadRequestProblem>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.ExpectedFingerprintRequired);
		_mediator.Verify(m => m.Send(It.IsAny<PromotePendingNamesCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task List_MapsTheHistory()
	{
		Guid id = Guid.NewGuid();
		DateTimeOffset at = DateTimeOffset.UnixEpoch;
		_mediator.Setup(m => m.Send(It.IsAny<ListPendingNamePromotionsQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PendingNamePromotion[] { new(id, Admin, at, 3, 12, at.AddHours(1), Admin, 2, 1) });

		Ok<List<PendingNamePromotionResponse>> result = await _controller.ListPendingNamePromotions(CancellationToken.None);

		result.Value.Should().ContainSingle().Which.Should().BeEquivalentTo(new
		{
			Id = id,
			CreatedBy = Admin,
			CreatedAt = at,
			PromotedCount = 3,
			PurchaseCount = 12,
			UndoneAt = (DateTimeOffset?)at.AddHours(1),
			UndoneBy = Admin,
			RestoredCount = (int?)2,
			KeptCount = (int?)1,
		});
	}

	[Fact]
	public async Task Undo_ReturnsTheRestoredAndKeptCounts()
	{
		Guid id = Guid.NewGuid();
		_mediator.Setup(m => m.Send(It.Is<UndoPendingNamePromotionCommand>(c => c.Actor == Admin && c.PromotionId == id), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PendingNamePromotionUndo(PendingNamePromotionUndoOutcome.Undone, 2, 1));

		var result = await _controller.UndoPendingNamePromotion(id, CancellationToken.None);

		Ok<UndoPendingNamePromotionResponse> ok = Assert.IsType<Ok<UndoPendingNamePromotionResponse>>(result.Result);
		ok.Value!.RestoredCount.Should().Be(2);
		ok.Value.KeptCount.Should().Be(1);
	}

	[Fact]
	public async Task Undo_UnknownPromotion_ReturnsNotFound()
	{
		_mediator.Setup(m => m.Send(It.IsAny<UndoPendingNamePromotionCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PendingNamePromotionUndo(PendingNamePromotionUndoOutcome.NotFound, 0, 0));

		var result = await _controller.UndoPendingNamePromotion(Guid.NewGuid(), CancellationToken.None);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task Undo_AlreadyUndone_ReturnsConflict()
	{
		_mediator.Setup(m => m.Send(It.IsAny<UndoPendingNamePromotionCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PendingNamePromotionUndo(PendingNamePromotionUndoOutcome.AlreadyUndone, 0, 0));

		var result = await _controller.UndoPendingNamePromotion(Guid.NewGuid(), CancellationToken.None);

		ConflictProblem conflict = Assert.IsType<ConflictProblem>(result.Result);
		conflict.Value!.Detail.Should().Be(NormalizedDescriptionsController.PromotionAlreadyUndone);
	}
}
