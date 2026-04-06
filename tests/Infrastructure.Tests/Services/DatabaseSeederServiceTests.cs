using Common;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Infrastructure.Tests.Services;

public class DatabaseSeederServiceTests : IDisposable
{
	private readonly Mock<RoleManager<IdentityRole>> _mockRoleManager;
	private readonly Mock<UserManager<ApplicationUser>> _mockUserManager;
	private readonly IConfiguration _configuration;
	private readonly ServiceProvider _serviceProvider;
	private readonly ApplicationDbContext _dbContext;

	public DatabaseSeederServiceTests()
	{
		Mock<IRoleStore<IdentityRole>> roleStore = new();
		_mockRoleManager = new Mock<RoleManager<IdentityRole>>(
			roleStore.Object, null!, null!, null!, null!);

		Mock<IUserStore<ApplicationUser>> userStore = new();
		_mockUserManager = new Mock<UserManager<ApplicationUser>>(
			userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

		_configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[ConfigurationVariables.AdminSeedEmail] = "admin@test.com",
				[ConfigurationVariables.AdminSeedPassword] = "Password123!",
				[ConfigurationVariables.AdminSeedFirstName] = "Admin",
				[ConfigurationVariables.AdminSeedLastName] = "User",
			})
			.Build();

		ServiceCollection services = new();
		services.AddSingleton(_mockRoleManager.Object);
		services.AddSingleton(_mockUserManager.Object);
		services.AddSingleton(_configuration);
		services.AddSingleton<ILoggerFactory>(new LoggerFactory());

		DbContextOptions<ApplicationDbContext> dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase(Guid.NewGuid().ToString())
			.Options;
		_dbContext = new ApplicationDbContext(dbOptions);
		services.AddScoped(_ => new ApplicationDbContext(dbOptions));

		_serviceProvider = services.BuildServiceProvider();
	}

	public void Dispose()
	{
		_dbContext.Dispose();
		_serviceProvider.Dispose();
		GC.SuppressFinalize(this);
	}

	private void SetupRolesExist()
	{
		_mockRoleManager.Setup(r => r.RoleExistsAsync(It.IsAny<string>()))
			.ReturnsAsync(true);
	}

	private void SetupFullySeedableUser()
	{
		_mockUserManager.Setup(u => u.FindByEmailAsync("admin@test.com"))
			.ReturnsAsync((ApplicationUser?)null);
		_mockUserManager.Setup(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
			.ReturnsAsync(IdentityResult.Success);
		_mockUserManager.Setup(u => u.IsInRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
			.ReturnsAsync(false);
		_mockUserManager.Setup(u => u.AddToRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
			.ReturnsAsync(IdentityResult.Success);
	}

	private async Task MarkSeedAsApplied()
	{
		_dbContext.SeedHistory.Add(new SeedHistoryEntry
		{
			SeedId = "RolesAndAdmin_v1",
			AppliedAt = DateTimeOffset.UtcNow,
		});
		await _dbContext.SaveChangesAsync();
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_WhenAlreadySeeded_SkipsEntireOperation()
	{
		// Arrange
		await MarkSeedAsApplied();

		// Act
		await DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider);

		// Assert — nothing was touched
		_mockRoleManager.Verify(r => r.RoleExistsAsync(It.IsAny<string>()), Times.Never);
		_mockRoleManager.Verify(r => r.CreateAsync(It.IsAny<IdentityRole>()), Times.Never);
		_mockUserManager.Verify(u => u.FindByEmailAsync(It.IsAny<string>()), Times.Never);
		_mockUserManager.Verify(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_WhenSuccessful_RecordsSeedHistory()
	{
		// Arrange
		SetupRolesExist();
		SetupFullySeedableUser();

		// Act
		await DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider);

		// Assert
		bool recorded = await _dbContext.SeedHistory.AnyAsync(s => s.SeedId == "RolesAndAdmin_v1");
		recorded.Should().BeTrue();
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_WhenSuccessful_SecondCallSkips()
	{
		// Arrange
		SetupRolesExist();
		SetupFullySeedableUser();

		// Act — run twice
		await DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider);
		await DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider);

		// Assert — user creation happened only once
		_mockUserManager.Verify(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Once);
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_ThrowsWhenRoleCreationFails()
	{
		// Arrange
		_mockRoleManager.Setup(r => r.RoleExistsAsync(It.IsAny<string>()))
			.ReturnsAsync(false);

		IdentityError error = new() { Code = "DuplicateRoleName", Description = "Role already exists." };
		_mockRoleManager.Setup(r => r.CreateAsync(It.IsAny<IdentityRole>()))
			.ReturnsAsync(IdentityResult.Failed(error));

		// Act
		Func<Task> act = () => DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*Failed to create role*Role already exists.*");
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_WhenRoleCreationFails_DoesNotRecordSeedHistory()
	{
		// Arrange
		_mockRoleManager.Setup(r => r.RoleExistsAsync(It.IsAny<string>()))
			.ReturnsAsync(false);
		_mockRoleManager.Setup(r => r.CreateAsync(It.IsAny<IdentityRole>()))
			.ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "Fail", Description = "Fail" }));

		// Act
		try { await DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider); } catch { }

		// Assert
		bool recorded = await _dbContext.SeedHistory.AnyAsync(s => s.SeedId == "RolesAndAdmin_v1");
		recorded.Should().BeFalse();
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_CreatesAllRolesWhenNoneExist()
	{
		// Arrange
		_mockRoleManager.Setup(r => r.RoleExistsAsync(It.IsAny<string>()))
			.ReturnsAsync(false);
		_mockRoleManager.Setup(r => r.CreateAsync(It.IsAny<IdentityRole>()))
			.ReturnsAsync(IdentityResult.Success);

		ApplicationUser existingUser = new() { Email = "admin@test.com" };
		_mockUserManager.Setup(u => u.FindByEmailAsync("admin@test.com"))
			.ReturnsAsync(existingUser);
		_mockUserManager.Setup(u => u.IsInRoleAsync(existingUser, It.IsAny<string>()))
			.ReturnsAsync(true);

		// Act
		await DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider);

		// Assert
		foreach (string role in AppRoles.All)
		{
			_mockRoleManager.Verify(r => r.CreateAsync(
				It.Is<IdentityRole>(ir => ir.Name == role)), Times.Once);
		}
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_SkipsExistingRoles()
	{
		// Arrange
		SetupRolesExist();

		ApplicationUser existingUser = new() { Email = "admin@test.com" };
		_mockUserManager.Setup(u => u.FindByEmailAsync("admin@test.com"))
			.ReturnsAsync(existingUser);
		_mockUserManager.Setup(u => u.IsInRoleAsync(existingUser, It.IsAny<string>()))
			.ReturnsAsync(true);

		// Act
		await DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider);

		// Assert
		_mockRoleManager.Verify(r => r.CreateAsync(It.IsAny<IdentityRole>()), Times.Never);
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_WhenUserCreationFails_ThrowsInvalidOperationException()
	{
		// Arrange
		SetupRolesExist();

		_mockUserManager.Setup(u => u.FindByEmailAsync("admin@test.com"))
			.ReturnsAsync((ApplicationUser?)null);

		IdentityError error = new() { Code = "DuplicateEmail", Description = "Email already taken" };
		_mockUserManager.Setup(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
			.ReturnsAsync(IdentityResult.Failed(error));

		// Act
		Func<Task> act = () => DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*Failed to create admin user*Email already taken*");
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_WhenAddToAdminRoleFails_ThrowsAndDeletesUser()
	{
		// Arrange
		SetupRolesExist();

		_mockUserManager.Setup(u => u.FindByEmailAsync("admin@test.com"))
			.ReturnsAsync((ApplicationUser?)null);
		_mockUserManager.Setup(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
			.ReturnsAsync(IdentityResult.Success);
		_mockUserManager.Setup(u => u.IsInRoleAsync(It.IsAny<ApplicationUser>(), AppRoles.Admin))
			.ReturnsAsync(false);
		_mockUserManager.Setup(u => u.IsInRoleAsync(It.IsAny<ApplicationUser>(), AppRoles.User))
			.ReturnsAsync(false);
		_mockUserManager.Setup(u => u.AddToRoleAsync(It.IsAny<ApplicationUser>(), AppRoles.Admin))
			.ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "RoleFail", Description = "Admin role assignment failed" }));
		_mockUserManager.Setup(u => u.DeleteAsync(It.IsAny<ApplicationUser>()))
			.ReturnsAsync(IdentityResult.Success);

		// Act
		Func<Task> act = () => DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*Admin role*Admin role assignment failed*");
		_mockUserManager.Verify(u => u.DeleteAsync(It.IsAny<ApplicationUser>()), Times.Once);
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_WhenAddToUserRoleFails_ThrowsAndDeletesUser()
	{
		// Arrange
		SetupRolesExist();

		_mockUserManager.Setup(u => u.FindByEmailAsync("admin@test.com"))
			.ReturnsAsync((ApplicationUser?)null);
		_mockUserManager.Setup(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
			.ReturnsAsync(IdentityResult.Success);
		_mockUserManager.Setup(u => u.IsInRoleAsync(It.IsAny<ApplicationUser>(), AppRoles.Admin))
			.ReturnsAsync(false);
		_mockUserManager.Setup(u => u.IsInRoleAsync(It.IsAny<ApplicationUser>(), AppRoles.User))
			.ReturnsAsync(false);
		_mockUserManager.Setup(u => u.AddToRoleAsync(It.IsAny<ApplicationUser>(), AppRoles.Admin))
			.ReturnsAsync(IdentityResult.Success);
		_mockUserManager.Setup(u => u.AddToRoleAsync(It.IsAny<ApplicationUser>(), AppRoles.User))
			.ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "RoleFail", Description = "User role assignment failed" }));
		_mockUserManager.Setup(u => u.DeleteAsync(It.IsAny<ApplicationUser>()))
			.ReturnsAsync(IdentityResult.Success);

		// Act
		Func<Task> act = () => DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*User role*User role assignment failed*");
		_mockUserManager.Verify(u => u.DeleteAsync(It.IsAny<ApplicationUser>()), Times.Once);
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_WhenBothRoleAssignmentsSucceed_DoesNotThrow()
	{
		// Arrange
		SetupRolesExist();
		SetupFullySeedableUser();

		// Act
		Func<Task> act = () => DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider);

		// Assert
		await act.Should().NotThrowAsync();

		_mockUserManager.Verify(u => u.AddToRoleAsync(It.IsAny<ApplicationUser>(), AppRoles.Admin), Times.Once);
		_mockUserManager.Verify(u => u.AddToRoleAsync(It.IsAny<ApplicationUser>(), AppRoles.User), Times.Once);
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_WhenExistingUserHasAllRoles_SkipsRoleAssignment()
	{
		// Arrange
		SetupRolesExist();

		ApplicationUser existingUser = new() { Email = "admin@test.com" };
		_mockUserManager.Setup(u => u.FindByEmailAsync("admin@test.com"))
			.ReturnsAsync(existingUser);
		_mockUserManager.Setup(u => u.IsInRoleAsync(existingUser, It.IsAny<string>()))
			.ReturnsAsync(true);

		// Act
		await DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider);

		// Assert
		_mockUserManager.Verify(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
		_mockUserManager.Verify(u => u.AddToRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_WhenExistingUserLacksRoles_AssignsRolesWithoutCreating()
	{
		// Arrange — simulates orphaned user from a previous partial failure
		SetupRolesExist();

		ApplicationUser orphanedUser = new() { Email = "admin@test.com" };
		_mockUserManager.Setup(u => u.FindByEmailAsync("admin@test.com"))
			.ReturnsAsync(orphanedUser);
		_mockUserManager.Setup(u => u.IsInRoleAsync(orphanedUser, It.IsAny<string>()))
			.ReturnsAsync(false);
		_mockUserManager.Setup(u => u.AddToRoleAsync(orphanedUser, It.IsAny<string>()))
			.ReturnsAsync(IdentityResult.Success);

		// Act
		await DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider);

		// Assert
		_mockUserManager.Verify(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
		_mockUserManager.Verify(u => u.AddToRoleAsync(orphanedUser, AppRoles.Admin), Times.Once);
		_mockUserManager.Verify(u => u.AddToRoleAsync(orphanedUser, AppRoles.User), Times.Once);
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_WhenRoleAssignmentFailsForOrphanedUser_DoesNotDeleteUser()
	{
		// Arrange — orphaned user from a previous run; role assignment fails again
		SetupRolesExist();

		ApplicationUser orphanedUser = new() { Email = "admin@test.com" };
		_mockUserManager.Setup(u => u.FindByEmailAsync("admin@test.com"))
			.ReturnsAsync(orphanedUser);
		_mockUserManager.Setup(u => u.IsInRoleAsync(orphanedUser, AppRoles.Admin))
			.ReturnsAsync(false);
		_mockUserManager.Setup(u => u.AddToRoleAsync(orphanedUser, AppRoles.Admin))
			.ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "RoleFail", Description = "Admin role assignment failed" }));

		// Act
		Func<Task> act = () => DatabaseSeederService.SeedRolesAndAdminAsync(_serviceProvider);

		// Assert — throws but does NOT delete the pre-existing user
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*Admin role*Admin role assignment failed*");
		_mockUserManager.Verify(u => u.DeleteAsync(It.IsAny<ApplicationUser>()), Times.Never);
	}

	[Fact]
	public async Task SeedRolesAndAdminAsync_WhenAdminConfigMissing_LogsWarningAndDoesNotRecordSeedHistory()
	{
		// Arrange — build a service provider with no admin seed config
		IConfiguration emptyAdminConfig = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>())
			.Build();

		Mock<IRoleStore<IdentityRole>> roleStore = new();
		Mock<RoleManager<IdentityRole>> roleManager = new(roleStore.Object, null!, null!, null!, null!);
		roleManager.Setup(r => r.RoleExistsAsync(It.IsAny<string>())).ReturnsAsync(true);

		Mock<IUserStore<ApplicationUser>> userStore = new();
		Mock<UserManager<ApplicationUser>> userManager = new(userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

		Mock<ILogger> mockLogger = new();
		Mock<ILoggerFactory> mockLoggerFactory = new();
		mockLoggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);

		DbContextOptions<ApplicationDbContext> dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase(Guid.NewGuid().ToString())
			.Options;

		ServiceCollection services = new();
		services.AddSingleton(roleManager.Object);
		services.AddSingleton(userManager.Object);
		services.AddSingleton<IConfiguration>(emptyAdminConfig);
		services.AddSingleton<ILoggerFactory>(mockLoggerFactory.Object);
		services.AddScoped(_ => new ApplicationDbContext(dbOptions));

		using ServiceProvider sp = services.BuildServiceProvider();

		// Act
		await DatabaseSeederService.SeedRolesAndAdminAsync(sp);

		// Assert — warning was logged
		mockLogger.Verify(
			l => l.Log(
				LogLevel.Warning,
				It.IsAny<EventId>(),
				It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("AdminSeed configuration is missing")),
				null,
				It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
			Times.Once);

		// Assert — seed history was NOT recorded (so a retry with config will work)
		await using ApplicationDbContext db = new(dbOptions);
		bool recorded = await db.SeedHistory.AnyAsync(s => s.SeedId == "RolesAndAdmin_v1");
		recorded.Should().BeFalse();

		// Assert — no user operations attempted
		userManager.Verify(u => u.FindByEmailAsync(It.IsAny<string>()), Times.Never);
		userManager.Verify(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
	}
}
