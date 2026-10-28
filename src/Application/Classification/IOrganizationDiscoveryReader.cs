namespace Application.Classification;

/// <summary>Local discovery evidence only; observed purchases are not authorized effects.</summary>
public sealed record OrganizationDiscoveryProgress(bool Enabled, string State, long SourceGroups,
	long ObservedItems, long ProtectedItems, long UnavailableItems, long InvalidSourceItems,
	DateTimeOffset? UpdatedAt);

public interface IOrganizationDiscoveryReader
{
	Task<OrganizationDiscoveryProgress> GetProgressAsync(CancellationToken ct = default);
}
