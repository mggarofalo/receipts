using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Application.Interfaces.Services;
using Application.Models;
using Infrastructure.Entities;
using Infrastructure.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class UserService(ApplicationDbContext dbContext) : IUserService
{
	private static readonly Dictionary<string, Expression<Func<ApplicationUser, object>>> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
	{
		["email"] = u => u.Email!,
		["firstName"] = u => u.FirstName!,
		["lastName"] = u => u.LastName!,
		["createdAt"] = u => u.CreatedAt,
	};

	public async Task<PagedResult<UserSummary>> ListUsersAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken = default)
	{
		offset = Math.Max(0, offset);
		limit = Math.Clamp(limit, 1, 100);

		int totalCount = await dbContext.Users.CountAsync(cancellationToken);

		List<ApplicationUser> users = await dbContext.Users
			.ApplySort(sort, AllowedSortColumns, u => u.Email!, u => u.Id)
			.Skip(offset)
			.Take(limit)
			.ToListAsync(cancellationToken);

		List<string> userIds = users.Select(u => u.Id).ToList();

		Dictionary<string, List<string>> rolesByUserId = await dbContext.UserRoles
			.Where(ur => userIds.Contains(ur.UserId))
			.Join(
				dbContext.Roles,
				ur => ur.RoleId,
				r => r.Id,
				(ur, r) => new { ur.UserId, RoleName = r.Name! })
			.GroupBy(x => x.UserId)
			.ToDictionaryAsync(
				g => g.Key,
				g => g.Select(x => x.RoleName).ToList(),
				cancellationToken);

		List<UserSummary> items = users.Select(user => new UserSummary(
			user.Id,
			user.Email ?? "",
			user.FirstName,
			user.LastName,
			rolesByUserId.GetValueOrDefault(user.Id, []),
			user.LockoutEnabled && user.LockoutEnd > DateTimeOffset.UtcNow,
			user.CreatedAt,
			user.LastLoginAt
		)).ToList();

		return new PagedResult<UserSummary>(items, totalCount, offset, limit);
	}

	public async Task<string?> FindUserIdByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrEmpty(refreshToken))
		{
			return null;
		}

		// Refresh tokens are stored hashed, so hash the incoming plaintext and compare hash-to-hash.
		string tokenHash = HashRefreshToken(refreshToken);
		return await dbContext.Users
			.Where(u => u.RefreshToken == tokenHash)
			.Select(u => u.Id)
			.FirstOrDefaultAsync(cancellationToken);
	}

	public async Task RevokeRefreshSessionAsync(string userId, Guid? refreshSessionId, CancellationToken cancellationToken = default)
	{
		// ExecuteUpdate bypasses Identity's change tracker. Changing its concurrency stamp atomically
		// prevents an already-loaded refresh request from restoring the revoked token with UpdateAsync.
		string concurrencyStamp = Guid.NewGuid().ToString();
		await dbContext.Users
			.Where(u => u.Id == userId && u.RefreshSessionId == refreshSessionId && u.RefreshToken != null)
			.ExecuteUpdateAsync(setters => setters
				.SetProperty(u => u.RefreshToken, (string?)null)
				.SetProperty(u => u.RefreshTokenExpiresAt, (DateTimeOffset?)null)
				.SetProperty(u => u.ConcurrencyStamp, concurrencyStamp), cancellationToken);
	}

	public string HashRefreshToken(string refreshToken)
	{
		byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));
		return Convert.ToHexString(bytes).ToLowerInvariant();
	}
}
