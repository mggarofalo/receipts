using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public sealed record PreparedYnabPushOperation(
	YnabSyncRecordEntity Record,
	bool Created);
