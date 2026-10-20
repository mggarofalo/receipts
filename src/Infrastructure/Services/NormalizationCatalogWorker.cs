using Application.Normalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class NormalizationCatalogWorker(IServiceScopeFactory scopeFactory, ILogger<NormalizationCatalogWorker> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				using var scope = scopeFactory.CreateScope();
				var service = scope.ServiceProvider.GetRequiredService<NormalizationCatalogService>();
				if (await service.ProcessNextAsync(stoppingToken)) { continue; }
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
			catch (Exception exception) { logger.LogError(exception, "Catalog worker could not persist its operation outcome."); }
			try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
		}
	}
}

public sealed partial class NormalizationCatalogService
{
	public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var next = await context.NormalizationCatalogOperations.AsNoTracking()
			.Where(operation => operation.State == "applying" || operation.State == "undoing")
			.OrderBy(operation => operation.CreatedAt).FirstOrDefaultAsync(cancellationToken);
		if (next is null) { return false; }
		string approver = next.MappingId is Guid mappingId
			? await context.NormalizationSourceMappings.Where(mapping => mapping.Id == mappingId).Select(mapping => mapping.ApprovedBy).SingleAsync(cancellationToken)
			: next.Actor;
		try { await ResumeAsync(approver, next.Id, cancellationToken); }
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (Exception exception)
		{
			await using ApplicationDbContext failure = await contextFactory.CreateDbContextAsync(cancellationToken);
			await using var lease = await NormalizationAi.NormalizationAiStoreLock.AcquireAsync(failure, cancellationToken);
			var operation = await failure.NormalizationCatalogOperations.SingleOrDefaultAsync(value => value.Id == next.Id, cancellationToken);
			if (operation is not null && operation.State is "applying" or "undoing")
			{
				operation.State = operation.State == "undoing" ? "undo_paused" : "paused";
				operation.Revision = Guid.NewGuid();
				operation.ErrorCode = exception is NormalizationCatalogException catalog ? catalog.Code : "storage_failure";
				operation.ErrorDetail = exception is NormalizationCatalogException reason ? reason.Message
					: "Historical processing stopped after a storage failure. Committed progress is preserved; resume deliberately.";
				await failure.SaveChangesAsync(cancellationToken);
				await lease.CommitAsync(cancellationToken);
			}
		}
		return true;
	}
}
