namespace Application.NormalizationAi;

public interface INormalizationAiService
{
	Task<NormalizationAiConnection> GetConnectionAsync(string actor, CancellationToken cancellationToken);
	Task<NormalizationAiCandidatePage> ListCandidatesAsync(string actor, string? q, int offset, int limit, CancellationToken cancellationToken);
	Task<IReadOnlyList<NormalizationAiRun>> ListRunsAsync(string actor, CancellationToken cancellationToken);
	Task<NormalizationAiRun?> GetRunAsync(string actor, Guid id, CancellationToken cancellationToken);
	Task<NormalizationAiRun> StartAsync(string actor, Guid clientRequestId, IReadOnlyList<Guid> itemIds, string model, CancellationToken cancellationToken);
	Task<NormalizationAiRun> CancelAsync(string actor, Guid id, CancellationToken cancellationToken);
	Task<NormalizationAiRun> RetryAsync(string actor, Guid id, Guid clientRequestId, CancellationToken cancellationToken);
	Task<NormalizationAiRun> ApplyAsync(string actor, Guid id, IReadOnlyList<Guid> proposalIds, Guid expectedRevision, CancellationToken cancellationToken);
	Task<Guid> BeginConnectionChangeAsync(CancellationToken cancellationToken);
	Task CompleteConnectionChangeAsync(Guid changeId, CancellationToken cancellationToken);
}

public sealed record NormalizationAiConnection(bool Configured, bool IsOwner, bool CredentialsPresent, Guid Generation, IReadOnlyList<string> Models, bool Busy = false);
public sealed record NormalizationAiCandidate(Guid Id, Guid ReceiptId, string Description, string Location, DateOnly Date);
public sealed record NormalizationAiCandidatePage(IReadOnlyList<NormalizationAiCandidate> Items, int Total);
public sealed record NormalizationAiRun(Guid Id, Guid Revision, Guid ClientRequestId, string State, string Model,
	DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? ErrorCode, string? ErrorDetail,
	IReadOnlyList<NormalizationAiProposal> Proposals, ChatGptUsage? Usage);
public sealed record NormalizationAiProposal(Guid Id, Guid ReceiptItemId, string Description, string Action,
	Guid? TargetId, string? CanonicalName, string? DisplayLabel, string? Reason, string State);

public sealed class NormalizationAiException(int statusCode, string code, string detail) : Exception(detail)
{
	public int StatusCode { get; } = statusCode;
	public string Code { get; } = code;
}
