namespace Application.Models.Ynab;

public record YnabTransactionsResult(List<YnabTransaction> Transactions, long ServerKnowledge);
