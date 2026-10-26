using Infrastructure.Services;

namespace Infrastructure.Jev;

/// <summary>Read-only check of the deployment-local transport identity.</summary>
public interface ICategoryAssistanceConnectionBinding
{
	Task<bool> IsCurrentAsync(CategoryAssistanceConnectionCoordinator.Configuration configuration,
		Guid generation, CancellationToken ct = default);
}
