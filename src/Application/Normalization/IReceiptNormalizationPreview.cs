namespace Application.Normalization;

public sealed record ReceiptNormalizationCandidate(Guid Id, Guid Revision, string Name);
public sealed record ReceiptNormalizationPreview(string SourceFingerprint, Guid? MappingId, Guid? MappingRevision,
	Guid? TargetId, Guid? TargetRevision, string? TargetName, bool Excluded,
	IReadOnlyList<ReceiptNormalizationCandidate> Candidates, int Total, int Offset, int Limit, bool SourceAmbiguous = false);

public interface IReceiptNormalizationPreview
{
	Task<ReceiptNormalizationPreview> PreviewAsync(NormalizationSourceKey source, string? query,
		int offset, int limit, CancellationToken cancellationToken);
}
