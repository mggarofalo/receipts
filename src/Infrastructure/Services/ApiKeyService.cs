using System.Security.Cryptography;
using System.Text;
using Application.Interfaces.Services;
using Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class ApiKeyService(IDbContextFactory<ApplicationDbContext> dbContextFactory) : IApiKeyService
{
	public async Task<CreateApiKeyResult> CreateApiKeyAsync(string userId, string name, DateTimeOffset? expiresAt, bool bypassRateLimit = false, CancellationToken cancellationToken = default)
	{
		string rawKey = GenerateRawKey();
		string keyHash = HashKey(rawKey);

		await using ApplicationDbContext context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
		ApiKeyEntity entity = new()
		{
			Id = Guid.NewGuid(),
			Name = name,
			KeyHash = keyHash,
			UserId = userId,
			CreatedAt = DateTimeOffset.UtcNow,
			ExpiresAt = expiresAt,
			IsRevoked = false,
			BypassRateLimit = bypassRateLimit,
		};

		context.ApiKeys.Add(entity);
		await context.SaveChangesAsync(cancellationToken);
		return new CreateApiKeyResult(rawKey, entity.Id, entity.CreatedAt);
	}

	public async Task<IReadOnlyList<ApiKeyInfo>> GetApiKeysForUserAsync(string userId, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
		return await context.ApiKeys
			.Where(k => k.UserId == userId && !k.IsRevoked)
			.Select(k => new ApiKeyInfo(k.Id, k.Name, k.CreatedAt, k.LastUsedAt, k.ExpiresAt, k.IsRevoked, k.BypassRateLimit))
			.ToListAsync(cancellationToken);
	}

	public async Task RevokeApiKeyAsync(Guid id, string userId, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
		ApiKeyEntity key = await context.ApiKeys
			.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId, cancellationToken)
			?? throw new KeyNotFoundException($"API key {id} not found for user {userId}.");

		key.IsRevoked = true;
		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task<int> RevokeAllForUserAsync(string userId, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
		List<ApiKeyEntity> keys = await context.ApiKeys
			.Where(k => k.UserId == userId && !k.IsRevoked)
			.ToListAsync(cancellationToken);

		if (keys.Count == 0)
		{
			return 0;
		}

		foreach (ApiKeyEntity key in keys)
		{
			key.IsRevoked = true;
		}

		await context.SaveChangesAsync(cancellationToken);
		return keys.Count;
	}

	// How recently LastUsedAt must have been stamped before a request skips re-stamping it.
	// This throttles the auth-path write: within one window we do at most one UPDATE per key,
	// which turns a per-request write into an occasional one under sustained traffic.
	private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(1);

	public async Task<ApiKeyValidationResult?> GetUserIdByApiKeyAsync(string rawKey, CancellationToken cancellationToken = default)
	{
		string keyHash = HashKey(rawKey);
		DateTimeOffset now = DateTimeOffset.UtcNow;

		await using ApplicationDbContext context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

		// Validation is unchanged (RECEIPTS-757): the key must exist, not be revoked, and not be
		// expired. AsNoTracking because we no longer mutate-and-save this entity — LastUsedAt is
		// stamped below with a targeted UPDATE. The per-request account-state check that rejects
		// deactivated/locked-out users lives in ApiKeyAuthenticationHandler and is untouched;
		// this method deliberately does NOT cache the result, so that check runs every request.
		ApiKeyEntity? key = await context.ApiKeys
			.AsNoTracking()
			.FirstOrDefaultAsync(
				k => k.KeyHash == keyHash && !k.IsRevoked && (k.ExpiresAt == null || k.ExpiresAt > now),
				cancellationToken);

		if (key is null)
		{
			return null;
		}

		// Stamp LastUsedAt cheaply. The throttle predicate makes the write a no-op when LastUsedAt
		// was set within the last minute, so sustained traffic on one key writes at most once per
		// window. Either way NO AuditLogs row is written: ApiKeyEntity is excluded from auditing
		// (see ApplicationDbContext.CollectAuditEntries) — a LastUsedAt bump is telemetry, not audit.
		DateTimeOffset throttleThreshold = now - LastUsedThrottle;

		if (context.Database.IsRelational())
		{
			// Production path: one targeted UPDATE that bypasses the change tracker entirely —
			// no load-modify-full-SaveChanges, no snapshot, no audit interceptor. The throttle lives
			// in the WHERE clause, so it is atomic and race-safe under concurrent requests.
			await context.ApiKeys
				.Where(k => k.Id == key.Id && (k.LastUsedAt == null || k.LastUsedAt < throttleThreshold))
				.ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken);
		}
		else if (key.LastUsedAt == null || key.LastUsedAt < throttleThreshold)
		{
			// The InMemory provider (unit tests) does not support ExecuteUpdate. Fall back to a
			// tracked update behind the same throttle; the audit exclusion above still keeps this
			// out of AuditLogs. Never taken against PostgreSQL in production.
			ApiKeyEntity? tracked = await context.ApiKeys
				.FirstOrDefaultAsync(k => k.Id == key.Id, cancellationToken);
			if (tracked is not null)
			{
				tracked.LastUsedAt = now;
				await context.SaveChangesAsync(cancellationToken);
			}
		}

		return new ApiKeyValidationResult(key.UserId, key.Id, key.BypassRateLimit);
	}

	private static string GenerateRawKey()
	{
		byte[] bytes = new byte[32];
		RandomNumberGenerator.Fill(bytes);
		return Convert.ToBase64String(bytes);
	}

	private static string HashKey(string rawKey)
	{
		byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
		return Convert.ToHexString(bytes).ToLowerInvariant();
	}
}
