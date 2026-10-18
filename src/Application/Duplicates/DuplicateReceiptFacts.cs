namespace Application.Duplicates;

/// <summary>The facts a pairwise duplicate acceptance acknowledges, independent of search tolerances.</summary>
public sealed record DuplicateReceiptFacts(Guid ReceiptId, DateOnly Date, string Location, decimal PaymentTotal);
