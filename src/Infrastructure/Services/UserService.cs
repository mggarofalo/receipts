using System.Linq.Expressions;
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
		return await dbContext.Users
			.Where(u => u.RefreshToken == refreshToken)
			.Select(u => u.Id)
			.FirstOrDefaultAsync(cancellationToken);
	}
}
