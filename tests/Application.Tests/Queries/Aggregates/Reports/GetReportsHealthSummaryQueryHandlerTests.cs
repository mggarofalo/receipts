using Application.Interfaces.Services;
using Application.Models.Reports;
using Application.Queries.Aggregates.Reports;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Aggregates.Reports;

public class GetReportsHealthSummaryQueryHandlerTests
{
	private readonly Mock<IReportService> _reportServiceMock;
	private readonly GetReportsHealthSummaryQueryHandler _handler;

	public GetReportsHealthSummaryQueryHandlerTests()
	{
		_reportServiceMock = new Mock<IReportService>();
		_handler = new GetReportsHealthSummaryQueryHandler(_reportServiceMock.Object);
	}

	[Fact]
	public async Task Handle_DelegatesToReportService()
	{
		// Arrange
		GetReportsHealthSummaryQuery query = new();
		ReportsHealthSummaryResult expectedResult = new(3, 2, 17);

		_reportServiceMock.Setup(s => s.GetHealthSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(expectedResult);

		// Act
		ReportsHealthSummaryResult result = await _handler.Handle(query, CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expectedResult);
		_reportServiceMock.Verify(s => s.GetHealthSummaryAsync(It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_PropagatesCancellationToken()
	{
		// Arrange
		using CancellationTokenSource cts = new();
		GetReportsHealthSummaryQuery query = new();

		_reportServiceMock.Setup(s => s.GetHealthSummaryAsync(cts.Token))
			.ReturnsAsync(new ReportsHealthSummaryResult(0, 0, 0));

		// Act
		await _handler.Handle(query, cts.Token);

		// Assert
		_reportServiceMock.Verify(s => s.GetHealthSummaryAsync(cts.Token), Times.Once);
	}
}
