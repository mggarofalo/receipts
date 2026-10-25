namespace Application.Classification;

public enum ProviderConnectionState { Disabled, Missing, ConfiguredUnverified, Invalid, Unavailable, Connected }
public sealed record ProviderConnectionStatus(ProviderConnectionState State, string Reason, IReadOnlyList<string> Models);
/// <summary>Administrative connectivity only; no inference capability is implied.</summary>
public interface IClassificationProviderConnection
{
	ProviderConnectionStatus GetStatus();
	Task<ProviderConnectionStatus> CheckAsync(CancellationToken cancellationToken);
}
