using Application.Models.Ynab;
using Domain.Envelopes;

namespace Application.Interfaces.Services;

public interface IEnvelopeService
{
	Task<List<Envelope>> GetAllAsync(bool? isActive, CancellationToken cancellationToken);
	Task<Envelope?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<Envelope> CreateAsync(Envelope envelope, CancellationToken cancellationToken);
	/// <summary>Returns false when the envelope does not exist.</summary>
	Task<bool> UpdateAsync(Envelope envelope, CancellationToken cancellationToken);
	/// <summary>Soft-deletes an unreferenced envelope; referenced envelopes must be archived instead.</summary>
	Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public interface IOccasionService
{
	Task<List<Occasion>> GetAllAsync(bool? isActive, CancellationToken cancellationToken);
	Task<Occasion?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<Occasion> CreateAsync(Occasion occasion, CancellationToken cancellationToken);
	Task<bool> UpdateAsync(Occasion occasion, CancellationToken cancellationToken);
	Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public sealed record ItemTypeEnvelopeDefault(Guid CategoryId, Guid EnvelopeId, DateTimeOffset UpdatedAt);

public interface IItemTypeEnvelopeDefaultService
{
	Task<List<ItemTypeEnvelopeDefault>> GetAllAsync(CancellationToken cancellationToken);
	/// <summary>Sets the default for a selectable item type. Existing purchases keep their recorded envelope.</summary>
	Task<ItemTypeEnvelopeDefault> SetAsync(Guid categoryId, Guid envelopeId, CancellationToken cancellationToken);
	Task<bool> DeleteAsync(Guid categoryId, CancellationToken cancellationToken);
}

public interface IYnabEnvelopeMappingService
{
	Task<List<YnabEnvelopeMappingDto>> GetByBudgetIdAsync(string ynabBudgetId, CancellationToken cancellationToken);
	Task<YnabEnvelopeMappingDto> SetAsync(Guid envelopeId, string ynabBudgetId, string ynabCategoryId,
		string ynabCategoryName, string ynabCategoryGroupName, CancellationToken cancellationToken);
	Task<bool> DeleteAsync(Guid envelopeId, string ynabBudgetId, CancellationToken cancellationToken);
	/// <summary>Active envelopes, and any envelope live purchases use, with no YNAB category in the budget.</summary>
	Task<List<Envelope>> GetUnmappedEnvelopesAsync(string ynabBudgetId, CancellationToken cancellationToken);
	Task<int> CountStaleMappingsAsync(string currentBudgetId, CancellationToken cancellationToken);
	Task<int> DeleteStaleMappingsAsync(string currentBudgetId, CancellationToken cancellationToken);
}

/// <summary>
/// Explicit "paid from" and occasion choices for one receipt or item. Unlike ordinary edits, where an
/// omitted choice keeps the stored one, null here means automatic (envelope) or none (occasion).
/// </summary>
public interface IBudgetChoiceService
{
	/// <summary>Returns false when the receipt does not exist.</summary>
	Task<bool> SetReceiptChoicesAsync(Guid receiptId, Guid? paidFromEnvelopeId, Guid? occasionId, CancellationToken cancellationToken);
	/// <summary>Returns false when the item does not exist.</summary>
	Task<bool> SetItemChoicesAsync(Guid itemId, Guid? envelopeId, Guid? occasionId, CancellationToken cancellationToken);
}
