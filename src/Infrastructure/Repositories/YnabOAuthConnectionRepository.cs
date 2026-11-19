using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class YnabOAuthConnectionRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : IYnabOAuthConnectionRepository
{
	// One row for the whole deployment.
	public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");

	public async Task<YnabOAuthConnectionEntity?> GetAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabOAuthConnections.AsNoTracking()
			.SingleOrDefaultAsync(e => e.Id == SingletonId, cancellationToken);
	}

	public async Task<YnabOAuthConnectionEntity> SaveConnectedAsync(
		string protectedTokens, DateTimeOffset accessTokenExpiresAtUtc, string? connectedByUserId, DateTimeOffset now, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabOAuthConnectionEntity? entity = await context.YnabOAuthConnections.FindAsync([SingletonId], cancellationToken);
		if (entity is null)
		{
			entity = new YnabOAuthConnectionEntity { Id = SingletonId };
			context.YnabOAuthConnections.Add(entity);
		}

		entity.Status = YnabOAuthConnectionStatus.Connected;
		entity.ProtectedTokens = protectedTokens;
		entity.AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc;
		entity.ConnectionId = Guid.NewGuid();
		entity.ConnectedAtUtc = now;
		entity.ConnectedByUserId = connectedByUserId;
		entity.RefreshedAtUtc = null;
		entity.StatusChangedAtUtc = now;
		entity.ConcurrencyStamp = Guid.NewGuid();

		await context.SaveChangesAsync(cancellationToken);
		return entity;
	}

	public async Task<bool> TryReplaceTokensAsync(
		Guid expectedStamp, string protectedTokens, DateTimeOffset accessTokenExpiresAtUtc, DateTimeOffset now, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabOAuthConnectionEntity? entity = await context.YnabOAuthConnections.FindAsync([SingletonId], cancellationToken);
		if (entity is null
			|| entity.Status != YnabOAuthConnectionStatus.Connected
			|| entity.ConcurrencyStamp != expectedStamp)
		{
			return false;
		}

		entity.ProtectedTokens = protectedTokens;
		entity.AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc;
		entity.RefreshedAtUtc = now;
		entity.ConcurrencyStamp = Guid.NewGuid();
		return await TrySaveAsync(context, cancellationToken);
	}

	public async Task<bool> MarkReconnectRequiredAsync(Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabOAuthConnectionEntity? entity = await context.YnabOAuthConnections.FindAsync([SingletonId], cancellationToken);
		if (entity is null
			|| entity.ConnectionId != connectionId
			|| entity.Status == YnabOAuthConnectionStatus.ReconnectRequired)
		{
			return false;
		}

		entity.Status = YnabOAuthConnectionStatus.ReconnectRequired;
		entity.ProtectedTokens = null;
		entity.AccessTokenExpiresAtUtc = null;
		entity.StatusChangedAtUtc = now;
		entity.ConcurrencyStamp = Guid.NewGuid();
		return await TrySaveAsync(context, cancellationToken);
	}

	public async Task<bool> DeleteAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabOAuthConnectionEntity? entity = await context.YnabOAuthConnections.FindAsync([SingletonId], cancellationToken);
		if (entity is null)
		{
			return false;
		}

		context.YnabOAuthConnections.Remove(entity);
		try
		{
			await context.SaveChangesAsync(cancellationToken);
		}
		catch (DbUpdateConcurrencyException)
		{
			// Changed by a concurrent writer after it was read. Whatever it wrote must go too.
			await context.YnabOAuthConnections.Where(e => e.Id == SingletonId).ExecuteDeleteAsync(cancellationToken);
		}

		return true;
	}

	private static async Task<bool> TrySaveAsync(ApplicationDbContext context, CancellationToken cancellationToken)
	{
		try
		{
			await context.SaveChangesAsync(cancellationToken);
			return true;
		}
		catch (DbUpdateConcurrencyException)
		{
			return false;
		}
	}
}
