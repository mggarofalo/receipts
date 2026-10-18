namespace Application.Duplicates;

public sealed record DuplicateAcceptanceWriteResult(int InsertedPairCount, IReadOnlyList<Guid> MissingReceiptIds);
