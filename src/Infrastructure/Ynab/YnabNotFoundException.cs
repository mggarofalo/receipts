using Application.Interfaces.Services;

namespace Infrastructure.Ynab;

public class YnabNotFoundException(string message) : Exception(message), IYnabDefiniteRejection;
