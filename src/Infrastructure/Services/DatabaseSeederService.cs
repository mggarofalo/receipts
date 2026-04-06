using Common;
using Infrastructure.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public static class DatabaseSeederService
{
	private const string RolesAndAdminSeedId = "RolesAndAdmin_v1";

	public static async Task SeedRolesAndAdminAsync(IServiceProvider services)
	{
		using IServiceScope scope = services.CreateScope();
		ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
		ILogger logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DatabaseSeederService));

		if (await dbContext.SeedHistory.AnyAsync(s => s.SeedId == RolesAndAdminSeedId))
		{
			logger.LogInformation("Seed operation '{SeedId}' already applied — skipping.", RolesAndAdminSeedId);
			return;
		}

		RoleManager<IdentityRole> roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
		UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
		IConfiguration configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

		foreach (string role in AppRoles.All)
		{
			if (!await roleManager.RoleExistsAsync(role))
			{
				IdentityResult roleResult = await roleManager.CreateAsync(new IdentityRole(role));
				if (!roleResult.Succeeded)
				{
					throw new InvalidOperationException(
						$"Failed to create role '{role}': {string.Join(", ", roleResult.Errors.Select(e => e.Description))}");
				}
			}
		}

		string? adminEmail = configuration[ConfigurationVariables.AdminSeedEmail];
		string? adminPassword = configuration[ConfigurationVariables.AdminSeedPassword];

		if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
		{
			logger.LogWarning(
				"AdminSeed configuration is missing (AdminSeed:Email and/or AdminSeed:Password not set). " +
				"Roles were seeded but no admin user was created. " +
				"Set AdminSeed__Email and AdminSeed__Password environment variables to seed an admin user.");
			return;
		}

		// Look up by email (not role membership) to detect orphaned users from partial failures
		ApplicationUser? adminUser = await userManager.FindByEmailAsync(adminEmail);
		bool userCreatedHere = false;

		if (adminUser == null)
		{
			adminUser = new()
			{
				UserName = adminEmail,
				Email = adminEmail,
				FirstName = configuration[ConfigurationVariables.AdminSeedFirstName],
				LastName = configuration[ConfigurationVariables.AdminSeedLastName],
				MustResetPassword = true,
				CreatedAt = DateTimeOffset.UtcNow,
			};

			IdentityResult result = await userManager.CreateAsync(adminUser, adminPassword);
			if (!result.Succeeded)
			{
				throw new InvalidOperationException(
					$"Failed to create admin user: {string.Join(", ", result.Errors.Select(e => e.Description))}");
			}

			userCreatedHere = true;
		}

		try
		{
			if (!await userManager.IsInRoleAsync(adminUser, AppRoles.Admin))
			{
				IdentityResult addAdmin = await userManager.AddToRoleAsync(adminUser, AppRoles.Admin);
				if (!addAdmin.Succeeded)
				{
					throw new InvalidOperationException(
						$"Failed to assign Admin role: {string.Join(", ", addAdmin.Errors.Select(e => e.Description))}");
				}
			}

			if (!await userManager.IsInRoleAsync(adminUser, AppRoles.User))
			{
				IdentityResult addUser = await userManager.AddToRoleAsync(adminUser, AppRoles.User);
				if (!addUser.Succeeded)
				{
					throw new InvalidOperationException(
						$"Failed to assign User role: {string.Join(", ", addUser.Errors.Select(e => e.Description))}");
				}
			}
		}
		catch when (userCreatedHere)
		{
			// Delete orphaned user so the next retry can succeed.
			// Swallow delete failures to preserve the original exception.
			try
			{
				await userManager.DeleteAsync(adminUser);
			}
			catch
			{
				// Intentionally swallowed — the original role-assignment exception is more important.
			}

			throw;
		}

		dbContext.SeedHistory.Add(new SeedHistoryEntry
		{
			SeedId = RolesAndAdminSeedId,
			AppliedAt = DateTimeOffset.UtcNow,
		});
		await dbContext.SaveChangesAsync();

		logger.LogInformation("Seed operation '{SeedId}' completed and recorded.", RolesAndAdminSeedId);
	}
}
