using Common;
using Infrastructure.Entities.Core;

namespace SampleData.Entities;

public static class YnabSyncRecordEntityGenerator
{
	public static YnabSyncRecordEntity Generate(
		Guid? localTransactionId = null,
		string? ynabBudgetId = null,
		YnabSyncType syncType = YnabSyncType.TransactionPush)
	{
		return new YnabSyncRecordEntity
		{
			Id = Guid.NewGuid(),
			LocalTransactionId = localTransactionId ?? Guid.NewGuid(),
			YnabBudgetId = ynabBudgetId ?? "budget-" + Guid.NewGuid().ToString("N")[..8],
			SyncType = syncType,
			SyncStatus = YnabSyncStatus.Pending,
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow
		};
	}

	public static List<YnabSyncRecordEntity> GenerateList(int count, Guid? localTransactionId = null, string? ynabBudgetId = null)
	{
		var syncTypes = Enum.GetValues<YnabSyncType>();
		return [.. Enumerable.Range(0, count).Select(i => Generate(localTransactionId, ynabBudgetId, syncTypes[i % syncTypes.Length]))];
	}
}
