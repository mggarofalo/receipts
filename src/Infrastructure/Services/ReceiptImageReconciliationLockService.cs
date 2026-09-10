using Application.Interfaces.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Services;

public sealed class ReceiptImageReconciliationLockService(
	IDbContextFactory<ApplicationDbContext> contextFactory) : IReceiptImageReconciliationLock
{
	public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
	{
		IAsyncDisposable localLease = await ReceiptImageReconciliationLock.AcquireLocalAsync(cancellationToken);
		ApplicationDbContext? context = null;
		try
		{
			context = await contextFactory.CreateDbContextAsync(cancellationToken);
			IDbContextTransaction? transaction = context.Database.IsNpgsql()
				? await context.Database.BeginTransactionAsync(cancellationToken)
				: null;
			await ReceiptImageReconciliationLock.AcquireDatabaseAsync(context, cancellationToken);
			return new Lease(context, transaction, localLease);
		}
		catch
		{
			try
			{
				if (context is not null)
				{
					await context.DisposeAsync();
				}
			}
			finally
			{
				await localLease.DisposeAsync();
			}
			throw;
		}
	}

	private sealed class Lease(
		ApplicationDbContext context,
		IDbContextTransaction? transaction,
		IAsyncDisposable localLease) : IAsyncDisposable
	{
		public async ValueTask DisposeAsync()
		{
			try
			{
				if (transaction is not null)
				{
					await transaction.DisposeAsync();
				}
			}
			finally
			{
				try
				{
					await context.DisposeAsync();
				}
				finally
				{
					await localLease.DisposeAsync();
				}
			}
		}
	}
}
