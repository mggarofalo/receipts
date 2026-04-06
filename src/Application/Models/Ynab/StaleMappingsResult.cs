namespace Application.Models.Ynab;

public record StaleMappingsResult(int StaleAccountMappingCount, int StaleCategoryMappingCount, string? CurrentBudgetId);
