using Application.Interfaces.Services;
using Application.Models.Ynab;
using Infrastructure.Ynab;

namespace Infrastructure.Services;

public class YnabRateLimitTracker : IYnabRateLimitTracker
{
	private readonly Queue<DateTimeOffset> _requestTimestamps = new();
	private readonly object _lock = new();
	private readonly YnabClientOptions _options;
	private readonly TimeProvider _timeProvider;

	// Latest authoritative counts parsed from YNAB's X-Rate-Limit header. Preferred over the
	// in-process estimate while still within the rate-limit window.
	private int? _serverUsed;
	private int? _serverLimit;
	private DateTimeOffset? _serverObservedAt;

	public YnabRateLimitTracker(YnabClientOptions options, TimeProvider timeProvider)
	{
		_options = options;
		_timeProvider = timeProvider;
	}

	public void RecordRequest()
	{
		lock (_lock)
		{
			PruneExpired();
			_requestTimestamps.Enqueue(_timeProvider.GetUtcNow());
		}
	}

	public void RecordServerRateLimit(int used, int limit)
	{
		if (limit <= 0)
		{
			return;
		}

		lock (_lock)
		{
			_serverUsed = Math.Max(0, used);
			_serverLimit = limit;
			_serverObservedAt = _timeProvider.GetUtcNow();
		}
	}

	public YnabRateLimitStatus GetStatus()
	{
		lock (_lock)
		{
			PruneExpired();

			int max = _options.RateLimitMaxRequests;
			int requestsUsed = _requestTimestamps.Count;

			// Prefer YNAB's authoritative header counts while the snapshot is still within the
			// rate-limit window; fall back to the in-process sliding-window estimate otherwise.
			bool serverFresh = _serverObservedAt is { } observed
				&& _serverUsed is { } serverUsed
				&& _serverLimit is { } serverLimit
				&& observed >= _timeProvider.GetUtcNow().AddSeconds(-_options.RateLimitWindowSeconds);

			if (serverFresh)
			{
				max = _serverLimit!.Value;
				requestsUsed = _serverUsed!.Value;
			}

			int remaining = Math.Max(0, max - requestsUsed);

			DateTimeOffset? windowResetAt = null;
			DateTimeOffset? oldestRequestAt = null;

			if (_requestTimestamps.TryPeek(out DateTimeOffset oldest))
			{
				oldestRequestAt = oldest;
				windowResetAt = oldest.Add(TimeSpan.FromSeconds(_options.RateLimitWindowSeconds));
			}

			return new YnabRateLimitStatus(
				remaining,
				max,
				requestsUsed,
				windowResetAt,
				oldestRequestAt);
		}
	}

	public bool CanMakeRequests(int count)
	{
		lock (_lock)
		{
			PruneExpired();
			int requestsUsed = _requestTimestamps.Count;
			return requestsUsed + count <= _options.RateLimitMaxRequests;
		}
	}

	private void PruneExpired()
	{
		DateTimeOffset cutoff = _timeProvider.GetUtcNow().AddSeconds(-_options.RateLimitWindowSeconds);

		while (_requestTimestamps.TryPeek(out DateTimeOffset oldest) && oldest < cutoff)
		{
			_requestTimestamps.Dequeue();
		}
	}
}
