using Common;

namespace Application.Models.Ynab;

public sealed record YnabPushOperation(
	Guid SyncRecordId,
	YnabSyncStatus SyncStatus,
	YnabCreateTransactionRequest? Request,
	string SourceVersion,
	string PayloadHash,
	int AttemptCount,
	string? YnabTransactionId,
	string? LastError);

public sealed record YnabPushOperationClaim(
	YnabPushOperation Operation,
	Guid ClaimToken);
