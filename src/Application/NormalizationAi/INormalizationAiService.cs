namespace Application.NormalizationAi;

public interface INormalizationAiService
{
	Task<NormalizationAiConnection> GetConnectionAsync(string actor, CancellationToken cancellationToken);
	Task<IReadOnlyList<NormalizationAiRun>> ListRunsAsync(string actor, CancellationToken cancellationToken);
	Task<NormalizationAiRun?> GetRunAsync(string actor, Guid id, CancellationToken cancellationToken);
	Task<NormalizationAiRun> StartSourcesAsync(string actor, Guid clientRequestId, IReadOnlyList<NormalizationAiSourceSelection> sources, string model, CancellationToken cancellationToken);
	Task<NormalizationAiRun> CancelAsync(string actor, Guid id, CancellationToken cancellationToken);
	Task<NormalizationAiRun> RetryAsync(string actor, Guid id, Guid clientRequestId, CancellationToken cancellationToken);
	Task<NormalizationAiRun> ApplyAsync(string actor, Guid id, IReadOnlyList<Guid> proposalIds, Guid expectedRevision, CancellationToken cancellationToken);
	Task<Guid> BeginConnectionChangeAsync(CancellationToken cancellationToken);
	Task CompleteConnectionChangeAsync(Guid changeId, CancellationToken cancellationToken);
	Task<bool> RecoverConnectionChangeAsync(CancellationToken cancellationToken);
	Task BindConnectionChangeAsync(Guid changeId, string consentingOwner, CancellationToken cancellationToken);
}

public sealed record NormalizationAiConnection(bool Configured, bool IsOwner, bool CredentialsPresent, Guid Generation, IReadOnlyList<string> Models, bool Busy = false);
public sealed record NormalizationAiRun(Guid Id, Guid Revision, Guid ClientRequestId, string State, string Model,
	DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? ErrorCode, string? ErrorDetail,
	IReadOnlyList<NormalizationAiProposal> Proposals, ChatGptUsage? Usage);
public sealed record NormalizationAiProposal(Guid Id, Guid ReceiptItemId, string Description, string Action,
	Guid? TargetId, string? CanonicalName, string? DisplayLabel, string? Reason, string State,
	Application.Normalization.NormalizationSourceKey? Source = null, Guid? TargetRevision = null, bool IncludeLegacyUnknown = false, bool LabelOnly = false, string? SuggestedDisplayLabel = null);

public sealed record NormalizationAiSourceSelection(Application.Normalization.NormalizationSourceKey Source, bool IncludeLegacyUnknown, bool LabelOnly = false);

public sealed class NormalizationAiException(int statusCode, string code, string detail, IReadOnlyDictionary<string, object?>? extensions = null) : Exception(detail)
{
	public int StatusCode { get; } = statusCode;
	public string Code { get; } = code;
	public IReadOnlyDictionary<string, object?> Extensions { get; } = extensions ?? new Dictionary<string, object?>();
}
