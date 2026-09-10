using Application.Interfaces.Services;
using Application.Models.Images;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class ReceiptImageCleanupService(
	IServiceScopeFactory scopeFactory,
	TimeProvider timeProvider,
	ILogger<ReceiptImageCleanupService> logger) : BackgroundService
{
	internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
	internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);
	internal static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromHours(1);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try
		{
			await Task.Delay(InitialDelay, timeProvider, stoppingToken);
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
			return;
		}

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await ProcessCleanupAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Receipt image cleanup cycle failed; it will retry on the next interval");
			}

			try
			{
				await Task.Delay(Interval, timeProvider, stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
		}
	}

	internal async Task<ImageCleanupResult> ProcessCleanupAsync(CancellationToken cancellationToken)
	{
		using IServiceScope scope = scopeFactory.CreateScope();
		IDbContextFactory<ApplicationDbContext> contextFactory =
			scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
		IImageStorageService storage = scope.ServiceProvider.GetRequiredService<IImageStorageService>();

		await using IAsyncDisposable reconciliationLease =
			await ReceiptImageReconciliationLock.AcquireLocalAsync(cancellationToken);
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = context.Database.IsRelational()
			? await context.Database.BeginTransactionAsync(cancellationToken)
			: null;
		await ReceiptImageReconciliationLock.AcquireDatabaseAsync(context, cancellationToken);

		var references = await context.Receipts
			.IgnoreQueryFilters()
			.AsNoTracking()
			.Select(receipt => new { receipt.OriginalImagePath, receipt.ProcessedImagePath })
			.ToListAsync(cancellationToken);
		HashSet<string> referencedPaths = references
			.SelectMany(reference => new[] { reference.OriginalImagePath, reference.ProcessedImagePath })
			.Where(path => path is not null)
			.Select(path => path!)
			.ToHashSet(StringComparer.Ordinal);

		DateTimeOffset createdBefore = timeProvider.GetUtcNow() - OrphanGracePeriod;
		ImageCleanupResult result = await storage.CleanupUnreferencedAsync(
			referencedPaths, createdBefore, cancellationToken);
		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken);
		}
		if (result.DeletedImageSets > 0 || result.DeletedReceiptDirectories > 0)
		{
			logger.LogInformation(
				"Cleaned {ImageSetCount} unreferenced receipt image sets and {ReceiptDirectoryCount} empty receipt directories",
				result.DeletedImageSets,
				result.DeletedReceiptDirectories);
		}
		if (result.FailedEntries > 0)
		{
			logger.LogWarning(
				"Receipt image cleanup skipped {FailedEntryCount} inaccessible or unsafe filesystem entries; it will retry later",
				result.FailedEntries);
		}

		return result;
	}
}
