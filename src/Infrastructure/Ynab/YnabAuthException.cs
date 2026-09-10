using Application.Interfaces.Services;

namespace Infrastructure.Ynab;

public class YnabAuthException(string message) : Exception(message), IYnabDefiniteRejection;
