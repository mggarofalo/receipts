using Application.Models.Ynab;

namespace Application.Interfaces.Services;

/// <summary>Connects and disconnects the deployment's single YNAB OAuth connection.</summary>
public interface IYnabConnectionService
{
	/// <summary>
	/// Starts a single-use authorization attempt for <paramref name="userId"/> and returns the YNAB
	/// address the browser must visit, or null when OAuth is not configured on this deployment.
	/// </summary>
	Task<string?> StartAuthorizationAsync(string userId, CancellationToken cancellationToken);

	Task<YnabAuthorizationCompletion> CompleteAuthorizationAsync(string userId, string code, string state, CancellationToken cancellationToken);

	/// <summary>Asks YNAB to revoke the grant, then removes the stored tokens whatever YNAB answered.</summary>
	Task<YnabRemoteRevocation> DisconnectAsync(CancellationToken cancellationToken);
}
