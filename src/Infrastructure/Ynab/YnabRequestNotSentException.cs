using Application.Interfaces.Services;

namespace Infrastructure.Ynab;

public sealed class YnabRequestNotSentException(string message, Exception innerException)
	: Exception(message, innerException), IYnabDefiniteRejection;
