using Application.NormalizationAi;
using Infrastructure.Entities.NormalizationAi;

namespace Infrastructure.NormalizationAi;

public sealed partial class NormalizationAiService
{
	/// <summary>Item-level apply belonged to the retired per-purchase workflow. It stays as an explicit
	/// rejection so saved runs explain themselves; catalog suggestions are approved through catalog preview.</summary>
	public async Task<NormalizationAiRun> ApplyAsync(string actor, Guid id, IReadOnlyList<Guid> proposalIds, Guid expectedRevision, CancellationToken cancellationToken)
	{
		RequireOwner(actor);
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		NormalizationAiRunEntity run = await OwnedRunAsync(context, actor, id, cancellationToken);
		if (!IsPerPurchase(run))
		{ throw Error(409, "catalog_review_required", "Review source-group suggestions through catalog preview and approval."); }
		throw WorkflowRetired();
	}
}
