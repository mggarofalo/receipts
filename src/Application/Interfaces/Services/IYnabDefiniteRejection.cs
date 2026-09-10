namespace Application.Interfaces.Services;

/// <summary>
/// Marks a YNAB failure that proves the remote write was rejected and therefore
/// does not require ambiguous-outcome reconciliation.
/// </summary>
public interface IYnabDefiniteRejection;
