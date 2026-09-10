using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using Common;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetYnabSyncRecordByTransactionQueryHandlerTests
{
	[Fact]
	public async Task Handle_QueriesTheSelectedBudgetOnly()
	{
		Guid transactionId = Guid.NewGuid();
		YnabSyncRecordDto expected = new(
			Guid.NewGuid(), transactionId, "ynab-B", "budget-B", null,
			YnabSyncType.TransactionPush, YnabSyncStatus.Synced, DateTimeOffset.UtcNow, null,
			DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
		Mock<IYnabSyncRecordService> records = new();
		records.Setup(s => s.GetByTransactionTypeAndBudgetAsync(
			transactionId, YnabSyncType.TransactionPush, "budget-B", It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);
		Mock<IYnabBudgetSelectionService> budgetSelection = new();
		budgetSelection.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync("budget-B");
		GetYnabSyncRecordByTransactionQueryHandler handler = new(records.Object, budgetSelection.Object);

		YnabSyncRecordDto? result = await handler.Handle(
			new GetYnabSyncRecordByTransactionQuery(transactionId, YnabSyncType.TransactionPush),
			CancellationToken.None);

		result.Should().BeSameAs(expected);
	}

	[Fact]
	public async Task Handle_NoSelectedBudget_ReturnsNullWithoutConsultingHistoricalRecords()
	{
		Mock<IYnabSyncRecordService> records = new();
		Mock<IYnabBudgetSelectionService> budgetSelection = new();
		budgetSelection.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);
		GetYnabSyncRecordByTransactionQueryHandler handler = new(records.Object, budgetSelection.Object);

		YnabSyncRecordDto? result = await handler.Handle(
			new GetYnabSyncRecordByTransactionQuery(Guid.NewGuid(), YnabSyncType.TransactionPush),
			CancellationToken.None);

		result.Should().BeNull();
		records.Verify(s => s.GetByTransactionTypeAndBudgetAsync(
			It.IsAny<Guid>(), It.IsAny<YnabSyncType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}
