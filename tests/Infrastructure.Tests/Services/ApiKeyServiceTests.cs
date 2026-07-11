using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Infrastructure.Tests.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

public class ApiKeyServiceTests
{
	private static ApiKeyEntity CreateKey(string userId, bool isRevoked = false) => new()
	{
		Id = Guid.NewGuid(),
		Name = "test-key",
		KeyHash = Guid.NewGuid().ToString("N"),
		UserId = userId,
		CreatedAt = DateTimeOffset.UtcNow,
		IsRevoked = isRevoked,
	};

	[Fact]
	public async Task RevokeAllForUserAsync_RevokesAllActiveKeysForUser_AndReturnsCount()
	{
		// Arrange
		(IDbContextFactory<ApplicationDbContext> contextFactory, _) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		ApiKeyEntity active1 = CreateKey("user-a");
		ApiKeyEntity active2 = CreateKey("user-a");
		ApiKeyEntity alreadyRevoked = CreateKey("user-a", isRevoked: true);
		ApiKeyEntity otherUserKey = CreateKey("user-b");

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.ApiKeys.AddRangeAsync(active1, active2, alreadyRevoked, otherUserKey);
			await context.SaveChangesAsync();
		}

		ApiKeyService service = new(contextFactory);

		// Act
		int revokedCount = await service.RevokeAllForUserAsync("user-a");

		// Assert — only the two active keys for user-a were revoked
		revokedCount.Should().Be(2);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			(await context.ApiKeys.FindAsync(active1.Id))!.IsRevoked.Should().BeTrue();
			(await context.ApiKeys.FindAsync(active2.Id))!.IsRevoked.Should().BeTrue();
			(await context.ApiKeys.FindAsync(alreadyRevoked.Id))!.IsRevoked.Should().BeTrue();
			// Another user's key must not be touched.
			(await context.ApiKeys.FindAsync(otherUserKey.Id))!.IsRevoked.Should().BeFalse();
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task RevokeAllForUserAsync_ReturnsZero_WhenUserHasNoActiveKeys()
	{
		// Arrange
		(IDbContextFactory<ApplicationDbContext> contextFactory, _) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		ApiKeyService service = new(contextFactory);

		// Act
		int revokedCount = await service.RevokeAllForUserAsync("user-with-no-keys");

		// Assert
		revokedCount.Should().Be(0);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetUserIdByApiKeyAsync_StampsLastUsedAt_WithoutWritingAuditLog()
	{
		// Arrange — a real key created by the service, with a current user set so that any
		// AUDITED write on this path would be attributed and persisted to AuditLogs.
		(IDbContextFactory<ApplicationDbContext> contextFactory, MockCurrentUserAccessor accessor) =
			DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		accessor.UserId = "user-a";

		ApiKeyService service = new(contextFactory);
		CreateApiKeyResult created = await service.CreateApiKeyAsync("user-a", "my-key", expiresAt: null);

		// Act — the auth hot path.
		ApiKeyValidationResult? result = await service.GetUserIdByApiKeyAsync(created.RawKey);

		// Assert — the key validated, LastUsedAt was stamped (was null after create)...
		result.Should().NotBeNull();
		result!.UserId.Should().Be("user-a");

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ApiKeyEntity persisted = await context.ApiKeys.SingleAsync(k => k.Id == created.Id);
			persisted.LastUsedAt.Should().NotBeNull();

			// ...and NOT a single AuditLog row was written for the API-key auth path
			// (RECEIPTS-769): the LastUsedAt bump is telemetry, not an audit event.
			(await context.AuditLogs.CountAsync()).Should().Be(0);
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetUserIdByApiKeyAsync_DoesNotRestampLastUsedAt_WithinThrottleWindow()
	{
		// Arrange — a key whose LastUsedAt was stamped moments ago (inside the ~1-minute throttle).
		(IDbContextFactory<ApplicationDbContext> contextFactory, _) =
			DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		ApiKeyService service = new(contextFactory);
		CreateApiKeyResult created = await service.CreateApiKeyAsync("user-a", "my-key", expiresAt: null);

		DateTimeOffset recent = DateTimeOffset.UtcNow.AddSeconds(-10);
		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ApiKeyEntity key = await context.ApiKeys.SingleAsync(k => k.Id == created.Id);
			key.LastUsedAt = recent;
			await context.SaveChangesAsync();
		}

		// Act — a second authentication within the throttle window.
		ApiKeyValidationResult? result = await service.GetUserIdByApiKeyAsync(created.RawKey);

		// Assert — still validates, but LastUsedAt is left untouched (the throttled UPDATE is a no-op),
		// so sustained traffic on one key does not write on every request.
		result.Should().NotBeNull();
		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ApiKeyEntity persisted = await context.ApiKeys.SingleAsync(k => k.Id == created.Id);
			persisted.LastUsedAt.Should().BeCloseTo(recent, TimeSpan.FromMilliseconds(1));
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task RevokeAllForUserAsync_CausesKeyToStopAuthenticating()
	{
		// Arrange — a real key that validates before revocation.
		(IDbContextFactory<ApplicationDbContext> contextFactory, _) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		ApiKeyService service = new(contextFactory);
		CreateApiKeyResult created = await service.CreateApiKeyAsync("user-a", "my-key", expiresAt: null);

		(await service.GetUserIdByApiKeyAsync(created.RawKey)).Should().NotBeNull();

		// Act
		int revokedCount = await service.RevokeAllForUserAsync("user-a");

		// Assert — key no longer authenticates once bulk-revoked.
		revokedCount.Should().Be(1);
		(await service.GetUserIdByApiKeyAsync(created.RawKey)).Should().BeNull();

		contextFactory.ResetDatabase();
	}
}
