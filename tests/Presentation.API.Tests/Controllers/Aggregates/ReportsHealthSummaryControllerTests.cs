using API.Controllers.Aggregates;
using API.Generated.Dtos;
using Application.Queries.Aggregates.Reports;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using AppReports = Application.Models.Reports;

namespace Presentation.API.Tests.Controllers.Aggregates;

public class ReportsHealthSummaryControllerTests
{
	private readonly Mock<IMediator> _mediatorMock;
	private readonly ReportsController _controller;

	public ReportsHealthSummaryControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_controller = new ReportsController(_mediatorMock.Object);
	}

	[Fact]
	public async Task GetHealthSummary_ReturnsOkResult_WithEachCount()
	{
		// Arrange
		AppReports.ReportsHealthSummaryResult summary = new(3, 2, 17);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetReportsHealthSummaryQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(summary);

		// Act
		Ok<ReportsHealthSummaryResponse> result = await _controller.GetHealthSummary(CancellationToken.None);

		// Assert
		ReportsHealthSummaryResponse response = result.Value!;
		response.OutOfBalanceCount.Should().Be(3);
		response.DuplicateGroupCount.Should().Be(2);
		response.UncategorizedItemCount.Should().Be(17);
	}

	[Fact]
	public async Task GetHealthSummary_ReturnsZeros_WhenNothingNeedsAttention()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetReportsHealthSummaryQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new AppReports.ReportsHealthSummaryResult(0, 0, 0));

		// Act
		Ok<ReportsHealthSummaryResponse> result = await _controller.GetHealthSummary(CancellationToken.None);

		// Assert
		ReportsHealthSummaryResponse response = result.Value!;
		response.OutOfBalanceCount.Should().Be(0);
		response.DuplicateGroupCount.Should().Be(0);
		response.UncategorizedItemCount.Should().Be(0);
	}

	[Fact]
	public async Task GetHealthSummary_SendsTheQueryExactlyOnce()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetReportsHealthSummaryQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new AppReports.ReportsHealthSummaryResult(1, 1, 1));

		// Act
		await _controller.GetHealthSummary(CancellationToken.None);

		// Assert
		_mediatorMock.Verify(m => m.Send(
			It.IsAny<GetReportsHealthSummaryQuery>(),
			It.IsAny<CancellationToken>()), Times.Once);
	}
}
