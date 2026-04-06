namespace Application.Models.Ynab;

public record YnabAccount(string Id, string Name, string Type, bool OnBudget, bool Closed, long Balance);
