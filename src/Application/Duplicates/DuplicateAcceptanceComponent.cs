namespace Application.Duplicates;

public sealed record DuplicateAcceptanceComponent(List<Guid> ReceiptIds, DateTimeOffset AcceptedAt);
