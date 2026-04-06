namespace Infrastructure.Ynab;

public class YnabNotFoundException(string message) : Exception(message);
