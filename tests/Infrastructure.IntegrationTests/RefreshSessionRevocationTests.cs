using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests;

[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class RefreshSessionRevocationTests(PostgresFixture fixture)
{
	private async Task<ApplicationUser> CreateUserAsync(Guid? family)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ApplicationUser user = new()
		{
			Id = Guid.NewGuid().ToString(),
			UserName = $"session-{Guid.NewGuid()}@example.com",
			RefreshSessionId = family,
			RefreshToken = "stored-token-hash",
			RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
			SecurityStamp = "security-stamp-must-not-change",
		};
		context.Users.Add(user);
		await context.SaveChangesAsync();
		return user;
	}

	[Fact]
	public async Task OldLogout_AfterNewLogin_DoesNotRevokeNewFamily()
	{
		Guid oldFamily = Guid.NewGuid();
		Guid newFamily = Guid.NewGuid();
		ApplicationUser user = await CreateUserAsync(oldFamily);
		await using ApplicationDbContext login = fixture.CreateDbContext();
		ApplicationUser latest = await login.Users.SingleAsync(u => u.Id == user.Id);
		using UserStore<ApplicationUser> store = new(login);
		latest.RefreshSessionId = newFamily;
		latest.RefreshToken = "new-login-token";
		(await store.UpdateAsync(latest)).Succeeded.Should().BeTrue();

		await using ApplicationDbContext logout = fixture.CreateDbContext();
		await new UserService(logout).RevokeRefreshSessionAsync(user.Id, oldFamily);

		ApplicationUser saved = await logout.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
		saved.RefreshToken.Should().Be("new-login-token");
		saved.RefreshSessionId.Should().Be(newFamily);
		saved.ConcurrencyStamp.Should().Be(latest.ConcurrencyStamp);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Logout_AfterRefreshRotation_RevokesSameFamilyIncludingLegacy(bool legacy)
	{
		Guid? family = legacy ? null : Guid.NewGuid();
		ApplicationUser user = await CreateUserAsync(family);
		await using ApplicationDbContext refresh = fixture.CreateDbContext();
		ApplicationUser rotating = await refresh.Users.SingleAsync(u => u.Id == user.Id);
		using UserStore<ApplicationUser> store = new(refresh);
		rotating.RefreshToken = "rotated-token";
		(await store.UpdateAsync(rotating)).Succeeded.Should().BeTrue();

		await using ApplicationDbContext logout = fixture.CreateDbContext();
		await new UserService(logout).RevokeRefreshSessionAsync(user.Id, family);

		ApplicationUser saved = await logout.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
		saved.RefreshToken.Should().BeNull();
		saved.RefreshTokenExpiresAt.Should().BeNull();
		saved.SecurityStamp.Should().Be(user.SecurityStamp);
		saved.ConcurrencyStamp.Should().NotBe(rotating.ConcurrencyStamp);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Logout_BeforeAlreadyLoadedRefreshSaves_PreventsTokenResurrection(bool legacy)
	{
		Guid? family = legacy ? null : Guid.NewGuid();
		ApplicationUser user = await CreateUserAsync(family);
		await using ApplicationDbContext refresh = fixture.CreateDbContext();
		ApplicationUser stale = await refresh.Users.SingleAsync(u => u.Id == user.Id);
		using UserStore<ApplicationUser> store = new(refresh);
		await using ApplicationDbContext logout = fixture.CreateDbContext();
		await new UserService(logout).RevokeRefreshSessionAsync(user.Id, family);

		stale.RefreshToken = "attempted-resurrection";
		IdentityResult result = await store.UpdateAsync(stale);

		result.Succeeded.Should().BeFalse();
		result.Errors.Should().Contain(e => e.Code == "ConcurrencyFailure");
		(await logout.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).RefreshToken.Should().BeNull();
	}

	[Fact]
	public async Task LegacyLogout_DoesNotRevokeNewFamily()
	{
		ApplicationUser user = await CreateUserAsync(Guid.NewGuid());
		await using ApplicationDbContext context = fixture.CreateDbContext();

		await new UserService(context).RevokeRefreshSessionAsync(user.Id, null);

		ApplicationUser saved = await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
		saved.RefreshToken.Should().Be(user.RefreshToken);
		saved.ConcurrencyStamp.Should().Be(user.ConcurrencyStamp);
	}

	[Fact]
	public async Task FamilyLogout_DoesNotRevokeLegacyOrAnotherUser()
	{
		Guid family = Guid.NewGuid();
		ApplicationUser legacy = await CreateUserAsync(null);
		ApplicationUser other = await CreateUserAsync(family);
		await using ApplicationDbContext context = fixture.CreateDbContext();

		await new UserService(context).RevokeRefreshSessionAsync(legacy.Id, family);
		await new UserService(context).RevokeRefreshSessionAsync("missing-user", family);

		(await context.Users.AsNoTracking().SingleAsync(u => u.Id == legacy.Id)).RefreshToken.Should().Be(legacy.RefreshToken);
		(await context.Users.AsNoTracking().SingleAsync(u => u.Id == other.Id)).RefreshToken.Should().Be(other.RefreshToken);
	}

	[Fact]
	public async Task DuplicateLogout_DoesNotInvalidateUpdatesAfterRevocation()
	{
		Guid family = Guid.NewGuid();
		ApplicationUser user = await CreateUserAsync(family);
		await using ApplicationDbContext context = fixture.CreateDbContext();
		UserService service = new(context);
		await service.RevokeRefreshSessionAsync(user.Id, family);
		string? stamp = await context.Users.Where(u => u.Id == user.Id).Select(u => u.ConcurrencyStamp).SingleAsync();

		await service.RevokeRefreshSessionAsync(user.Id, family);

		(await context.Users.Where(u => u.Id == user.Id).Select(u => u.ConcurrencyStamp).SingleAsync()).Should().Be(stamp);
	}

	[Fact]
	public async Task RefreshFamilyMigration_MatchesModel_AndSupportsLegacySessions()
	{
		ApplicationUser legacy = await CreateUserAsync(null);
		await using ApplicationDbContext context = fixture.CreateDbContext();

		context.Database.HasPendingModelChanges().Should().BeFalse();
		(await context.Users.AsNoTracking().SingleAsync(u => u.Id == legacy.Id)).RefreshSessionId.Should().BeNull();
		(await context.Database.GetAppliedMigrationsAsync()).Should().Contain("20260905190000_AddRefreshSessionId");
	}
}
