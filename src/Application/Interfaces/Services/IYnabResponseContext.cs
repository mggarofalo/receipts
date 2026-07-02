namespace Application.Interfaces.Services;

/// <summary>
/// Scoped holder for transport-level metadata from the most recent YNAB API response
/// (status code and, when present, a request id). Populated by the YNAB API client after
/// each call so business-layer handlers can attach it to the <c>YnabSyncEvent</c> they write.
/// </summary>
public interface IYnabResponseContext
{
	int? LastStatusCode { get; }
	string? LastRequestId { get; }
	void Record(int statusCode, string? requestId);
}
