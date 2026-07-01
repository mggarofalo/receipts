using Application.Interfaces.Services;

namespace Infrastructure.Services;

/// <summary>
/// Scoped, per-request holder populated by <see cref="YnabApiClient"/> after each call.
/// </summary>
public class YnabResponseContext : IYnabResponseContext
{
	public int? LastStatusCode { get; private set; }
	public string? LastRequestId { get; private set; }

	public void Record(int statusCode, string? requestId)
	{
		LastStatusCode = statusCode;
		LastRequestId = requestId;
	}
}
