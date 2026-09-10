using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class DateTimeOffsetUtcTests(PostgresFixture fixture)
{
	[Fact]
	public async Task NonUtcDateTimeOffset_IsPersisted_AsUtc()
	{
		// Arrange — create a user so the ApiKey FK is satisfied
		await using ApplicationDbContext context = fixture.CreateDbContext();

		ApplicationUser user = new()
		{
			Id = Guid.NewGuid().ToString(),
			UserName = $"test-utc-{Guid.NewGuid():N}",
			NormalizedUserName = $"TEST-UTC-{Guid.NewGuid():N}",
			Email = "utctest@example.com",
			NormalizedEmail = "UTCTEST@EXAMPLE.COM",
			SecurityStamp = Guid.NewGuid().ToString(),
			CreatedAt = DateTimeOffset.UtcNow,
		};
		context.Users.Add(user);
		await context.SaveChangesAsync();

		// Create an ApiKey with a deliberately non-UTC DateTimeOffset (EST = -5h)
		DateTimeOffset easternTime = new(2026, 3, 15, 10, 30, 0, TimeSpan.FromHours(-5));
		ApiKeyEntity apiKey = new()
		{
			Id = Guid.NewGuid(),
			Name = "UTC-Test-Key",
			KeyHash = $"hash-{Guid.NewGuid():N}",
			UserId = user.Id,
			CreatedAt = easternTime,
			ExpiresAt = easternTime.AddDays(30),
		};

		// Act — save to real Postgres (this would throw without the UTC converter)
		context.ApiKeys.Add(apiKey);
		await context.SaveChangesAsync();

		// Assert — read it back and verify UTC normalization
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		ApiKeyEntity? loaded = await readContext.ApiKeys.FirstOrDefaultAsync(k => k.Id == apiKey.Id);

		loaded.Should().NotBeNull();
		loaded!.CreatedAt.Offset.Should().Be(TimeSpan.Zero, "Npgsql returns timestamptz as UTC");
		loaded.CreatedAt.Should().Be(easternTime.ToUniversalTime());
		loaded.ExpiresAt!.Value.Offset.Should().Be(TimeSpan.Zero);
		loaded.ExpiresAt.Value.Should().Be(easternTime.AddDays(30).ToUniversalTime());
	}

	[Fact]
	public async Task UtcDateTimeOffset_RoundTrips_Correctly()
	{
		// Arrange
		await using ApplicationDbContext context = fixture.CreateDbContext();

		ApplicationUser user = new()
		{
			Id = Guid.NewGuid().ToString(),
			UserName = $"test-roundtrip-{Guid.NewGuid():N}",
			NormalizedUserName = $"TEST-ROUNDTRIP-{Guid.NewGuid():N}",
			Email = "roundtrip@example.com",
			NormalizedEmail = "ROUNDTRIP@EXAMPLE.COM",
			SecurityStamp = Guid.NewGuid().ToString(),
			CreatedAt = DateTimeOffset.UtcNow,
		};
		context.Users.Add(user);
		await context.SaveChangesAsync();

		DateTimeOffset utcNow = DateTimeOffset.UtcNow;
		ApiKeyEntity apiKey = new()
		{
			Id = Guid.NewGuid(),
			Name = "RoundTrip-Key",
			KeyHash = $"hash-{Guid.NewGuid():N}",
			UserId = user.Id,
			CreatedAt = utcNow,
		};

		// Act
		context.ApiKeys.Add(apiKey);
		await context.SaveChangesAsync();

		// Assert
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		ApiKeyEntity? loaded = await readContext.ApiKeys.FirstOrDefaultAsync(k => k.Id == apiKey.Id);

		loaded.Should().NotBeNull();
		loaded!.CreatedAt.Should().BeCloseTo(utcNow, TimeSpan.FromMilliseconds(1));
		loaded.CreatedAt.Offset.Should().Be(TimeSpan.Zero);
	}
}
