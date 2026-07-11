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
