using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.NormalizationAi;

internal sealed class NormalizationAiStoreLock(IDbContextTransaction? transaction) : IAsyncDisposable
{
	private static readonly SemaphoreSlim LocalGate = new(1, 1);
	public static async Task<NormalizationAiStoreLock> AcquireAsync(ApplicationDbContext context, CancellationToken cancellationToken)
	{
		await LocalGate.WaitAsync(cancellationToken);
		IDbContextTransaction? transaction = null;
		try
		{
			transaction = context.Database.IsRelational() ? await context.Database.BeginTransactionAsync(cancellationToken) : null;
			await NormalizationWriteGuard.AcquireWriteGateAsync(context, cancellationToken);
			return new(transaction);
		}
		catch
		{
			if (transaction is not null) { await transaction.DisposeAsync(); }
			LocalGate.Release();
			throw;
		}
	}
	public Task CommitAsync(CancellationToken cancellationToken) => transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;
	public async ValueTask DisposeAsync()
	{
		try { if (transaction is not null) { await transaction.DisposeAsync(); } }
		finally { LocalGate.Release(); }
	}
}
