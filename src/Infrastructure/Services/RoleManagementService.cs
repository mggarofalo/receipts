using Application.Interfaces.Services;
using Application.Models;
using Common;
using Infrastructure.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Infrastructure.Services;

public sealed class RoleManagementService(
	ApplicationDbContext context,
	UserManager<ApplicationUser> userManager) : IRoleManagementService
{
	public async Task<RoleChangeResult> ChangeAsync(
		string userId,
		string actorId,
		RoleChangeMode mode,
		IReadOnlyCollection<string> roles,
		UserProfileUpdate? profile = null,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(actorId))
		{
			return RoleChangeResult.Invalid("An authenticated actor is required to change roles.");
		}

		if (!Enum.IsDefined(mode) || roles.Any(role => !AppRoles.All.Contains(role)))
		{
			return RoleChangeResult.Invalid($"Valid roles: {string.Join(", ", AppRoles.All)}");
		}

		if (profile is not null && mode != RoleChangeMode.Replace)
		{
			return RoleChangeResult.Invalid("Profile updates require replacement roles.");
		}

		await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		bool committed = false;
		try
		{
			// One stable coordinator for all membership decisions. Locking only the
			// target user would allow two administrators to demote each other.
			string normalizedAdmin = userManager.NormalizeName(AppRoles.Admin)
				?? throw new InvalidOperationException("The Admin role name could not be normalized.");
			IdentityRole? adminRole = await context.Roles
				.FromSqlInterpolated($"SELECT * FROM identity.\"AspNetRoles\" WHERE \"NormalizedName\" = {normalizedAdmin} FOR UPDATE")
				.AsNoTracking()
				.SingleOrDefaultAsync(cancellationToken);
			if (adminRole is null)
			{
				throw new InvalidOperationException("The Admin role must be provisioned before managing user roles.");
			}

			// Authentication may already have tracked this user. Refresh it after
			// acquiring the lock so every decision observes the preceding commit.
			ApplicationUser? user = await userManager.FindByIdAsync(userId);
			if (user is null)
			{
				return RoleChangeResult.NotFound;
			}

			await context.Entry(user).ReloadAsync(cancellationToken);
			if (context.Entry(user).State == EntityState.Detached)
			{
				return RoleChangeResult.NotFound;
			}

			HashSet<string> currentRoles = new(await userManager.GetRolesAsync(user), StringComparer.Ordinal);
			HashSet<string> desiredRoles = mode switch
			{
				RoleChangeMode.Add => new(currentRoles.Concat(roles), StringComparer.Ordinal),
				RoleChangeMode.Remove => new(currentRoles.Except(roles), StringComparer.Ordinal),
				_ => new(roles, StringComparer.Ordinal),
			};
			bool removesAdmin = currentRoles.Contains(AppRoles.Admin) && !desiredRoles.Contains(AppRoles.Admin);
			if (userId == actorId)
			{
				if (profile?.IsDisabled == true)
				{
					return RoleChangeResult.Invalid("Cannot disable your own account.");
				}

				if (removesAdmin)
				{
					return RoleChangeResult.Invalid("Cannot remove your own Admin role.");
				}
			}

			if (removesAdmin && await context.UserRoles.CountAsync(r => r.RoleId == adminRole.Id, cancellationToken) <= 1)
			{
				return RoleChangeResult.Invalid("Cannot remove the last Admin role.");
			}

			if (profile is not null)
			{
				user.Email = profile.Email;
				user.UserName = profile.Email;
				user.FirstName = profile.FirstName;
				user.LastName = profile.LastName;
				// Preserve failed-login lockout support when re-enabling an account.
				user.LockoutEnabled = true;
				user.LockoutEnd = profile.IsDisabled ? DateTimeOffset.MaxValue : null;
				IdentityResult update = await userManager.UpdateAsync(user);
				if (!update.Succeeded)
				{
					return FromIdentity(update);
				}
			}

			string[] removed = currentRoles.Except(desiredRoles).ToArray();
			string[] added = desiredRoles.Except(currentRoles).ToArray();
			if (removed.Length > 0)
			{
				IdentityResult removal = await userManager.RemoveFromRolesAsync(user, removed);
				if (!removal.Succeeded)
				{
					return FromIdentity(removal);
				}
			}
			if (added.Length > 0)
			{
				IdentityResult addition = await userManager.AddToRolesAsync(user, added);
				if (!addition.Succeeded)
				{
					return FromIdentity(addition);
				}
			}
			if (removed.Length > 0 || added.Length > 0 || profile?.IsDisabled == true)
			{
				IdentityResult stamp = await userManager.UpdateSecurityStampAsync(user);
				if (!stamp.Succeeded)
				{
					return FromIdentity(stamp);
				}
			}

			await transaction.CommitAsync(cancellationToken);
			committed = true;
			return RoleChangeResult.Success;
		}
		catch (DbUpdateConcurrencyException)
		{
			return RoleChangeResult.Conflict("The user changed concurrently. Reload and retry.");
		}
		catch (Exception exception) when (IsDatabaseConflict(exception))
		{
			return RoleChangeResult.Conflict("User roles changed concurrently. Reload and retry.");
		}
		finally
		{
			if (!committed)
			{
				// Disposal rolls the transaction back. Do not leave rolled-back
				// Identity values available for a later save in this request scope.
				foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in context.ChangeTracker.Entries().ToList())
				{
					if (entry.Entity is ApplicationUser user && user.Id == userId ||
						entry.Entity is IdentityUserRole<string> membership && membership.UserId == userId)
					{
						entry.State = EntityState.Detached;
					}
				}
			}
		}
	}

	private static RoleChangeResult FromIdentity(IdentityResult result)
	{
		string[] errors = result.Errors.Select(error => error.Description).ToArray();
		return result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure))
			? RoleChangeResult.Conflict(errors)
			: RoleChangeResult.Invalid(errors);
	}

	private static bool IsDatabaseConflict(Exception exception) =>
		exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } ||
		exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } };
}
