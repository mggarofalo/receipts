using System.Net;
using Application.Interfaces.Services;

namespace Infrastructure.Ynab;

/// <summary>
/// The OAuth access token could not be renewed for a temporary reason, so the YNAB request
/// was never sent. It is an <see cref="HttpRequestException"/> so it is handled like any other
/// temporary YNAB failure, and a definite rejection because nothing reached the YNAB API.
/// </summary>
public sealed class YnabTokenRenewalUnavailableException(string message)
	: HttpRequestException(message, null, HttpStatusCode.ServiceUnavailable), IYnabDefiniteRejection;
