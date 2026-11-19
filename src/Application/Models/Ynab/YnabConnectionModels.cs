namespace Application.Models.Ynab;

/// <summary>How completing an OAuth authorization ended.</summary>
public enum YnabAuthorizationCompletion
{
	Connected,
	/// <summary>The state is unknown, expired, already used, or was started by someone else.</summary>
	AttemptNotValid,
	/// <summary>YNAB refused the code. The attempt cannot be used again.</summary>
	RejectedByYnab,
	/// <summary>YNAB gave no answer. The same attempt can be completed again.</summary>
	YnabUnavailable,
}

/// <summary>What happened when YNAB was asked to revoke the grant during a disconnect.</summary>
public enum YnabRemoteRevocation
{
	/// <summary>YNAB accepted the request. It answers the same for a token it does not know.</summary>
	Succeeded,
	Failed,
	NotAttempted,
}
