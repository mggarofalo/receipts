namespace Infrastructure.Interfaces.Repositories;

public sealed record CascadeMutationResult(
	bool EntityChanged,
	int YnabSyncRecordsChanged);
