using API.Controllers.Aggregates;
using API.Controllers.Core;
using API.Generated.Dtos;
using API.Http;
using API.Mapping.Core;
using API.Services;
using Application.Commands.Reports;
using Application.Interfaces.Services;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace Presentation.API.Tests.Controllers;

/// <summary>
/// RECEIPTS-886. The per-endpoint tests assert on <c>Detail</c>, which would still pass if
/// the surrounding document were malformed — no status, no title, wrong shape entirely.
/// These pin the envelope itself, one per status the API raises with a reason, so a
/// regression in <see cref="ApiProblem"/> fails here rather than silently shipping bodies
/// that every consumer has to special-case again.
/// </summary>
public class ProblemDetailsContractTests
{
	private const string BadRequestType = "https://tools.ietf.org/html/rfc9110#section-15.5.1";
	private const string NotFoundType = "https://tools.ietf.org/html/rfc9110#section-15.5.5";
	private const string ConflictType = "https://tools.ietf.org/html/rfc9110#section-15.5.10";

	[Fact]
	public async Task Rejected400_CarriesAWholeProblemDocument_NotJustAMessage()
	{
		CardsController controller = BuildCardsController(out _, out _);

		Results<Ok<CardListResponse>, BadRequest<ProblemDetails>> result =
			await controller.GetAllCards(null, null, -1, 50, null, null);

		ProblemDetails problem = Assert.IsType<BadRequest<ProblemDetails>>(result.Result).Value!;
		problem.Status.Should().Be(StatusCodes.Status400BadRequest);
		problem.Title.Should().Be("Bad Request");
		problem.Type.Should().Be(BadRequestType);
		problem.Detail.Should().Be("offset must be >= 0");
	}

	[Fact]
	public async Task Rejected409_CarriesAWholeProblemDocument_AndItsCountAsAnExtension()
	{
		CardsController controller = BuildCardsController(out Mock<ICardService> cardService, out _);
		Guid cardId = Guid.NewGuid();
		cardService
			.Setup(s => s.GetTransactionCountByCardIdAsync(cardId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(3);

		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await controller.DeleteCard(cardId);

		ProblemDetails problem = Assert.IsType<Conflict<ProblemDetails>>(result.Result).Value!;
		problem.Status.Should().Be(StatusCodes.Status409Conflict);
		problem.Title.Should().Be("Conflict");
		problem.Type.Should().Be(ConflictType);
		problem.Detail.Should().Be("Cannot delete — 3 transaction(s) reference this card");

		// The count rides as an RFC 9457 extension member, which serialises at the top level
		// of the body — the same place the client read it from when this was an ad-hoc
		// `{ message, transactionCount }` object. Only the prose moved.
		problem.Extensions.Should().ContainKey("transactionCount");
		problem.Extensions["transactionCount"].Should().Be(3);
	}

	[Fact]
	public async Task Rejected404WithAReason_CarriesAWholeProblemDocument()
	{
		Mock<IMediator> mediator = new();
		mediator
			.Setup(m => m.Send(It.IsAny<AcceptDuplicateGroupCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException("Receipt 123 not found"));

		ReportsController controller = new(mediator.Object);

		Results<Ok<AcceptDuplicateGroupResponse>, NotFound<ProblemDetails>> result =
			await controller.AcceptDuplicateGroup(
				new AcceptDuplicateGroupRequest { ReceiptIds = [Guid.NewGuid(), Guid.NewGuid()] },
				CancellationToken.None);

		ProblemDetails problem = Assert.IsType<NotFound<ProblemDetails>>(result.Result).Value!;
		problem.Status.Should().Be(StatusCodes.Status404NotFound);
		problem.Title.Should().Be("Not Found");
		problem.Type.Should().Be(NotFoundType);
		problem.Detail.Should().Be("Receipt 123 not found");
	}

	[Fact]
	public void ABatchOfRejectionReasons_IsJoinedIntoDetail_NotSerialisedAsAnArray()
	{
		// ASP.NET Identity hands back several messages at once. As a bare JSON array they
		// reached no consumer at all: the client's normaliser spreads an array into
		// {0: "…", 1: "…"}, finds no detail and no title, and falls back to a generic
		// status message — so the user was told nothing about why.
		BadRequest<ProblemDetails> result = ApiProblem.BadRequest(
			["Passwords must have at least one digit.", "Passwords must have at least one uppercase character."]);

		ProblemDetails problem = result.Value!;
		problem.Status.Should().Be(StatusCodes.Status400BadRequest);
		problem.Detail.Should().Be(
			"Passwords must have at least one digit. Passwords must have at least one uppercase character.");
	}

	private static CardsController BuildCardsController(
		out Mock<ICardService> cardService,
		out Mock<IEntityChangeNotifier> notifier)
	{
		Mock<IMediator> mediator = new();
		cardService = new Mock<ICardService>();
		notifier = new Mock<IEntityChangeNotifier>();
		Mock<IAccountService> accountService = new();
		accountService
			.Setup(s => s.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		return new CardsController(
			mediator.Object,
			new CardMapper(),
			ControllerTestHelpers.GetLoggerMock<CardsController>().Object,
			notifier.Object,
			cardService.Object,
			accountService.Object)
		{
			ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
		};
	}
}
