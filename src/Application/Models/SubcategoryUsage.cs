namespace Application.Models;

/// <summary>Historical item usage for one current category/subcategory name pair, including trash.</summary>
public record SubcategoryUsage(int ReceiptItemCount, List<SubcategoryAffectedReceipt> AffectedReceipts);

public record SubcategoryAffectedReceipt(Guid ReceiptId, DateOnly Date, string Location, bool IsDeleted);
