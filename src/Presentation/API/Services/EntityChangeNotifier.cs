using System.Collections.Concurrent;
using System.Security.Claims;
using API.Authentication;
using API.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace API.Services;

public sealed class EntityChangeNotifier : IEntityChangeNotifier, IDisposable
{
	private readonly IHubContext<EntityHub, IEntityHubClient> _hubContext;
	private readonly IHttpContextAccessor _httpContextAccessor;
	private readonly ILogger<EntityChangeNotifier> _logger;
	private readonly ConcurrentDictionary<(string EntityType, string ChangeType, string? UserId, string? AuthMethod, string? ConnectionId), NotificationBucket> _pending = new();
	private readonly Timer _flushTimer;
	private readonly TimeSpan _flushInterval;
	private int _disposed;

	public EntityChangeNotifier(IHubContext<EntityHub, IEntityHubClient> hubContext, IHttpContextAccessor httpContextAccessor, ILogger<EntityChangeNotifier> logger)
		: this(hubContext, httpContextAccessor, TimeSpan.FromSeconds(1), logger)
	{
	}

	internal EntityChangeNotifier(IHubContext<EntityHub, IEntityHubClient> hubContext, IHttpContextAccessor httpContextAccessor, TimeSpan flushInterval, ILogger<EntityChangeNotifier>? logger = null)
	{
		_hubContext = hubContext;
		_httpContextAccessor = httpContextAccessor;
		_logger = logger ?? NullLogger<EntityChangeNotifier>.Instance;
		_flushInterval = flushInterval;
		// Route the timer through TimerFlushAsync so a throw can never fault the callback or
		// surface as an unobserved task exception — the timer must keep firing (RECEIPTS-794).
		_flushTimer = new Timer(static state => _ = ((EntityChangeNotifier)state!).TimerFlushAsync(), this, _flushInterval, _flushInterval);
	}

	public Task NotifyCreated(string entityType, Guid id)
	{
		Enqueue(entityType, "created", id);
		return Task.CompletedTask;
	}

	public Task NotifyUpdated(string entityType, Guid id)
	{
		Enqueue(entityType, "updated", id);
		return Task.CompletedTask;
	}

	public Task NotifyDeleted(string entityType, Guid id)
	{
		Enqueue(entityType, "deleted", id);
		return Task.CompletedTask;
	}

	public Task NotifyBulkChanged(string entityType, string changeType, IEnumerable<Guid> ids)
	{
		foreach (Guid id in ids)
		{
			Enqueue(entityType, changeType, id);
		}
		return Task.CompletedTask;
	}

	public Task NotifyAllChanged(string entityType, string changeType)
	{
		Enqueue(entityType, changeType, id: null);
		return Task.CompletedTask;
	}

	private void Enqueue(string entityType, string changeType, Guid? id)
	{
		var origin = CaptureOrigin();
		var key = (entityType, changeType, origin.UserId, origin.AuthMethod, origin.ConnectionId);
		_pending.AddOrUpdate(
			key,
			_ => new NotificationBucket(id),
			(_, bucket) =>
			{
				bucket.Add(id);
				return bucket;
			});
	}

	private NotificationOrigin CaptureOrigin()
	{
		var httpContext = _httpContextAccessor.HttpContext;
		if (httpContext is null)
		{
			return new NotificationOrigin(null, null, null);
		}

		var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
		var authType = httpContext.User.Identity?.AuthenticationType;
		var authMethod = authType == ApiKeyAuthenticationDefaults.AuthenticationScheme
			? "apikey"
			: authType is not null
				? "jwt"
				: null;
		// connectionId is client-supplied and untrusted; spoofing suppresses a toast
		// but does not affect data integrity or query invalidation.
		var connectionId = httpContext.Request.Headers["X-SignalR-Connection-Id"].FirstOrDefault();

		return new NotificationOrigin(userId, authMethod, connectionId);
	}

	private async Task TimerFlushAsync()
	{
		try
		{
			await FlushAsync();
		}
		catch (Exception ex)
		{
			// FlushAsync already handles per-send failures; this is a last-resort guard so an
			// unexpected throw can never fault the timer callback or become an unobserved
			// exception. The timer keeps firing on its interval regardless.
			_logger.LogError(ex, "Unexpected error in entity-change notifier flush timer.");
		}
	}

	internal async Task FlushAsync()
	{
		// Snapshot and remove all pending buckets atomically per key.
		List<(string EntityType, string ChangeType, string? UserId, string? AuthMethod, string? ConnectionId, int Count)> toSend = [];
		foreach (var key in _pending.Keys)
		{
			if (_pending.TryRemove(key, out NotificationBucket? bucket))
			{
				toSend.Add((key.EntityType, key.ChangeType, key.UserId, key.AuthMethod, key.ConnectionId, bucket.Count));
			}
		}

		for (int i = 0; i < toSend.Count; i++)
		{
			var (entityType, changeType, userId, authMethod, connectionId, count) = toSend[i];
			try
			{
				await _hubContext.Clients.All.EntityChanged(
					new EntityChangeNotification(entityType, changeType, null, count, userId, authMethod, connectionId));
			}
			catch (Exception ex)
			{
				// A hub/transport failure mid-flush must not permanently drop notifications we
				// already dequeued. Re-enqueue this bucket and every one not yet sent so the
				// next timer tick (or Dispose) retries them, then log and stop (RECEIPTS-794).
				int requeued = 0;
				for (int j = i; j < toSend.Count; j++)
				{
					Requeue(toSend[j]);
					requeued++;
				}

				_logger.LogError(
					ex,
					"Failed to flush entity-change notifications; re-enqueued {Requeued} batch(es) for retry.",
					requeued);
				return;
			}
		}
	}

	private void Requeue((string EntityType, string ChangeType, string? UserId, string? AuthMethod, string? ConnectionId, int Count) item)
	{
		var key = (item.EntityType, item.ChangeType, item.UserId, item.AuthMethod, item.ConnectionId);
		_pending.AddOrUpdate(
			key,
			_ => new NotificationBucket(item.Count),
			(_, existing) =>
			{
				// A fresh bucket may have accumulated for this key since we dequeued; merge the
				// un-sent count into it so nothing is lost and nothing is double-counted.
				existing.Add(item.Count);
				return existing;
			});
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			_flushTimer.Dispose();
			// Fire one last flush synchronously to drain pending notifications. FlushAsync
			// swallows and logs its own send failures, so this cannot throw during teardown.
			FlushAsync().GetAwaiter().GetResult();
		}
	}

	private sealed record NotificationOrigin(string? UserId, string? AuthMethod, string? ConnectionId);

	private sealed class NotificationBucket
	{
		private int _count;

		public NotificationBucket(Guid? initialId)
		{
			_ = initialId; // Individual IDs not needed for aggregated notifications
			_count = 1;
		}

		public NotificationBucket(int initialCount)
		{
			_count = initialCount;
		}

		public int Count => Volatile.Read(ref _count);

		public void Add(Guid? id)
		{
			_ = id;
			Interlocked.Increment(ref _count);
		}

		public void Add(int delta) => Interlocked.Add(ref _count, delta);
	}
}
