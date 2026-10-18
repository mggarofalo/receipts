namespace Application.Models.Search;

public record PurchaseSearchResult(IReadOnlyList<PurchaseSearchItem> Items, int Total, int Offset, int Limit);
