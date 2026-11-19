using Application.Models.Ynab;

namespace Application.Interfaces.Services;

/// <summary>The single place that decides which credential YNAB requests use.</summary>
public interface IYnabCredentialSource
{
	/// <summary>Reports the credential in use without contacting YNAB.</summary>
	ValueTask<YnabCredentialState> GetStateAsync(CancellationToken cancellationToken);

	/// <summary>Returns a bearer credential ready to send, or null when none is usable.</summary>
	ValueTask<YnabBearerCredential?> GetCredentialAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Called when YNAB answered 401 to a request sent with <paramref name="rejected"/>. Returns a
	/// renewed credential to retry with once, or null when this kind of credential cannot be renewed.
	/// </summary>
	ValueTask<YnabBearerCredential?> RenewAfterRejectionAsync(YnabBearerCredential rejected, CancellationToken cancellationToken);
}
