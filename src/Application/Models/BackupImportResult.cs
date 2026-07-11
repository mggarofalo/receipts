namespace Application.Models;

public record BackupImportResult(
	int AccountsCreated,
	int AccountsUpdated,
	int CardsCreated,
	int CardsUpdated,
	int CategoriesCreated,
	int CategoriesUpdated,
	int SubcategoriesCreated,
	int SubcategoriesUpdated,
	int ItemTemplatesCreated,
	int ItemTemplatesUpdated,
	int ReceiptsCreated,
	int ReceiptsUpdated,
	int ReceiptItemsCreated,
	int ReceiptItemsUpdated,
	int TransactionsCreated,
	int TransactionsUpdated,
	int AdjustmentsCreated,
	int AdjustmentsUpdated,
	int YnabSelectedBudgetsCreated = 0,
	int YnabSelectedBudgetsUpdated = 0,
	int YnabAccountMappingsCreated = 0,
	int YnabAccountMappingsUpdated = 0,
	int YnabCategoryMappingsCreated = 0,
	int YnabCategoryMappingsUpdated = 0,
	int YnabSyncRecordsCreated = 0,
	int YnabSyncRecordsUpdated = 0,
	int NormalizedDescriptionsCreated = 0,
	int NormalizedDescriptionsUpdated = 0)
{
	public int TotalCreated => AccountsCreated + CardsCreated + CategoriesCreated + SubcategoriesCreated +
		ItemTemplatesCreated + ReceiptsCreated + ReceiptItemsCreated +
		TransactionsCreated + AdjustmentsCreated +
		YnabSelectedBudgetsCreated + YnabAccountMappingsCreated + YnabCategoryMappingsCreated +
		YnabSyncRecordsCreated + NormalizedDescriptionsCreated;

	public int TotalUpdated => AccountsUpdated + CardsUpdated + CategoriesUpdated + SubcategoriesUpdated +
		ItemTemplatesUpdated + ReceiptsUpdated + ReceiptItemsUpdated +
		TransactionsUpdated + AdjustmentsUpdated +
		YnabSelectedBudgetsUpdated + YnabAccountMappingsUpdated + YnabCategoryMappingsUpdated +
		YnabSyncRecordsUpdated + NormalizedDescriptionsUpdated;
}
