using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

public class UserServiceTests
{
	private static ApplicationDbContext CreateContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase($"UserServiceTests_{Guid.NewGuid()}")
			.Options);

	[Fact]
	public void HashRefreshToken_ProducesSha256Hex_ThatDiffersFromPlaintext()
	{
		using ApplicationDbContext context = CreateContext();
		UserService service = new(context);

		const string token = "some-random-refresh-token";
		string hash = service.HashRefreshToken(token);

		hash.Should().NotBe(token);
		hash.Should().HaveLength(64); // SHA-256 = 32 bytes = 64 lowercase hex chars
		hash.Should().MatchRegex("^[0-9a-f]{64}$");
		service.HashRefreshToken(token).Should().Be(hash); // deterministic
	}

	[Fact]
	public async Task FindUserIdByRefreshTokenAsync_MatchesOnHash_WhenStoredValueIsHashed()
	{
		using ApplicationDbContext context = CreateContext();
		UserService service = new(context);

		const string plaintext = "the-plaintext-refresh-token";
		context.Users.Add(new ApplicationUser
		{
			Id = "user-1",
			Email = "u@example.com",
			UserName = "u@example.com",
			// Stored hashed, exactly as AuthController persists it.
			RefreshToken = service.HashRefreshToken(plaintext),
			RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
		});
		await context.SaveChangesAsync();

		// A valid plaintext token still resolves to its user (hashed internally before comparison).
		string? foundId = await service.FindUserIdByRefreshTokenAsync(plaintext);
		foundId.Should().Be("user-1");
	}

	[Fact]
	public async Task FindUserIdByRefreshTokenAsync_DoesNotMatchStoredPlaintext()
	{
		using ApplicationDbContext context = CreateContext();
		UserService service = new(context);

		const string plaintext = "leaked-plaintext-token";
		// A legacy/attacker row that stored the raw token must NOT be found via the hashed lookup.
		context.Users.Add(new ApplicationUser
		{
			Id = "user-2",
			Email = "v@example.com",
			UserName = "v@example.com",
			RefreshToken = plaintext,
			RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
		});
		await context.SaveChangesAsync();

		string? foundId = await service.FindUserIdByRefreshTokenAsync(plaintext);
		foundId.Should().BeNull();
	}

	[Fact]
	public async Task FindUserIdByRefreshTokenAsync_ReturnsNull_ForEmptyToken()
	{
		using ApplicationDbContext context = CreateContext();
		UserService service = new(context);

		string? foundId = await service.FindUserIdByRefreshTokenAsync("");
		foundId.Should().BeNull();
	}
}
