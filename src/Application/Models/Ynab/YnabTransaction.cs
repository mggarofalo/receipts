namespace Application.Models.Ynab;

public record YnabTransaction(string Id, DateOnly Date, long Amount, string? Memo, string ClearedStatus, bool Approved, string AccountId, string? CategoryId, string? PayeeName);
