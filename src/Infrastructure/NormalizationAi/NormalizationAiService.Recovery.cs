using Application.NormalizationAi;
using Infrastructure.Entities.NormalizationAi;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.NormalizationAi;

public sealed partial class NormalizationAiService
{
	/// <summary>Retryable awaited admission/startup gate. No OAuth or credential inspection.
	/// A live coordinator retains the operator lease through finalization, so cannot be recovered.</summary>
	public async Task<bool> RecoverConnectionChangeAsync(CancellationToken cancellationToken)
	{
		if (runnerOptions is null) { return false; }
		Guid observed;
		await using (ApplicationDbContext read = await contextFactory.CreateDbContextAsync(cancellationToken))
		{
			var connection = await read.NormalizationAiConnection.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
			if (connection is null || !connection.Changing) { return true; }
			observed = connection.Generation;
		}
		try
		{
			using ChatGptOperatorLease operation = ChatGptOperatorLease.Acquire(runnerOptions.Value);
			using ChatGptRunnerIdleLease idle = ChatGptRunnerIdleLease.Acquire(runnerOptions.Value);
			await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
			await using NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
			NormalizationAiConnectionEntity current = await ConnectionAsync(context, cancellationToken);
			if (!current.Changing) { return true; }
			if (current.Generation != observed) { return false; }
			Binding!.Invalidate();
			current.Changing = false;
			await FenceForRestoreAsync(context, cancellationToken);
			await context.SaveChangesAsync(cancellationToken);
			await lease.CommitAsync(cancellationToken);
			return true;
		}
		catch (ChatGptRunnerException error) when (error.Code is "busy" or "operator_busy") { return false; }
	}
}
