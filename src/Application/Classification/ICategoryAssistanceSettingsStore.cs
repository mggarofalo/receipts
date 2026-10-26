using Application.Classification;

namespace Application.Classification;

/// <summary>Deployment-local settings, excluded from portable archives. Contains no credentials.</summary>
public interface ICategoryAssistanceSettingsStore
{
	Task<CategoryAssistanceSettings> GetAsync(CancellationToken ct = default);
	Task<CategoryAssistanceSettings> UpdateAsync(string actor, Guid expectedRevision,
		CategoryAssistanceSettings proposed, CancellationToken ct = default);
}
