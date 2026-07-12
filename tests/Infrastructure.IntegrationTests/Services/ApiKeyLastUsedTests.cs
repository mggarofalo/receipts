using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

// Postgres-only coverage for RECEIPTS-769 (PR #612): the auth hot path stamps ApiKey.LastUsedAt via
// ExecuteUpdateAsync — the RELATIONAL branch of GetUserIdByApiKeyAsync. The InMemory unit suite can
// only reach the tracked-fallback branch (ExecuteUpdate is unsupported there), so the production
// path — the targeted UPDATE, its in-WHERE throttle, and its audit-bypass — is proven only here.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ApiKeyLastUsedTests(PostgresFixture fixture)
{
	[Fact]
	public async Task GetUserIdByApiKeyAsync_OnRelationalProvider_StampsLastUsedAt_ThrottlesRestamp_AndWritesNoAudit()
	{
		// Arrange — a real user (the ApiKey.UserId FK is enforced by Postgres) and a key created by
		// the service. Suppress auditing on the user seed so only the key path is under observation.
		string userId = Guid.NewGuid().ToString();
		{
			await using ApplicationDbContext setup = fixture.CreateDbContext();
			setup.AuditingEnabled = false;
			setup.Users.Add(new ApplicationUser
			{
				Id = userId,
				UserName = $"apikey-{userId}",
				NormalizedUserName = $"APIKEY-{userId}".ToUpperInvariant(),
				Email = $"{userId}@test.local",
				NormalizedEmail = $"{userId}@TEST.LOCAL".ToUpperInvariant(),
				SecurityStamp = Guid.NewGuid().ToString(),
			});
			await setup.SaveChangesAsync();
		}

		ApiKeyService service = new(new FixtureDbContextFactory(fixture));
		CreateApiKeyResult created = await service.CreateApiKeyAsync(userId, "integration-key", expiresAt: null);

		// Sanity — a freshly created key has never been used.
		(await ReadLastUsedAsync(created.Id)).Should().BeNull("CreateApiKeyAsync does not stamp LastUsedAt");

		// Act 1 — the auth hot path stamps LastUsedAt through the relational ExecuteUpdate.
		ApiKeyValidationResult? first = await service.GetUserIdByApiKeyAsync(created.RawKey);

		// Assert 1 — validated and stamped.
		first.Should().NotBeNull();
		first!.UserId.Should().Be(userId);
		DateTimeOffset? stampedAt = await ReadLastUsedAsync(created.Id);
		stampedAt.Should().NotBeNull("the relational path must stamp LastUsedAt on first use");

		// Act 2 — a second authentication immediately after, well inside the 1-minute throttle window.
		ApiKeyValidationResult? second = await service.GetUserIdByApiKeyAsync(created.RawKey);

		// Assert 2 — still validates, but the throttle predicate makes the UPDATE a no-op: LastUsedAt
		// is unchanged, so sustained traffic on one key does not write on every request.
		second.Should().NotBeNull();
		DateTimeOffset? afterSecond = await ReadLastUsedAsync(created.Id);
		afterSecond.Should().BeCloseTo(stampedAt!.Value, TimeSpan.FromMilliseconds(1),
			"a second call within the throttle window must not re-stamp LastUsedAt");

		// Assert 3 — a LastUsedAt bump is telemetry, not audit: the ExecuteUpdate bypasses the change
		// tracker/interceptor, and ApiKeyEntity is excluded from auditing. No ApiKey audit rows exist.
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		int apiKeyAuditRows = await verify.AuditLogs.AsNoTracking()
			.CountAsync(a => a.EntityType == "ApiKey");
		apiKeyAuditRows.Should().Be(0, "the API-key auth path must never write an AuditLogs row");
	}

	private async Task<DateTimeOffset?> ReadLastUsedAsync(Guid apiKeyId)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		return await context.ApiKeys.AsNoTracking()
			.Where(k => k.Id == apiKeyId)
			.Select(k => k.LastUsedAt)
			.SingleAsync();
	}

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
