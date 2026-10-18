namespace Application.Duplicates;

public sealed record DuplicateAcceptedPair(Guid ReceiptIdA, Guid ReceiptIdB, DateTimeOffset AcceptedAt);
