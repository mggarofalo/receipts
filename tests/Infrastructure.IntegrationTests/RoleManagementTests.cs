using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using API.Configuration;
using API.Controllers;
using API.Generated.Dtos;
using Application.Interfaces.Services;
using Application.Models;
using Common;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// A private PostgreSQL fixture keeps global Admin membership counts independent from other
/// integration classes. Real Identity stores, signed JWTs, API keys, authorization policies,
/// and both production controllers participate; only audit delivery is stubbed.
/// </summary>
[Trait("Category", "Integration")]
public class RoleManagementTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
	private WebApplication _app = null!;
	private readonly StoreFailure _failure = new();
	private static readonly IConfiguration Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
	{
		["Jwt:Key"] = "role-tests-signing-key-at-least-32-characters!!",
		["Jwt:Issuer"] = "role-tests",
		["Jwt:Audience"] = "role-tests",
	}).Build();

	public async Task InitializeAsync()
	{
		await using (ApplicationDbContext context = fixture.CreateDbContext())
		{
			await context.ApiKeys.ExecuteDeleteAsync();
			await context.Users.ExecuteDeleteAsync();
		}
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new ContextFactory(fixture));
		builder.Services.AddScoped(_ => fixture.CreateDbContext());
		builder.Services.AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
		builder.Services.Replace(ServiceDescriptor.Scoped<IUserStore<ApplicationUser>>(provider =>
			new FailingUserStore(provider.GetRequiredService<ApplicationDbContext>(), _failure)));
		builder.Services.AddScoped<IRoleManagementService, RoleManagementService>();
		builder.Services.AddScoped<IUserService, UserService>();
		builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
		builder.Services.AddSingleton(Mock.Of<IAuthAuditService>());
		builder.Services.AddAuthServices(Configuration);
		builder.Services.AddControllers().AddApplicationPart(typeof(UsersController).Assembly);
		_app = builder.Build();
		_app.UseAuthentication();
		_app.UseAuthorization();
		_app.MapControllers();
		_app.MapGet("/ordinary", () => Results.Ok()).RequireAuthorization();
		_app.MapGet("/admin", () => Results.Ok()).RequireAuthorization("RequireAdmin");
		await _app.StartAsync();
		await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
		RoleManager<IdentityRole> roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
		foreach (string role in AppRoles.All)
		{
			if (!await roles.RoleExistsAsync(role))
			{
				(await roles.CreateAsync(new IdentityRole(role))).Succeeded.Should().BeTrue();
			}
		}
	}

	public async Task DisposeAsync() => await _app.DisposeAsync();

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Demotion_BothRoutes_ImmediatelyInvalidatesOldJwt_AndApiKeyReadsLiveRoles(bool replace)
	{
		ApplicationUser actor = await CreateUserAsync(AppRoles.Admin);
		ApplicationUser target = await CreateUserAsync(AppRoles.Admin, AppRoles.User);
		string oldToken = await MintAsync(target.Id);
		string actorToken = await MintAsync(actor.Id);
		CreateApiKeyResult key;
		await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope())
		{
			key = await scope.ServiceProvider.GetRequiredService<IApiKeyService>().CreateApiKeyAsync(target.Id, "role test", null);
		}
		(await GetStatusAsync(oldToken, "/admin")).Should().Be(HttpStatusCode.OK);
		(await GetStatusAsync(key.RawKey, "/admin", apiKey: true)).Should().Be(HttpStatusCode.OK);

		using HttpResponseMessage result = await DemoteAsync(actorToken, target, replace);

		result.StatusCode.Should().Be(HttpStatusCode.NoContent, await result.Content.ReadAsStringAsync());
		(await GetStatusAsync(oldToken, "/ordinary")).Should().Be(HttpStatusCode.Unauthorized);
		(await GetStatusAsync(oldToken, "/admin")).Should().Be(HttpStatusCode.Unauthorized);
		string freshToken = await MintAsync(target.Id);
		(await GetStatusAsync(freshToken, "/ordinary")).Should().Be(HttpStatusCode.OK);
		(await GetStatusAsync(freshToken, "/admin")).Should().Be(HttpStatusCode.Forbidden);
		(await GetStatusAsync(actorToken, "/admin")).Should().Be(HttpStatusCode.OK);
		(await GetStatusAsync(key.RawKey, "/ordinary", apiKey: true)).Should().Be(HttpStatusCode.OK);
		(await GetStatusAsync(key.RawKey, "/admin", apiKey: true)).Should().Be(HttpStatusCode.Forbidden);
		await using ApplicationDbContext context = fixture.CreateDbContext();
		(await context.ApiKeys.SingleAsync(k => k.Id == key.Id)).IsRevoked.Should().BeFalse();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MixedSubjects_CannotBorrowAdminAuthorityToBypassSelfDemotionGuard(bool replace)
	{
		ApplicationUser target = await CreateUserAsync(AppRoles.Admin);
		ApplicationUser keyOwner = await CreateUserAsync(AppRoles.User);
		await CreateUserAsync(AppRoles.Admin);
		await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
		CreateApiKeyResult key = await scope.ServiceProvider.GetRequiredService<IApiKeyService>().CreateApiKeyAsync(keyOwner.Id, "other subject", null);
		using HttpClient client = Client(await MintAsync(target.Id));
		client.DefaultRequestHeaders.Add("X-API-Key", key.RawKey);

		using HttpResponseMessage result = replace
			? await client.PutAsJsonAsync($"/api/users/{target.Id}", new UpdateUserRequest { Email = "changed@example.com", Role = AppRoles.User })
			: await client.DeleteAsync($"/api/users/{target.Id}/roles/Admin");

		result.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await result.Content.ReadAsStringAsync()).Should().Contain("single user");
		await AssertUnchangedAsync(target, [AppRoles.Admin]);
	}

	[Fact]
	public async Task SameSubjectJwtAndApiKey_CanManageAnotherUsersRoles()
	{
		ApplicationUser actor = await CreateUserAsync(AppRoles.Admin);
		ApplicationUser target = await CreateUserAsync(AppRoles.User);
		await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
		CreateApiKeyResult key = await scope.ServiceProvider.GetRequiredService<IApiKeyService>().CreateApiKeyAsync(actor.Id, "same subject", null);
		using HttpClient client = Client(await MintAsync(actor.Id));
		client.DefaultRequestHeaders.Add("X-API-Key", key.RawKey);

		using HttpResponseMessage result = await client.PostAsync($"/api/users/{target.Id}/roles/Admin", null);

		result.StatusCode.Should().Be(HttpStatusCode.NoContent);
		(await GetStatusAsync(await MintAsync(target.Id), "/admin")).Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Promotion_InvalidatesOldJwt_AndNewJwtHasAdminRole()
	{
		ApplicationUser actor = await CreateUserAsync(AppRoles.Admin);
		ApplicationUser target = await CreateUserAsync(AppRoles.User);
		string oldToken = await MintAsync(target.Id);
		using HttpClient client = Client(await MintAsync(actor.Id));

		using HttpResponseMessage result = await client.PostAsync($"/api/users/{target.Id}/roles/Admin", null);

		result.StatusCode.Should().Be(HttpStatusCode.NoContent);
		(await GetStatusAsync(oldToken, "/ordinary")).Should().Be(HttpStatusCode.Unauthorized);
		(await GetStatusAsync(await MintAsync(target.Id), "/admin")).Should().Be(HttpStatusCode.OK);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SelfDemotion_BothRoutes_IsRejectedWithoutChangingProfileRolesOrStamp(bool replace)
	{
		ApplicationUser target = await CreateUserAsync(AppRoles.Admin, AppRoles.User);
		await CreateUserAsync(AppRoles.Admin);
		string token = await MintAsync(target.Id);

		using HttpResponseMessage result = await DemoteAsync(token, target, replace);

		result.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await result.Content.ReadAsStringAsync()).Should().Contain("Cannot remove your own Admin role");
		await AssertUnchangedAsync(target, [AppRoles.Admin, AppRoles.User]);
		(await GetStatusAsync(token, "/admin")).Should().Be(HttpStatusCode.OK);
	}

	[Theory]
	[InlineData(RoleChangeMode.Add, "User")]
	[InlineData(RoleChangeMode.Remove, "Admin")]
	[InlineData(RoleChangeMode.Replace, "User")]
	public async Task EffectiveNoOp_PreservesStampAndExistingJwt(RoleChangeMode mode, string role)
	{
		ApplicationUser target = await CreateUserAsync(AppRoles.User);
		string token = await MintAsync(target.Id);
		UserProfileUpdate? profile = mode == RoleChangeMode.Replace ? new(target.Email!, "Edited", "Profile", false) : null;

		RoleChangeResult result = await ChangeAsync(target.Id, "other", mode, [role], profile);

		result.Status.Should().Be(RoleChangeStatus.Success);
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ApplicationUser stored = await context.Users.SingleAsync(u => u.Id == target.Id);
		stored.SecurityStamp.Should().Be(target.SecurityStamp);
		if (profile is not null)
		{
			stored.FirstName.Should().Be("Edited");
		}

		(await GetStatusAsync(token, "/ordinary")).Should().Be(HttpStatusCode.OK);
	}

	[Theory]
	[InlineData(RoleChangeMode.Remove)]
	[InlineData(RoleChangeMode.Replace)]
	public async Task LastAdminMembership_CannotBeRemoved_AndRejectedProfileDoesNotPersist(RoleChangeMode mode)
	{
		ApplicationUser target = await CreateUserAsync(AppRoles.Admin);
		UserProfileUpdate? profile = mode == RoleChangeMode.Replace ? new("changed@example.com", "Changed", "Name", false) : null;

		RoleChangeResult result = await ChangeAsync(target.Id, "another-actor", mode,
			mode == RoleChangeMode.Remove ? [AppRoles.Admin] : [AppRoles.User], profile);

		result.Status.Should().Be(RoleChangeStatus.Invalid);
		result.Errors.Should().Contain("Cannot remove the last Admin role.");
		await AssertUnchangedAsync(target, [AppRoles.Admin]);
	}

	[Fact]
	public async Task SelfDisable_IsRejectedBeforeProfileOrRoleChanges()
	{
		ApplicationUser target = await CreateUserAsync(AppRoles.Admin);
		using HttpClient client = Client(await MintAsync(target.Id));

		using HttpResponseMessage result = await client.PutAsJsonAsync($"/api/users/{target.Id}", new UpdateUserRequest
		{
			Email = "changed@example.com",
			FirstName = "Changed",
			Role = "Admin",
			IsDisabled = true,
		});

		result.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await result.Content.ReadAsStringAsync()).Should().Contain("Cannot disable your own account");
		await AssertUnchangedAsync(target, [AppRoles.Admin]);
	}

	[Fact]
	public async Task DisableThenEnable_PreservesLockoutSupport_AndInvalidatesJwtWhenDisabling()
	{
		ApplicationUser actor = await CreateUserAsync(AppRoles.Admin);
		ApplicationUser target = await CreateUserAsync(AppRoles.User);
		string oldToken = await MintAsync(target.Id);
		using HttpClient client = Client(await MintAsync(actor.Id));
		UpdateUserRequest request = new() { Email = target.Email!, Role = AppRoles.User, IsDisabled = true };

		using HttpResponseMessage disabled = await client.PutAsJsonAsync($"/api/users/{target.Id}", request);
		disabled.StatusCode.Should().Be(HttpStatusCode.NoContent);
		await using (ApplicationDbContext context = fixture.CreateDbContext())
		{
			ApplicationUser stored = await context.Users.SingleAsync(u => u.Id == target.Id);
			stored.LockoutEnabled.Should().BeTrue();
			stored.LockoutEnd.Should().Be(DateTimeOffset.MaxValue);
			stored.SecurityStamp.Should().NotBe(target.SecurityStamp);
		}
		(await GetStatusAsync(oldToken, "/ordinary")).Should().Be(HttpStatusCode.Unauthorized);
		request.IsDisabled = false;
		using HttpResponseMessage enabled = await client.PutAsJsonAsync($"/api/users/{target.Id}", request);
		enabled.StatusCode.Should().Be(HttpStatusCode.NoContent);
		await using ApplicationDbContext enabledContext = fixture.CreateDbContext();
		ApplicationUser enabledUser = await enabledContext.Users.SingleAsync(u => u.Id == target.Id);
		enabledUser.LockoutEnabled.Should().BeTrue();
		enabledUser.LockoutEnd.Should().BeNull();
		(await GetStatusAsync(await MintAsync(target.Id), "/ordinary")).Should().Be(HttpStatusCode.OK);
	}

	[Theory]
	[InlineData(1)] // Profile update.
	[InlineData(2)] // Remove previous membership, after profile was persisted inside the transaction.
	[InlineData(3)] // Add replacement membership, after the previous membership was removed.
	[InlineData(4)] // Security stamp, after both role writes.
	public async Task IdentityFailure_AtEveryReplacementStage_RollsBackProfileRolesAndStamp(int failingUpdate)
	{
		ApplicationUser actor = await CreateUserAsync(AppRoles.Admin);
		ApplicationUser target = await CreateUserAsync(AppRoles.Admin);
		_failure.Arm(target.Id, failingUpdate);
		using HttpClient client = Client(await MintAsync(actor.Id));

		using HttpResponseMessage result = await client.PutAsJsonAsync($"/api/users/{target.Id}", new UpdateUserRequest
		{
			Email = "changed@example.com",
			FirstName = "Changed",
			LastName = "Profile",
			Role = AppRoles.User,
			IsDisabled = true,
		});

		result.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await result.Content.ReadAsStringAsync()).Should().Contain("Injected Identity failure");
		_failure.Updates.Should().Be(failingUpdate, "later Identity operations must stop after failure");
		await AssertUnchangedAsync(target, [AppRoles.Admin]);
	}

	[Theory]
	[InlineData(RoleChangeMode.Add)]
	[InlineData(RoleChangeMode.Remove)]
	public async Task SingleRoleRoute_ReportsIdentityFailure_WithoutPartialMembershipOrStampChange(RoleChangeMode mode)
	{
		ApplicationUser actor = await CreateUserAsync(AppRoles.Admin);
		ApplicationUser target = await CreateUserAsync(mode == RoleChangeMode.Add ? AppRoles.User : AppRoles.Admin);
		_failure.Arm(target.Id, 1);
		using HttpClient client = Client(await MintAsync(actor.Id));

		using HttpResponseMessage result = mode == RoleChangeMode.Add
			? await client.PostAsync($"/api/users/{target.Id}/roles/Admin", null)
			: await client.DeleteAsync($"/api/users/{target.Id}/roles/Admin");

		result.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await result.Content.ReadAsStringAsync()).Should().Contain("Injected Identity failure");
		await AssertUnchangedAsync(target, [mode == RoleChangeMode.Add ? AppRoles.User : AppRoles.Admin]);
	}

	[Fact]
	public async Task IdentityConcurrencyFailure_ReturnsConflict_AndRollsBackEarlierWrites()
	{
		ApplicationUser actor = await CreateUserAsync(AppRoles.Admin);
		ApplicationUser target = await CreateUserAsync(AppRoles.Admin);
		_failure.Arm(target.Id, 3, concurrency: true);
		using HttpResponseMessage result = await DemoteAsync(await MintAsync(actor.Id), target, replace: true);

		result.StatusCode.Should().Be(HttpStatusCode.Conflict);
		(await result.Content.ReadAsStringAsync()).Should().Contain("Injected Identity failure");
		await AssertUnchangedAsync(target, [AppRoles.Admin]);
	}

	[Fact]
	public async Task ConcurrentExternalUserUpdate_ReturnsConflict_AndDoesNotOverwriteNewStateOrAddMembership()
	{
		ApplicationUser target = await CreateUserAsync(AppRoles.User);
		_failure.BeforeUpdate = async () =>
		{
			await using ApplicationDbContext otherContext = fixture.CreateDbContext();
			await otherContext.Users.Where(u => u.Id == target.Id).ExecuteUpdateAsync(update => update
				.SetProperty(u => u.FirstName, "Concurrent edit")
				.SetProperty(u => u.ConcurrencyStamp, Guid.NewGuid().ToString()));
		};

		RoleChangeResult result = await ChangeAsync(target.Id, "other", RoleChangeMode.Add, [AppRoles.Admin]);

		result.Status.Should().Be(RoleChangeStatus.Conflict);
		await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
		UserManager<ApplicationUser> manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
		ApplicationUser stored = (await manager.FindByIdAsync(target.Id))!;
		stored.FirstName.Should().Be("Concurrent edit");
		stored.SecurityStamp.Should().Be(target.SecurityStamp);
		(await manager.GetRolesAsync(stored)).Should().BeEquivalentTo([AppRoles.User]);
	}

	[Fact]
	public async Task DuplicateUserName_IsRejectedByRealIdentityValidation_WithoutProfileOrRoleChanges()
	{
		ApplicationUser existing = await CreateUserAsync(AppRoles.Admin);
		ApplicationUser target = await CreateUserAsync(AppRoles.User);

		RoleChangeResult result = await ChangeAsync(target.Id, existing.Id, RoleChangeMode.Replace, [AppRoles.Admin],
			new UserProfileUpdate(existing.Email!, "Changed", "Profile", false));

		result.Status.Should().Be(RoleChangeStatus.Invalid);
		result.Errors.Should().Contain(error => error.Contains("already taken"));
		await AssertUnchangedAsync(target, [AppRoles.User]);
	}

	[Fact]
	public async Task ConcurrentCrossDemotions_PreserveOneAdmin_AndOnlyCommittedChangeRotatesStamp()
	{
		ApplicationUser first = await CreateUserAsync(AppRoles.Admin);
		ApplicationUser second = await CreateUserAsync(AppRoles.Admin);
		await using AsyncServiceScope firstScope = _app.Services.CreateAsyncScope();
		await using AsyncServiceScope secondScope = _app.Services.CreateAsyncScope();
		// Authentication can have loaded either target before the serialized operation begins.
		await firstScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(second.Id);
		await secondScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(first.Id);

		Task<RoleChangeResult> firstChange = firstScope.ServiceProvider.GetRequiredService<IRoleManagementService>()
			.ChangeAsync(second.Id, first.Id, RoleChangeMode.Remove, [AppRoles.Admin]);
		Task<RoleChangeResult> secondChange = secondScope.ServiceProvider.GetRequiredService<IRoleManagementService>()
			.ChangeAsync(first.Id, second.Id, RoleChangeMode.Remove, [AppRoles.Admin]);
		RoleChangeResult[] results = await Task.WhenAll(firstChange, secondChange).WaitAsync(TimeSpan.FromSeconds(15));

		results.Count(r => r.Status == RoleChangeStatus.Success).Should().Be(1);
		results.Count(r => r.Status == RoleChangeStatus.Invalid).Should().Be(1);
		await using ApplicationDbContext context = fixture.CreateDbContext();
		string adminRoleId = await context.Roles.Where(r => r.Name == AppRoles.Admin).Select(r => r.Id).SingleAsync();
		List<string> remaining = await context.UserRoles.Where(r => r.RoleId == adminRoleId).Select(r => r.UserId).ToListAsync();
		remaining.Should().ContainSingle();
		foreach (ApplicationUser original in new[] { first, second })
		{
			ApplicationUser stored = await context.Users.SingleAsync(u => u.Id == original.Id);
			if (remaining.Contains(original.Id))
			{
				stored.SecurityStamp.Should().Be(original.SecurityStamp);
			}
			else
			{
				stored.SecurityStamp.Should().NotBe(original.SecurityStamp);
			}
		}
	}

	[Fact]
	public async Task MissingUser_AndInvalidRole_AreReportedWithoutChangingOtherMembership()
	{
		ApplicationUser target = await CreateUserAsync(AppRoles.Admin);
		(await ChangeAsync("missing", target.Id, RoleChangeMode.Add, [AppRoles.User])).Status.Should().Be(RoleChangeStatus.NotFound);
		(await ChangeAsync(target.Id, "other", RoleChangeMode.Add, ["admin"])).Status.Should().Be(RoleChangeStatus.Invalid);
		await AssertUnchangedAsync(target, [AppRoles.Admin]);
	}

	[Theory]
	[InlineData("", RoleChangeMode.Add, false)]
	[InlineData("other", (RoleChangeMode)99, false)]
	[InlineData("other", RoleChangeMode.Add, true)]
	public async Task InvalidOperationArguments_AreRejectedWithoutWrites(string actor, RoleChangeMode mode, bool includeProfile)
	{
		ApplicationUser target = await CreateUserAsync(AppRoles.User);

		RoleChangeResult result = await ChangeAsync(target.Id, actor, mode, [AppRoles.Admin],
			includeProfile ? new UserProfileUpdate("changed@example.com", "Changed", "Profile", false) : null);

		result.Status.Should().Be(RoleChangeStatus.Invalid);
		await AssertUnchangedAsync(target, [AppRoles.User]);
	}

	private async Task<ApplicationUser> CreateUserAsync(params string[] roles)
	{
		await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
		UserManager<ApplicationUser> manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
		string email = $"{Guid.NewGuid():N}@example.com";
		ApplicationUser user = new() { Email = email, UserName = email, FirstName = "Original", LastName = "Name", CreatedAt = DateTimeOffset.UtcNow };
		(await manager.CreateAsync(user)).Succeeded.Should().BeTrue();
		(await manager.AddToRolesAsync(user, roles)).Succeeded.Should().BeTrue();
		return user;
	}

	private async Task<string> MintAsync(string userId)
	{
		await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
		UserManager<ApplicationUser> manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
		ApplicationUser user = (await manager.FindByIdAsync(userId))!;
		return new TokenService(Configuration).GenerateAccessToken(user.Id, user.Email!, await manager.GetRolesAsync(user), false, user.SecurityStamp!);
	}

	private HttpClient Client(string credential, bool apiKey = false)
	{
		HttpClient client = _app.GetTestClient();
		if (apiKey)
		{
			client.DefaultRequestHeaders.Add("X-API-Key", credential);
		}
		else
		{
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
		}

		return client;
	}

	private async Task<HttpStatusCode> GetStatusAsync(string credential, string route, bool apiKey = false)
	{
		using HttpClient client = Client(credential, apiKey);
		using HttpResponseMessage response = await client.GetAsync(route);
		return response.StatusCode;
	}

	private async Task<HttpResponseMessage> DemoteAsync(string actorToken, ApplicationUser target, bool replace)
	{
		using HttpClient client = Client(actorToken);
		return replace
			? await client.PutAsJsonAsync($"/api/users/{target.Id}", new UpdateUserRequest
			{
				Email = "changed@example.com",
				FirstName = "Changed",
				LastName = "Name",
				Role = AppRoles.User,
				IsDisabled = false,
			})
			: await client.DeleteAsync($"/api/users/{target.Id}/roles/Admin");
	}

	private async Task<RoleChangeResult> ChangeAsync(string target, string actor, RoleChangeMode mode, string[] roles, UserProfileUpdate? profile = null)
	{
		await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
		return await scope.ServiceProvider.GetRequiredService<IRoleManagementService>().ChangeAsync(target, actor, mode, roles, profile);
	}

	private async Task AssertUnchangedAsync(ApplicationUser original, string[] roles)
	{
		await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
		UserManager<ApplicationUser> manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
		ApplicationUser stored = (await manager.FindByIdAsync(original.Id))!;
		stored.Email.Should().Be(original.Email);
		stored.UserName.Should().Be(original.UserName);
		stored.FirstName.Should().Be(original.FirstName);
		stored.LastName.Should().Be(original.LastName);
		stored.LockoutEnd.Should().Be(original.LockoutEnd);
		stored.LockoutEnabled.Should().Be(original.LockoutEnabled);
		stored.SecurityStamp.Should().Be(original.SecurityStamp);
		(await manager.GetRolesAsync(stored)).Should().BeEquivalentTo(roles);
	}

	private sealed class ContextFactory(PostgresFixture database) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => database.CreateDbContext();
	}

	private sealed class StoreFailure
	{
		public string? UserId { get; private set; }
		public int AtUpdate { get; private set; }
		public int Updates { get; set; }
		public bool Concurrency { get; private set; }
		public Func<Task>? BeforeUpdate { get; set; }
		public void Arm(string userId, int atUpdate, bool concurrency = false)
		{
			UserId = userId;
			AtUpdate = atUpdate;
			Updates = 0;
			Concurrency = concurrency;
		}
	}

	private sealed class FailingUserStore(ApplicationDbContext context, StoreFailure failure) : UserStore<ApplicationUser>(context)
	{
		public override async Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken = default)
		{
			if (failure.BeforeUpdate is { } beforeUpdate)
			{
				failure.BeforeUpdate = null;
				await beforeUpdate();
			}
			if (user.Id == failure.UserId && ++failure.Updates == failure.AtUpdate)
			{
				return IdentityResult.Failed(new IdentityError
				{
					Code = failure.Concurrency ? nameof(IdentityErrorDescriber.ConcurrencyFailure) : "InjectedFailure",
					Description = "Injected Identity failure",
				});
			}
			return await base.UpdateAsync(user, cancellationToken);
		}
	}
}
