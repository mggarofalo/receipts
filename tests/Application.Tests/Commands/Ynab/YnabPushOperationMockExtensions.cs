using Application.Interfaces.Services;
using Application.Models.Ynab;
using Common;
using Moq;

namespace Application.Tests.Commands.Ynab;

internal static class YnabPushOperationMockExtensions
{
	public static void SetupPushOperationDefaults(this Mock<IYnabSyncRecordService> service)
	{
		Dictionary<(Guid TransactionId, string BudgetId), YnabPushOperation> operations = [];
		Dictionary<Guid, int> attempts = [];
		service.Setup(s => s.GetPushOperationIdentitiesByReceiptAsync(
				It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);
		service.Setup(s => s.PreparePushOperationAsync(
				It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<YnabCreateTransactionRequest>(),
				It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((Guid transactionId, string budgetId, YnabCreateTransactionRequest request,
				string sourceVersion, CancellationToken _) =>
			{
				if (operations.TryGetValue((transactionId, budgetId), out YnabPushOperation? existing))
				{
					return existing;
				}

				YnabPushOperation created = new(
					Guid.NewGuid(), YnabSyncStatus.Pending, request, sourceVersion, "payload-hash", 0, null, null);
				operations[(transactionId, budgetId)] = created;
				return created;
			});
		service.Setup(s => s.TryClaimPushOperationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((Guid id, CancellationToken _) =>
			{
				YnabPushOperation operation = operations.Values.Single(value => value.SyncRecordId == id);
				int attempt = attempts.TryGetValue(id, out int prior) ? prior + 1 : 1;
				attempts[id] = attempt;
				return new YnabPushOperationClaim(operation with { AttemptCount = attempt }, Guid.NewGuid());
			});
		service.Setup(s => s.CompletePushOperationAsync(
				It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<YnabSyncStatus>(), It.IsAny<string?>(),
				It.IsAny<string?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);
	}
}
