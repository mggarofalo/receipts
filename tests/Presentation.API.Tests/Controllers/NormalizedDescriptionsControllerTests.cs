using API.Controllers;
using API.Generated.Dtos;
using Application.Commands.NormalizedDescription.LinkTemplate;
using Application.Commands.NormalizedDescription.Merge;
using Application.Commands.NormalizedDescription.Rename;
using Application.Commands.NormalizedDescription.RequeuePending;
using Application.Commands.NormalizedDescription.Split;
using Application.Commands.NormalizedDescription.UpdateSettings;
using Application.Commands.NormalizedDescription.UpdateStatus;
using Application.Models;
using Application.Models.NormalizedDescriptions;
using Application.Queries.NormalizedDescription.GetAll;
using Application.Queries.NormalizedDescription.GetById;
using Application.Queries.NormalizedDescription.GetSettings;
using Application.Queries.NormalizedDescription.PreviewRequeuePending;
using Application.Queries.NormalizedDescription.PreviewThresholdImpact;
using Application.Queries.NormalizedDescription.TestMatch;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using DomainStatus = Domain.NormalizedDescriptions.NormalizedDescriptionStatus;
using DtoStatus = API.Generated.Dtos.NormalizedDescriptionStatus;

namespace Presentation.API.Tests.Controllers;

public class NormalizedDescriptionsControllerTests
{
	private readonly Mock<IMediator> _mediatorMock;
	private readonly NormalizedDescriptionsController _controller;

	public NormalizedDescriptionsControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_controller = new NormalizedDescriptionsController(_mediatorMock.Object);
	}

	// ── GET settings ────────────────────────────────────────────

	[Fact]
	public async Task GetSettings_ReturnsOkWithMappedResponse()
	{
		NormalizedDescriptionSettings settings = new(
			Guid.NewGuid(), 0.81, 0.68,
			new DateTimeOffset(2026, 4, 19, 0, 0, 0, TimeSpan.Zero));

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<GetNormalizedDescriptionSettingsQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(settings);

		Ok<NormalizedDescriptionSettingsResponse> result = await _controller.GetSettings(CancellationToken.None);

		result.Value!.Id.Should().Be(settings.Id);
		result.Value.AutoAcceptThreshold.Should().Be(0.81);
		result.Value.PendingReviewThreshold.Should().Be(0.68);
	}

	// ── PATCH settings ──────────────────────────────────────────

	[Fact]
	public async Task UpdateSettings_ValidRequest_ReturnsOkWithUpdatedValues()
	{
		UpdateNormalizedDescriptionSettingsRequest request = new()
		{
			AutoAcceptThreshold = 0.9,
			PendingReviewThreshold = 0.5,
		};

		NormalizedDescriptionSettings updated = new(
			Guid.NewGuid(), 0.9, 0.5,
			new DateTimeOffset(2026, 4, 19, 0, 0, 0, TimeSpan.Zero));

		_mediatorMock
			.Setup(m => m.Send(
				It.Is<UpdateNormalizedDescriptionSettingsCommand>(c => c.AutoAcceptThreshold == 0.9 && c.PendingReviewThreshold == 0.5),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(updated);

		Results<Ok<NormalizedDescriptionSettingsResponse>, BadRequest<ProblemDetails>> result =
			await _controller.UpdateSettings(request, CancellationToken.None);

		Ok<NormalizedDescriptionSettingsResponse> ok = Assert.IsType<Ok<NormalizedDescriptionSettingsResponse>>(result.Result);
		ok.Value!.AutoAcceptThreshold.Should().Be(0.9);
		ok.Value.PendingReviewThreshold.Should().Be(0.5);
	}

	[Theory]
	[InlineData(-0.01, 0.5, NormalizedDescriptionsController.AutoAcceptOutOfRange)]
	[InlineData(1.01, 0.5, NormalizedDescriptionsController.AutoAcceptOutOfRange)]
	[InlineData(0.8, -0.01, NormalizedDescriptionsController.PendingReviewOutOfRange)]
	[InlineData(0.8, 1.01, NormalizedDescriptionsController.PendingReviewOutOfRange)]
	[InlineData(0.5, 0.6, NormalizedDescriptionsController.PendingMustBeLessThanAuto)]
	[InlineData(0.5, 0.5, NormalizedDescriptionsController.PendingMustBeLessThanAuto)]
	public async Task UpdateSettings_InvalidRequest_ReturnsBadRequest(double autoAccept, double pendingReview, string expectedMessage)
	{
		UpdateNormalizedDescriptionSettingsRequest request = new()
		{
			AutoAcceptThreshold = autoAccept,
			PendingReviewThreshold = pendingReview,
		};

		Results<Ok<NormalizedDescriptionSettingsResponse>, BadRequest<ProblemDetails>> result =
			await _controller.UpdateSettings(request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(expectedMessage);

		// Should short-circuit without invoking the mediator.
		_mediatorMock.Verify(m => m.Send(It.IsAny<UpdateNormalizedDescriptionSettingsCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	// ── POST test ───────────────────────────────────────────────

	[Fact]
	public async Task TestMatch_ValidRequest_ReturnsOkWithCandidates()
	{
		TestMatchRequest request = new()
		{
			Description = "whole milk",
			TopN = 3,
			AutoAcceptThresholdOverride = 0.85,
			PendingReviewThresholdOverride = 0.55,
		};

		Guid candidateId = Guid.NewGuid();
		MatchTestResult serviceResult = new(
			Candidates: [new MatchCandidate(candidateId, "Whole Milk", 0.92, "Active")],
			SimulatedOutcome: MatchTestOutcomes.AutoAccept,
			SimulatedTargetId: candidateId);

		_mediatorMock
			.Setup(m => m.Send(
				It.Is<TestNormalizedDescriptionMatchQuery>(q =>
					q.Description == "whole milk" &&
					q.TopN == 3 &&
					q.AutoAcceptThresholdOverride == 0.85 &&
					q.PendingReviewThresholdOverride == 0.55),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(serviceResult);

		Results<Ok<MatchTestResultResponse>, BadRequest<ProblemDetails>> result =
			await _controller.TestMatch(request, CancellationToken.None);

		Ok<MatchTestResultResponse> ok = Assert.IsType<Ok<MatchTestResultResponse>>(result.Result);
		ok.Value!.SimulatedOutcome.Should().Be("AutoAccept");
		ok.Value.SimulatedTargetId.Should().Be(candidateId);
		ok.Value.Candidates.Should().ContainSingle();
		MatchCandidateDto first = ok.Value.Candidates.First();
		first.CanonicalName.Should().Be("Whole Milk");
		first.CosineSimilarity.Should().Be(0.92);
	}

	[Fact]
	public async Task TestMatch_ZeroTopN_DefaultsToFive()
	{
		// The generated DTO has TopN default = 5, but JSON deserialization from a client
		// that omits TopN (or sends topN=0 explicitly) can set it to 0. The controller
		// coerces 0 → 5 as a defensive fallback.
		TestMatchRequest request = new()
		{
			Description = "milk",
			TopN = 0,
		};

		_mediatorMock
			.Setup(m => m.Send(It.Is<TestNormalizedDescriptionMatchQuery>(q => q.TopN == 5), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new MatchTestResult([], MatchTestOutcomes.CreateNew, null));

		Results<Ok<MatchTestResultResponse>, BadRequest<ProblemDetails>> result =
			await _controller.TestMatch(request, CancellationToken.None);

		Assert.IsType<Ok<MatchTestResultResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(It.Is<TestNormalizedDescriptionMatchQuery>(q => q.TopN == 5), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Theory]
	[InlineData("", NormalizedDescriptionsController.DescriptionRequired)]
	[InlineData("   ", NormalizedDescriptionsController.DescriptionRequired)]
	public async Task TestMatch_EmptyDescription_ReturnsBadRequest(string description, string expectedMessage)
	{
		TestMatchRequest request = new() { Description = description, TopN = 5 };

		Results<Ok<MatchTestResultResponse>, BadRequest<ProblemDetails>> result =
			await _controller.TestMatch(request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(expectedMessage);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(21)]
	public async Task TestMatch_TopNOutOfRange_ReturnsBadRequest(int topN)
	{
		TestMatchRequest request = new() { Description = "milk", TopN = topN };

		Results<Ok<MatchTestResultResponse>, BadRequest<ProblemDetails>> result =
			await _controller.TestMatch(request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.TopNOutOfRange);
	}

	[Fact]
	public async Task TestMatch_OverrideOutOfRange_ReturnsBadRequest()
	{
		TestMatchRequest request = new()
		{
			Description = "milk",
			TopN = 5,
			AutoAcceptThresholdOverride = 1.5,
		};

		Results<Ok<MatchTestResultResponse>, BadRequest<ProblemDetails>> result =
			await _controller.TestMatch(request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.OverrideOutOfRange);
	}

	[Fact]
	public async Task TestMatch_CrossedOverrides_ReturnsBadRequest()
	{
		TestMatchRequest request = new()
		{
			Description = "milk",
			TopN = 5,
			AutoAcceptThresholdOverride = 0.5,
			PendingReviewThresholdOverride = 0.8,
		};

		Results<Ok<MatchTestResultResponse>, BadRequest<ProblemDetails>> result =
			await _controller.TestMatch(request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.PendingMustBeLessThanAuto);
	}

	// ── POST settings/preview ────────────────────────────────────

	[Fact]
	public async Task PreviewThresholdImpact_ValidRequest_ReturnsOkWithMappedCounts()
	{
		PreviewThresholdImpactRequest request = new()
		{
			AutoAcceptThreshold = 0.75,
			PendingReviewThreshold = 0.4,
		};

		ThresholdImpactPreview preview = new(
			Current: new ClassificationCounts(10, 5, 3),
			Proposed: new ClassificationCounts(15, 2, 1),
			Deltas: new ReclassificationDeltas(AutoToPending: 0, PendingToAuto: 3, UnresolvedToAuto: 2, UnresolvedToPending: 0));

		_mediatorMock
			.Setup(m => m.Send(
				It.Is<PreviewThresholdImpactQuery>(q => q.AutoAcceptThreshold == 0.75 && q.PendingReviewThreshold == 0.4),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(preview);

		Results<Ok<ThresholdImpactPreviewResponse>, BadRequest<ProblemDetails>> result =
			await _controller.PreviewThresholdImpact(request, CancellationToken.None);

		Ok<ThresholdImpactPreviewResponse> ok = Assert.IsType<Ok<ThresholdImpactPreviewResponse>>(result.Result);
		ok.Value!.Current.AutoAccepted.Should().Be(10);
		ok.Value.Proposed.AutoAccepted.Should().Be(15);
		ok.Value.Deltas.PendingToAuto.Should().Be(3);
		ok.Value.Deltas.UnresolvedToAuto.Should().Be(2);
	}

	[Theory]
	[InlineData(-0.1, 0.5, NormalizedDescriptionsController.AutoAcceptOutOfRange)]
	[InlineData(1.1, 0.5, NormalizedDescriptionsController.AutoAcceptOutOfRange)]
	[InlineData(0.8, -0.1, NormalizedDescriptionsController.PendingReviewOutOfRange)]
	[InlineData(0.8, 1.1, NormalizedDescriptionsController.PendingReviewOutOfRange)]
	[InlineData(0.5, 0.6, NormalizedDescriptionsController.PendingMustBeLessThanAuto)]
	public async Task PreviewThresholdImpact_InvalidRequest_ReturnsBadRequest(double autoAccept, double pendingReview, string expectedMessage)
	{
		PreviewThresholdImpactRequest request = new()
		{
			AutoAcceptThreshold = autoAccept,
			PendingReviewThreshold = pendingReview,
		};

		Results<Ok<ThresholdImpactPreviewResponse>, BadRequest<ProblemDetails>> result =
			await _controller.PreviewThresholdImpact(request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(expectedMessage);

		_mediatorMock.Verify(m => m.Send(It.IsAny<PreviewThresholdImpactQuery>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	// ── GET list ─────────────────────────────────────────────────

	private static PagedResult<NormalizedDescriptionDetail> Page(
		List<NormalizedDescriptionDetail> data,
		int? total = null,
		int offset = 0,
		int limit = 50) =>
		new(data, total ?? data.Count, offset, limit);

	[Fact]
	public async Task GetAllNormalizedDescriptions_NoFilter_ReturnsOkWithAllItems()
	{
		List<NormalizedDescriptionDetail> items =
		[
			new(new NormalizedDescription(Guid.NewGuid(), "coffee beans", DomainStatus.Active, new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)), LinkedItemCount: 7, NearestNeighbourName: null, ["COFFEE BEANS 1LB"]),
			new(new NormalizedDescription(Guid.NewGuid(), "whole milk", DomainStatus.PendingReview, new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero)), LinkedItemCount: 2, NearestNeighbourName: null, []),
		];

		_mediatorMock
			.Setup(m => m.Send(It.Is<GetAllNormalizedDescriptionsQuery>(q => q.StatusFilter == null), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Page(items));

		Results<Ok<NormalizedDescriptionListResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetAllNormalizedDescriptions(status: null, q: null, cancellationToken: CancellationToken.None);

		Ok<NormalizedDescriptionListResponse> ok = Assert.IsType<Ok<NormalizedDescriptionListResponse>>(result.Result);
		ok.Value!.Items.Should().HaveCount(2);
		ok.Value.TotalCount.Should().Be(2);
		ok.Value.Items.First().CanonicalName.Should().Be("coffee beans");
	}

	/** Matches a query whose status filter is exactly the given set, in any order. */
	private static bool FilterIs(GetAllNormalizedDescriptionsQuery query, params DomainStatus[] expected) =>
		query.StatusFilter is not null &&
		query.StatusFilter.Count == expected.Length &&
		expected.All(query.StatusFilter.Contains);

	[Theory]
	[InlineData("Active", DomainStatus.Active)]
	[InlineData("PendingReview", DomainStatus.PendingReview)]
	[InlineData("Rejected", DomainStatus.Rejected)]
	[InlineData("active", DomainStatus.Active)]
	[InlineData("pendingreview", DomainStatus.PendingReview)]
	public async Task GetAllNormalizedDescriptions_WithStatusFilter_ForwardsToQuery(string status, DomainStatus expected)
	{
		_mediatorMock
			.Setup(m => m.Send(It.Is<GetAllNormalizedDescriptionsQuery>(q => FilterIs(q, expected)), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Page([]));

		Results<Ok<NormalizedDescriptionListResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetAllNormalizedDescriptions([status], q: null, cancellationToken: CancellationToken.None);

		Ok<NormalizedDescriptionListResponse> ok = Assert.IsType<Ok<NormalizedDescriptionListResponse>>(result.Result);
		ok.Value!.Items.Should().BeEmpty();
		ok.Value.TotalCount.Should().Be(0);
		_mediatorMock.Verify(m => m.Send(It.Is<GetAllNormalizedDescriptionsQuery>(q => FilterIs(q, expected)), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetAllNormalizedDescriptions_InvalidStatusFilter_ReturnsBadRequest()
	{
		Results<Ok<NormalizedDescriptionListResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetAllNormalizedDescriptions(["archived"], q: null, cancellationToken: CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.InvalidStatusFilter);
		_mediatorMock.Verify(m => m.Send(It.IsAny<GetAllNormalizedDescriptionsQuery>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	// ── RECEIPTS-878: the status filter takes several values ──────

	[Fact]
	public async Task GetAllNormalizedDescriptions_RepeatedStatus_MatchesAnyOfThem()
	{
		_mediatorMock
			.Setup(m => m.Send(It.IsAny<GetAllNormalizedDescriptionsQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Page([]));

		// The merge dialog's query. Before this it had to pick one status, so two near-duplicate
		// pending entries from the same resolver batch could not be merged with each other.
		await _controller.GetAllNormalizedDescriptions(
			["Active", "PendingReview"],
			q: null,
			cancellationToken: CancellationToken.None);

		_mediatorMock.Verify(
			m => m.Send(
				It.Is<GetAllNormalizedDescriptionsQuery>(q => FilterIs(q, DomainStatus.Active, DomainStatus.PendingReview)),
				It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task GetAllNormalizedDescriptions_CommaSeparatedStatus_IsSplit()
	{
		_mediatorMock
			.Setup(m => m.Send(It.IsAny<GetAllNormalizedDescriptionsQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Page([]));

		// ASP.NET Core binds ?status=Active,PendingReview as one element, and that is what a
		// hand-written URL tends to look like.
		await _controller.GetAllNormalizedDescriptions(
			["Active,PendingReview"],
			q: null,
			cancellationToken: CancellationToken.None);

		_mediatorMock.Verify(
			m => m.Send(
				It.Is<GetAllNormalizedDescriptionsQuery>(q => FilterIs(q, DomainStatus.Active, DomainStatus.PendingReview)),
				It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task GetAllNormalizedDescriptions_DuplicateStatuses_AreCollapsed()
	{
		_mediatorMock
			.Setup(m => m.Send(It.IsAny<GetAllNormalizedDescriptionsQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Page([]));

		await _controller.GetAllNormalizedDescriptions(
			["Active", "active", "ACTIVE"],
			q: null,
			cancellationToken: CancellationToken.None);

		_mediatorMock.Verify(
			m => m.Send(It.Is<GetAllNormalizedDescriptionsQuery>(q => FilterIs(q, DomainStatus.Active)), It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Theory]
	[InlineData(null)]        // ?status omitted entirely
	[InlineData("")]          // ?status=
	[InlineData("   ")]       // ?status=%20%20%20
	[InlineData(",")]         // ?status=, — separators with nothing between them
	public async Task GetAllNormalizedDescriptions_BlankStatus_IsNoFilterNotAnEmptySet(string? token)
	{
		_mediatorMock
			.Setup(m => m.Send(It.IsAny<GetAllNormalizedDescriptionsQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Page([]));

		string[]? status = token is null ? null : [token];
		await _controller.GetAllNormalizedDescriptions(status, q: null, cancellationToken: CancellationToken.None);

		// An empty set would silently return zero rows for what reads like an unfiltered request.
		_mediatorMock.Verify(
			m => m.Send(It.Is<GetAllNormalizedDescriptionsQuery>(q => q.StatusFilter == null), It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task GetAllNormalizedDescriptions_OneBadStatusAmongGoodOnes_ReturnsBadRequest()
	{
		Results<Ok<NormalizedDescriptionListResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetAllNormalizedDescriptions(
				["Active", "archived"],
				q: null,
				cancellationToken: CancellationToken.None);

		// Silently dropping the unparseable one would answer a narrower question than was asked
		// and look like a successful filter.
		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.InvalidStatusFilter);
		_mediatorMock.Verify(m => m.Send(It.IsAny<GetAllNormalizedDescriptionsQuery>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	// ── RECEIPTS-880: last seen ───────────────────────────────────

	[Fact]
	public async Task GetAllNormalizedDescriptions_CarriesLastSeenThrough()
	{
		DateTimeOffset lastSeen = new(2026, 5, 2, 0, 0, 0, TimeSpan.Zero);
		List<NormalizedDescriptionDetail> items =
		[
			new(new NormalizedDescription(Guid.NewGuid(), "coffee beans", DomainStatus.Active, new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)), LinkedItemCount: 7, NearestNeighbourName: null, ["COFFEE BEANS 1LB"], lastSeen),
			new(new NormalizedDescription(Guid.NewGuid(), "orphan", DomainStatus.Active, new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero)), LinkedItemCount: 0, NearestNeighbourName: null, [], null),
		];

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<GetAllNormalizedDescriptionsQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Page(items));

		Results<Ok<NormalizedDescriptionListResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetAllNormalizedDescriptions(status: null, q: null, cancellationToken: CancellationToken.None);

		// Null stays null rather than collapsing to a default date — "never matched anything" and
		// "last matched a long time ago" call for opposite decisions about the entry.
		Ok<NormalizedDescriptionListResponse> ok = Assert.IsType<Ok<NormalizedDescriptionListResponse>>(result.Result);
		ok.Value!.Items.First().LastSeen.Should().Be(lastSeen);
		ok.Value.Items.Last().LastSeen.Should().BeNull();
	}

	// ── RECEIPTS-879: paging ──────────────────────────────────────

	[Fact]
	public async Task GetAllNormalizedDescriptions_DefaultsToTheFirstPage()
	{
		_mediatorMock
			.Setup(m => m.Send(It.IsAny<GetAllNormalizedDescriptionsQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Page([]));

		await _controller.GetAllNormalizedDescriptions(status: null, q: null, cancellationToken: CancellationToken.None);

		// A caller that supplies no window gets a bounded one. The endpoint used to return every
		// row, which is what RECEIPTS-879 exists to stop.
		_mediatorMock.Verify(
			m => m.Send(It.Is<GetAllNormalizedDescriptionsQuery>(q => q.Offset == 0 && q.Limit == 50), It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task GetAllNormalizedDescriptions_ForwardsSearchTermAndWindow()
	{
		_mediatorMock
			.Setup(m => m.Send(It.IsAny<GetAllNormalizedDescriptionsQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Page([], total: 412, offset: 100, limit: 25));

		Results<Ok<NormalizedDescriptionListResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetAllNormalizedDescriptions(status: null, q: "milk", offset: 100, limit: 25, CancellationToken.None);

		_mediatorMock.Verify(
			m => m.Send(It.Is<GetAllNormalizedDescriptionsQuery>(q => q.Q == "milk" && q.Offset == 100 && q.Limit == 25), It.IsAny<CancellationToken>()),
			Times.Once);

		// The response echoes the window back so a client rendering a pager does not have to
		// remember what it asked for, and reports the true match count rather than the page length.
		Ok<NormalizedDescriptionListResponse> ok = Assert.IsType<Ok<NormalizedDescriptionListResponse>>(result.Result);
		ok.Value!.TotalCount.Should().Be(412);
		ok.Value.Offset.Should().Be(100);
		ok.Value.Limit.Should().Be(25);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(int.MinValue)]
	public async Task GetAllNormalizedDescriptions_NegativeOffset_ReturnsBadRequest(int offset)
	{
		Results<Ok<NormalizedDescriptionListResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetAllNormalizedDescriptions(status: null, q: null, offset, limit: 50, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.OffsetOutOfRange);
		_mediatorMock.Verify(m => m.Send(It.IsAny<GetAllNormalizedDescriptionsQuery>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-5)]
	[InlineData(NormalizedDescriptionsController.MaxPageSize + 1)]
	public async Task GetAllNormalizedDescriptions_LimitOutsideBounds_ReturnsBadRequest(int limit)
	{
		Results<Ok<NormalizedDescriptionListResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetAllNormalizedDescriptions(status: null, q: null, offset: 0, limit, CancellationToken.None);

		// The upper bound is the point of the issue: without it a client can ask for everything
		// again and undo the pagination by parameter.
		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.LimitOutOfRange);
		_mediatorMock.Verify(m => m.Send(It.IsAny<GetAllNormalizedDescriptionsQuery>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task GetAllNormalizedDescriptions_LimitAtTheCeiling_IsAccepted()
	{
		_mediatorMock
			.Setup(m => m.Send(It.IsAny<GetAllNormalizedDescriptionsQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Page([], limit: NormalizedDescriptionsController.MaxPageSize));

		Results<Ok<NormalizedDescriptionListResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetAllNormalizedDescriptions(status: null, q: null, offset: 0, NormalizedDescriptionsController.MaxPageSize, CancellationToken.None);

		Assert.IsType<Ok<NormalizedDescriptionListResponse>>(result.Result);
	}

	// ── GET by id ────────────────────────────────────────────────

	[Fact]
	public async Task GetNormalizedDescriptionById_Found_ReturnsOkWithMappedResponse()
	{
		Guid id = Guid.NewGuid();
		NormalizedDescriptionDetail item = new(
			new NormalizedDescription(
				id,
				"cherry cola",
				DomainStatus.PendingReview,
				new DateTimeOffset(2026, 4, 19, 0, 0, 0, TimeSpan.Zero),
				Guid.NewGuid(),
				0.86),
			LinkedItemCount: 3,
			NearestNeighbourName: "cola",
			["CHERRY COLA 12PK", "cherry cola 2L"]);

		_mediatorMock
			.Setup(m => m.Send(It.Is<GetNormalizedDescriptionByIdQuery>(q => q.Id == id), It.IsAny<CancellationToken>()))
			.ReturnsAsync(item);

		Results<Ok<NormalizedDescriptionResponse>, NotFound, BadRequest<ProblemDetails>> result =
			await _controller.GetNormalizedDescriptionById(id, CancellationToken.None);

		Ok<NormalizedDescriptionResponse> ok = Assert.IsType<Ok<NormalizedDescriptionResponse>>(result.Result);
		ok.Value!.Id.Should().Be(id);
		ok.Value.CanonicalName.Should().Be("cherry cola");
		ok.Value.Status.Should().Be(DtoStatus.PendingReview);
		ok.Value.CreatedAt.Should().Be(item.Description.CreatedAt);
		// RECEIPTS-873 evidence must survive the mapping — this is the whole point of the row.
		ok.Value.LinkedItemCount.Should().Be(3);
		ok.Value.SampleRawDescriptions.Should().BeEquivalentTo(["CHERRY COLA 12PK", "cherry cola 2L"]);
		ok.Value.NearestNeighbourName.Should().Be("cola");
		ok.Value.NearestNeighbourSimilarity.Should().Be(0.86);
	}

	[Fact]
	public async Task GetNormalizedDescriptionById_NotFound_ReturnsNotFound()
	{
		Guid id = Guid.NewGuid();
		_mediatorMock
			.Setup(m => m.Send(It.Is<GetNormalizedDescriptionByIdQuery>(q => q.Id == id), It.IsAny<CancellationToken>()))
			.ReturnsAsync((NormalizedDescriptionDetail?)null);

		Results<Ok<NormalizedDescriptionResponse>, NotFound, BadRequest<ProblemDetails>> result =
			await _controller.GetNormalizedDescriptionById(id, CancellationToken.None);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task GetNormalizedDescriptionById_EmptyGuid_ReturnsBadRequest()
	{
		Results<Ok<NormalizedDescriptionResponse>, NotFound, BadRequest<ProblemDetails>> result =
			await _controller.GetNormalizedDescriptionById(Guid.Empty, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.IdCannotBeEmpty);
		_mediatorMock.Verify(m => m.Send(It.IsAny<GetNormalizedDescriptionByIdQuery>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	// ── POST merge ───────────────────────────────────────────────

	[Fact]
	public async Task MergeNormalizedDescriptions_ValidRequest_ReturnsOkWithRelinkCount()
	{
		Guid keepId = Guid.NewGuid();
		Guid discardId = Guid.NewGuid();
		MergeNormalizedDescriptionRequest request = new() { DiscardId = discardId };

		_mediatorMock
			.Setup(m => m.Send(
				It.Is<MergeNormalizedDescriptionsCommand>(c => c.KeepId == keepId && c.DiscardId == discardId),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(12);

		Results<Ok<MergeNormalizedDescriptionsResponse>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>> result =
			await _controller.MergeNormalizedDescriptions(keepId, request, CancellationToken.None);

		Ok<MergeNormalizedDescriptionsResponse> ok = Assert.IsType<Ok<MergeNormalizedDescriptionsResponse>>(result.Result);
		ok.Value!.ItemsRelinkedCount.Should().Be(12);
	}

	[Fact]
	public async Task MergeNormalizedDescriptions_WhenARowIsMissing_ReturnsNotFoundRatherThanAZeroCount()
	{
		// RECEIPTS-891. A stale id used to come back as 200 { itemsRelinkedCount: 0 } —
		// the same body a merge that genuinely had nothing to re-link returns. The admin
		// was told it worked and the row they meant to consolidate was still there.
		Guid keepId = Guid.NewGuid();
		Guid discardId = Guid.NewGuid();
		MergeNormalizedDescriptionRequest request = new() { DiscardId = discardId };

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<MergeNormalizedDescriptionsCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException($"Normalized description {discardId} not found."));

		Results<Ok<MergeNormalizedDescriptionsResponse>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>> result =
			await _controller.MergeNormalizedDescriptions(keepId, request, CancellationToken.None);

		NotFound<ProblemDetails> notFound = Assert.IsType<NotFound<ProblemDetails>>(result.Result);
		notFound.Value!.Status.Should().Be(404);
		// The id is carried through so the admin can tell which of the two was stale.
		notFound.Value.Detail.Should().Contain(discardId.ToString());
	}

	[Fact]
	public async Task MergeNormalizedDescriptions_WhenNothingNeededRelinking_StillReturnsOk()
	{
		// The counterpart to the test above: zero now means one thing only, and it is a
		// success. If this ever became a 404 the honest-zero case would be unreachable.
		Guid keepId = Guid.NewGuid();
		MergeNormalizedDescriptionRequest request = new() { DiscardId = Guid.NewGuid() };

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<MergeNormalizedDescriptionsCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		Results<Ok<MergeNormalizedDescriptionsResponse>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>> result =
			await _controller.MergeNormalizedDescriptions(keepId, request, CancellationToken.None);

		Ok<MergeNormalizedDescriptionsResponse> ok = Assert.IsType<Ok<MergeNormalizedDescriptionsResponse>>(result.Result);
		ok.Value!.ItemsRelinkedCount.Should().Be(0);
	}

	[Fact]
	public async Task MergeNormalizedDescriptions_EmptyKeepId_ReturnsBadRequest()
	{
		MergeNormalizedDescriptionRequest request = new() { DiscardId = Guid.NewGuid() };

		Results<Ok<MergeNormalizedDescriptionsResponse>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>> result =
			await _controller.MergeNormalizedDescriptions(Guid.Empty, request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.IdCannotBeEmpty);
		_mediatorMock.Verify(m => m.Send(It.IsAny<MergeNormalizedDescriptionsCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task MergeNormalizedDescriptions_EmptyDiscardId_ReturnsBadRequest()
	{
		MergeNormalizedDescriptionRequest request = new() { DiscardId = Guid.Empty };

		Results<Ok<MergeNormalizedDescriptionsResponse>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>> result =
			await _controller.MergeNormalizedDescriptions(Guid.NewGuid(), request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.DiscardIdCannotBeEmpty);
		_mediatorMock.Verify(m => m.Send(It.IsAny<MergeNormalizedDescriptionsCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task MergeNormalizedDescriptions_SameKeepAndDiscard_ReturnsBadRequest()
	{
		Guid id = Guid.NewGuid();
		MergeNormalizedDescriptionRequest request = new() { DiscardId = id };

		Results<Ok<MergeNormalizedDescriptionsResponse>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>> result =
			await _controller.MergeNormalizedDescriptions(id, request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.MergeIdsMustDiffer);
		_mediatorMock.Verify(m => m.Send(It.IsAny<MergeNormalizedDescriptionsCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	// ── POST split ───────────────────────────────────────────────

	[Fact]
	public async Task SplitNormalizedDescription_ValidRequest_ReturnsOkWithNewResource()
	{
		Guid currentId = Guid.NewGuid();
		Guid receiptItemId = Guid.NewGuid();
		Guid newId = Guid.NewGuid();
		Guid secondItemId = Guid.NewGuid();
		SplitNormalizedDescriptionRequest request = new()
		{
			ReceiptItemIds = [receiptItemId, secondItemId],
			CanonicalName = "reese cup",
		};

		NormalizedDescriptionDetail created = new(
			new NormalizedDescription(
				newId,
				"reese cup",
				DomainStatus.Active,
				new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero)),
			LinkedItemCount: 2,
			NearestNeighbourName: null,
			["REESE CUP KING"]);

		_mediatorMock
			.Setup(m => m.Send(
				It.Is<SplitNormalizedDescriptionCommand>(c =>
					c.ReceiptItemIds.SequenceEqual(new[] { receiptItemId, secondItemId })
					&& c.CanonicalName == "reese cup"),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(created);

		Results<Ok<NormalizedDescriptionResponse>, NotFound, BadRequest<ProblemDetails>> result =
			await _controller.SplitNormalizedDescription(currentId, request, CancellationToken.None);

		Ok<NormalizedDescriptionResponse> ok = Assert.IsType<Ok<NormalizedDescriptionResponse>>(result.Result);
		ok.Value!.Id.Should().Be(newId);
		ok.Value.CanonicalName.Should().Be("reese cup");
		ok.Value.Status.Should().Be(DtoStatus.Active);
		// Both selected items landed on the new row — a multi-item split that silently moved one
		// would be worse than one that failed (RECEIPTS-877).
		ok.Value.LinkedItemCount.Should().Be(2);
	}

	[Fact]
	public async Task SplitNormalizedDescription_ReceiptItemMissing_ReturnsNotFound()
	{
		Guid currentId = Guid.NewGuid();
		Guid missingReceiptItemId = Guid.NewGuid();
		SplitNormalizedDescriptionRequest request = new()
		{
			// One good id and one stale one: the split is all-or-nothing, so the whole request
			// must 404 rather than quietly moving the half it could find.
			ReceiptItemIds = [Guid.NewGuid(), missingReceiptItemId],
			CanonicalName = "reese cup",
		};

		_mediatorMock
			.Setup(m => m.Send(
				It.Is<SplitNormalizedDescriptionCommand>(c => c.ReceiptItemIds.Contains(missingReceiptItemId)),
				It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException("Receipt item not found."));

		Results<Ok<NormalizedDescriptionResponse>, NotFound, BadRequest<ProblemDetails>> result =
			await _controller.SplitNormalizedDescription(currentId, request, CancellationToken.None);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task SplitNormalizedDescription_EmptyId_ReturnsBadRequest()
	{
		SplitNormalizedDescriptionRequest request = new()
		{
			ReceiptItemIds = [Guid.NewGuid()],
			CanonicalName = "reese cup",
		};

		Results<Ok<NormalizedDescriptionResponse>, NotFound, BadRequest<ProblemDetails>> result =
			await _controller.SplitNormalizedDescription(Guid.Empty, request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.IdCannotBeEmpty);
		_mediatorMock.Verify(m => m.Send(It.IsAny<SplitNormalizedDescriptionCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task SplitNormalizedDescription_EmptyReceiptItemId_ReturnsBadRequest()
	{
		SplitNormalizedDescriptionRequest request = new()
		{
			ReceiptItemIds = [Guid.NewGuid(), Guid.Empty],
			CanonicalName = "reese cup",
		};

		Results<Ok<NormalizedDescriptionResponse>, NotFound, BadRequest<ProblemDetails>> result =
			await _controller.SplitNormalizedDescription(Guid.NewGuid(), request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.ReceiptItemIdCannotBeEmpty);
		_mediatorMock.Verify(m => m.Send(It.IsAny<SplitNormalizedDescriptionCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task SplitNormalizedDescription_EmptySelection_ReturnsBadRequest()
	{
		SplitNormalizedDescriptionRequest request = new()
		{
			ReceiptItemIds = [],
			CanonicalName = "reese cup",
		};

		Results<Ok<NormalizedDescriptionResponse>, NotFound, BadRequest<ProblemDetails>> result =
			await _controller.SplitNormalizedDescription(Guid.NewGuid(), request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.SplitRequiresAtLeastOneItem);
		_mediatorMock.Verify(m => m.Send(It.IsAny<SplitNormalizedDescriptionCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task SplitNormalizedDescription_BlankName_ReturnsBadRequest()
	{
		SplitNormalizedDescriptionRequest request = new()
		{
			ReceiptItemIds = [Guid.NewGuid()],
			CanonicalName = "   ",
		};

		Results<Ok<NormalizedDescriptionResponse>, NotFound, BadRequest<ProblemDetails>> result =
			await _controller.SplitNormalizedDescription(Guid.NewGuid(), request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.SplitNameRequired);
		_mediatorMock.Verify(m => m.Send(It.IsAny<SplitNormalizedDescriptionCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	// ── PATCH rename (RECEIPTS-876) ──────────────────────────────

	// ── POST {id}/link-template (RECEIPTS-930) ──────────────────

	private static NormalizedDescriptionDetail TemplateBackedDetail(Guid id, string canonicalName, string templateName) => new(
		new NormalizedDescription(
			id,
			canonicalName,
			DomainStatus.Active,
			new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero),
			nearestNeighbourId: null,
			nearestNeighbourSimilarity: null,
			displayLabel: null),
		LinkedItemCount: 5,
		NearestNeighbourName: null,
		[canonicalName],
		LastSeen: null,
		LinkedTemplateId: Guid.NewGuid(),
		LinkedTemplateName: templateName,
		LinkedTemplateCount: 1);

	[Fact]
	public async Task LinkItemTemplate_ReturnsTheSurvivingRowAndWhatMoved()
	{
		Guid rowId = Guid.NewGuid();
		Guid survivorId = Guid.NewGuid();
		Guid templateId = Guid.NewGuid();
		LinkItemTemplateRequest request = new() { ItemTemplateId = templateId };

		_mediatorMock
			.Setup(m => m.Send(
				It.Is<LinkItemTemplateCommand>(c => c.DescriptionId == rowId && c.ItemTemplateId == templateId),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new LinkTemplateResult(
				TemplateBackedDetail(survivorId, "Gallon of Milk", "Gallon of Milk"),
				ItemsRelinkedCount: 5,
				Merged: true));

		Results<Ok<LinkItemTemplateResponse>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>> result =
			await _controller.LinkItemTemplateToNormalizedDescription(rowId, request, CancellationToken.None);

		Ok<LinkItemTemplateResponse> ok = Assert.IsType<Ok<LinkItemTemplateResponse>>(result.Result);
		// The survivor is the template's entry, not the row the caller asked about — a client that
		// assumed otherwise would show the wrong name and leave a deleted row on screen.
		ok.Value!.Description.Id.Should().Be(survivorId);
		ok.Value.ItemsRelinkedCount.Should().Be(5);
		ok.Value.Merged.Should().BeTrue();
		ok.Value.Description.LinkedTemplateName.Should().Be("Gallon of Milk");
		ok.Value.Description.LinkedTemplateCount.Should().Be(1);
	}

	[Fact]
	public async Task LinkItemTemplate_ReportsTheNonDestructiveCaseSeparately()
	{
		// merged: false means nothing moved and nothing was deleted. Collapsing the two cases
		// would make the client either over-warn or under-warn on every link.
		Guid rowId = Guid.NewGuid();
		LinkItemTemplateRequest request = new() { ItemTemplateId = Guid.NewGuid() };

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<LinkItemTemplateCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new LinkTemplateResult(
				TemplateBackedDetail(rowId, "Gallon of Milk", "Gallon of Milk"),
				ItemsRelinkedCount: 0,
				Merged: false));

		Results<Ok<LinkItemTemplateResponse>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>> result =
			await _controller.LinkItemTemplateToNormalizedDescription(rowId, request, CancellationToken.None);

		Ok<LinkItemTemplateResponse> ok = Assert.IsType<Ok<LinkItemTemplateResponse>>(result.Result);
		ok.Value!.Merged.Should().BeFalse();
		ok.Value.ItemsRelinkedCount.Should().Be(0);
		ok.Value.Description.Id.Should().Be(rowId);
	}

	[Fact]
	public async Task LinkItemTemplate_EmptyId_ReturnsBadRequest()
	{
		LinkItemTemplateRequest request = new() { ItemTemplateId = Guid.NewGuid() };

		Results<Ok<LinkItemTemplateResponse>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>> result =
			await _controller.LinkItemTemplateToNormalizedDescription(Guid.Empty, request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.IdCannotBeEmpty);
	}

	[Fact]
	public async Task LinkItemTemplate_EmptyTemplateId_ReturnsBadRequest()
	{
		LinkItemTemplateRequest request = new() { ItemTemplateId = Guid.Empty };

		Results<Ok<LinkItemTemplateResponse>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>> result =
			await _controller.LinkItemTemplateToNormalizedDescription(Guid.NewGuid(), request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.ItemTemplateIdCannotBeEmpty);
	}

	[Fact]
	public async Task LinkItemTemplate_MissingRowOrTemplate_ReturnsNotFoundNamingWhich()
	{
		LinkItemTemplateRequest request = new() { ItemTemplateId = Guid.NewGuid() };

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<LinkItemTemplateCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException("Item template not found."));

		Results<Ok<LinkItemTemplateResponse>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>> result =
			await _controller.LinkItemTemplateToNormalizedDescription(Guid.NewGuid(), request, CancellationToken.None);

		NotFound<ProblemDetails> notFound = Assert.IsType<NotFound<ProblemDetails>>(result.Result);
		notFound.Value!.Status.Should().Be(404);
		// Both the review queue and the template picker can be minutes old, so "not found" alone
		// leaves the admin guessing which half of their request went stale.
		notFound.Value.Detail.Should().Be("Item template not found.");
	}

	[Fact]
	public async Task LinkItemTemplate_RejectedRow_ReturnsBadRequestRatherThanConflict()
	{
		// Not a lost race: retrying unchanged will never succeed, because the endpoint refuses to
		// undo a rejection on the caller's behalf.
		LinkItemTemplateRequest request = new() { ItemTemplateId = Guid.NewGuid() };

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<LinkItemTemplateCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("This entry was rejected."));

		Results<Ok<LinkItemTemplateResponse>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>> result =
			await _controller.LinkItemTemplateToNormalizedDescription(Guid.NewGuid(), request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be("This entry was rejected.");
	}

	private static NormalizedDescriptionDetail RenamedDetail(Guid id, string canonicalName, string? label) => new(
		new NormalizedDescription(
			id,
			canonicalName,
			DomainStatus.Active,
			new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero),
			nearestNeighbourId: null,
			nearestNeighbourSimilarity: null,
			displayLabel: label),
		LinkedItemCount: 3,
		NearestNeighbourName: null,
		[canonicalName]);

	[Fact]
	public async Task RenameNormalizedDescription_SetsLabel_ReturnsOkWithBothNames()
	{
		Guid id = Guid.NewGuid();
		RenameNormalizedDescriptionRequest request = new() { DisplayLabel = "Milk" };

		_mediatorMock
			.Setup(m => m.Send(
				It.Is<RenameNormalizedDescriptionCommand>(c => c.Id == id && c.DisplayLabel == "Milk"),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(RenamedDetail(id, "MILK 2% GAL", "Milk"));

		Results<Ok<NormalizedDescriptionResponse>, NotFound<ProblemDetails>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>> result =
			await _controller.RenameNormalizedDescription(id, request, CancellationToken.None);

		Ok<NormalizedDescriptionResponse> ok = Assert.IsType<Ok<NormalizedDescriptionResponse>>(result.Result);
		ok.Value!.DisplayLabel.Should().Be("Milk");
		ok.Value.DisplayName.Should().Be("Milk");
		// The matched text is still reported. A reviewer who renamed "MILK 2% GAL" to "Milk"
		// needs to see which receipt text the entry actually covers.
		ok.Value.CanonicalName.Should().Be("MILK 2% GAL");
	}

	[Fact]
	public async Task RenameNormalizedDescription_NullLabel_ClearsBackToMatchedText()
	{
		Guid id = Guid.NewGuid();
		RenameNormalizedDescriptionRequest request = new() { DisplayLabel = null };

		_mediatorMock
			.Setup(m => m.Send(
				It.Is<RenameNormalizedDescriptionCommand>(c => c.Id == id && c.DisplayLabel == null),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(RenamedDetail(id, "MILK 2% GAL", null));

		Results<Ok<NormalizedDescriptionResponse>, NotFound<ProblemDetails>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>> result =
			await _controller.RenameNormalizedDescription(id, request, CancellationToken.None);

		Ok<NormalizedDescriptionResponse> ok = Assert.IsType<Ok<NormalizedDescriptionResponse>>(result.Result);
		ok.Value!.DisplayLabel.Should().BeNull();
		ok.Value.DisplayName.Should().Be("MILK 2% GAL");
	}

	[Fact]
	public async Task RenameNormalizedDescription_NameTaken_ReturnsConflict()
	{
		Guid id = Guid.NewGuid();
		RenameNormalizedDescriptionRequest request = new() { DisplayLabel = "Milk" };

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<RenameNormalizedDescriptionCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("Another normalized description already displays that name."));

		Results<Ok<NormalizedDescriptionResponse>, NotFound<ProblemDetails>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>> result =
			await _controller.RenameNormalizedDescription(id, request, CancellationToken.None);

		// 409 rather than 400: the request is well-formed, it just lost to the current registry.
		Conflict<ProblemDetails> conflict = Assert.IsType<Conflict<ProblemDetails>>(result.Result);
		conflict.Value!.Detail.Should().Contain("already displays that name");
	}

	[Fact]
	public async Task RenameNormalizedDescription_WhitespaceLabel_ReturnsBadRequest()
	{
		Guid id = Guid.NewGuid();
		RenameNormalizedDescriptionRequest request = new() { DisplayLabel = "   " };

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<RenameNormalizedDescriptionCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new ArgumentException(NormalizedDescription.DisplayLabelCannotBeWhitespace));

		Results<Ok<NormalizedDescriptionResponse>, NotFound<ProblemDetails>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>> result =
			await _controller.RenameNormalizedDescription(id, request, CancellationToken.None);

		Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
	}

	[Fact]
	public async Task RenameNormalizedDescription_UnknownId_ReturnsNotFound()
	{
		Guid id = Guid.NewGuid();
		RenameNormalizedDescriptionRequest request = new() { DisplayLabel = "Milk" };

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<RenameNormalizedDescriptionCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException("Normalized description not found."));

		Results<Ok<NormalizedDescriptionResponse>, NotFound<ProblemDetails>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>> result =
			await _controller.RenameNormalizedDescription(id, request, CancellationToken.None);

		Assert.IsType<NotFound<ProblemDetails>>(result.Result);
	}

	[Fact]
	public async Task RenameNormalizedDescription_EmptyId_ReturnsBadRequest()
	{
		RenameNormalizedDescriptionRequest request = new() { DisplayLabel = "Milk" };

		Results<Ok<NormalizedDescriptionResponse>, NotFound<ProblemDetails>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>> result =
			await _controller.RenameNormalizedDescription(Guid.Empty, request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.IdCannotBeEmpty);
		_mediatorMock.Verify(m => m.Send(It.IsAny<RenameNormalizedDescriptionCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task UpdateNormalizedDescriptionStatus_Rejected_IsAcceptedAndForwarded()
	{
		Guid id = Guid.NewGuid();
		UpdateNormalizedDescriptionStatusRequest request = new() { Status = DtoStatus.Rejected };

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<GetNormalizedDescriptionByIdQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(RenamedDetail(id, "MISC 4.99", null));
		_mediatorMock
			.Setup(m => m.Send(It.IsAny<UpdateNormalizedDescriptionStatusCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result =
			await _controller.UpdateNormalizedDescriptionStatus(id, request, CancellationToken.None);

		Assert.IsType<NoContent>(result.Result);
		_mediatorMock.Verify(
			m => m.Send(
				It.Is<UpdateNormalizedDescriptionStatusCommand>(c => c.Status == DomainStatus.Rejected),
				It.IsAny<CancellationToken>()),
			Times.Once);
	}

	// ── PATCH status ─────────────────────────────────────────────

	[Fact]
	public async Task UpdateNormalizedDescriptionStatus_ExistingRow_ReturnsNoContent()
	{
		Guid id = Guid.NewGuid();
		UpdateNormalizedDescriptionStatusRequest request = new() { Status = DtoStatus.PendingReview };

		NormalizedDescriptionDetail existing = new(new NormalizedDescription(id, "whole milk", DomainStatus.Active, DateTimeOffset.UtcNow), LinkedItemCount: 0, NearestNeighbourName: null, []);
		_mediatorMock
			.Setup(m => m.Send(It.Is<GetNormalizedDescriptionByIdQuery>(q => q.Id == id), It.IsAny<CancellationToken>()))
			.ReturnsAsync(existing);
		_mediatorMock
			.Setup(m => m.Send(
				It.Is<UpdateNormalizedDescriptionStatusCommand>(c => c.Id == id && c.Status == DomainStatus.PendingReview),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result =
			await _controller.UpdateNormalizedDescriptionStatus(id, request, CancellationToken.None);

		Assert.IsType<NoContent>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<UpdateNormalizedDescriptionStatusCommand>(c => c.Id == id && c.Status == DomainStatus.PendingReview),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task UpdateNormalizedDescriptionStatus_ActiveStatus_MapsToDomainCorrectly()
	{
		Guid id = Guid.NewGuid();
		UpdateNormalizedDescriptionStatusRequest request = new() { Status = DtoStatus.Active };

		NormalizedDescriptionDetail existing = new(new NormalizedDescription(id, "whole milk", DomainStatus.PendingReview, DateTimeOffset.UtcNow), LinkedItemCount: 0, NearestNeighbourName: null, []);
		_mediatorMock
			.Setup(m => m.Send(It.Is<GetNormalizedDescriptionByIdQuery>(q => q.Id == id), It.IsAny<CancellationToken>()))
			.ReturnsAsync(existing);
		_mediatorMock
			.Setup(m => m.Send(
				It.Is<UpdateNormalizedDescriptionStatusCommand>(c => c.Id == id && c.Status == DomainStatus.Active),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result =
			await _controller.UpdateNormalizedDescriptionStatus(id, request, CancellationToken.None);

		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateNormalizedDescriptionStatus_MissingRow_ReturnsNotFound()
	{
		Guid id = Guid.NewGuid();
		UpdateNormalizedDescriptionStatusRequest request = new() { Status = DtoStatus.Active };

		_mediatorMock
			.Setup(m => m.Send(It.Is<GetNormalizedDescriptionByIdQuery>(q => q.Id == id), It.IsAny<CancellationToken>()))
			.ReturnsAsync((NormalizedDescriptionDetail?)null);

		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result =
			await _controller.UpdateNormalizedDescriptionStatus(id, request, CancellationToken.None);

		Assert.IsType<NotFound>(result.Result);
		// Should never reach the update command when the existence check fails.
		_mediatorMock.Verify(m => m.Send(It.IsAny<UpdateNormalizedDescriptionStatusCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task UpdateNormalizedDescriptionStatus_EmptyId_ReturnsBadRequest()
	{
		UpdateNormalizedDescriptionStatusRequest request = new() { Status = DtoStatus.Active };

		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result =
			await _controller.UpdateNormalizedDescriptionStatus(Guid.Empty, request, CancellationToken.None);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.IdCannotBeEmpty);
		_mediatorMock.Verify(m => m.Send(It.IsAny<GetNormalizedDescriptionByIdQuery>(), It.IsAny<CancellationToken>()), Times.Never);
		_mediatorMock.Verify(m => m.Send(It.IsAny<UpdateNormalizedDescriptionStatusCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	// ── Requeue pending (RECEIPTS-883) ──────────────────────────

	[Fact]
	public async Task PreviewRequeuePending_ReturnsOkWithMappedCounts()
	{
		_mediatorMock
			.Setup(m => m.Send(It.IsAny<PreviewRequeuePendingQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RequeuePendingPreview(4, "digest-abc", 120, 118, 3, 90));

		Ok<RequeuePendingPreviewResponse> result = await _controller.PreviewRequeuePending(CancellationToken.None);

		result.Value!.PendingDescriptionCount.Should().Be(4);
		result.Value.PendingFingerprint.Should().Be("digest-abc");
		result.Value.LinkedItemCount.Should().Be(120);
		result.Value.StaleMatchScoreCount.Should().Be(118);
		result.Value.EstimatedResolverCycles.Should().Be(3);
		result.Value.EstimatedCatchUpSeconds.Should().Be(90);
	}

	[Fact]
	public async Task RequeuePending_ReturnsOkWithMappedCounts()
	{
		RequeuePendingRequest request = new() { ExpectedFingerprint = "digest-abc" };

		_mediatorMock
			.Setup(m => m.Send(It.Is<RequeuePendingCommand>(c => c.ExpectedFingerprint == "digest-abc"), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RequeuePendingResult(4, 120, 118));

		Results<Ok<RequeuePendingResponse>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>> result =
			await _controller.RequeuePending(request, CancellationToken.None);

		Ok<RequeuePendingResponse> ok = Assert.IsType<Ok<RequeuePendingResponse>>(result.Result);
		ok.Value!.DeletedDescriptionCount.Should().Be(4);
		ok.Value.UnlinkedItemCount.Should().Be(120);
		ok.Value.ClearedMatchScoreCount.Should().Be(118);
	}

	[Fact]
	public async Task RequeuePending_SetChangedSincePreview_ReturnsConflict()
	{
		RequeuePendingRequest request = new() { ExpectedFingerprint = "digest-abc" };

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<RequeuePendingCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((RequeuePendingResult?)null);

		Results<Ok<RequeuePendingResponse>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>> result =
			await _controller.RequeuePending(request, CancellationToken.None);

		// 409, not 500 or a silent success: nothing was deleted and the caller must re-read.
		Conflict<ProblemDetails> conflict = Assert.IsType<Conflict<ProblemDetails>>(result.Result);
		conflict.Value!.Detail.Should().Be(NormalizedDescriptionsController.PendingSetChanged);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task RequeuePending_MissingFingerprint_ReturnsBadRequestWithoutDispatching(string fingerprint)
	{
		RequeuePendingRequest request = new() { ExpectedFingerprint = fingerprint };

		Results<Ok<RequeuePendingResponse>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>> result =
			await _controller.RequeuePending(request, CancellationToken.None);

		// Without a fingerprint there is nothing to compare against, so the guard would be
		// vacuous. Refuse rather than dispatch an unguarded bulk delete.
		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(NormalizedDescriptionsController.ExpectedFingerprintRequired);
		_mediatorMock.Verify(m => m.Send(It.IsAny<RequeuePendingCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task RequeuePending_EmptySetFingerprint_IsAllowedAsARerun()
	{
		RequeuePendingRequest request = new() { ExpectedFingerprint = "empty-digest" };

		_mediatorMock
			.Setup(m => m.Send(It.Is<RequeuePendingCommand>(c => c.ExpectedFingerprint == "empty-digest"), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RequeuePendingResult(0, 0, 0));

		Results<Ok<RequeuePendingResponse>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>> result =
			await _controller.RequeuePending(request, CancellationToken.None);

		// The empty set has a real digest, so a re-run is a legitimate call, not a validation failure.
		Ok<RequeuePendingResponse> ok = Assert.IsType<Ok<RequeuePendingResponse>>(result.Result);
		ok.Value!.DeletedDescriptionCount.Should().Be(0);
	}
}
