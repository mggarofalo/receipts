namespace Application.Validation;

public sealed class AdmissionDatePolicy(TimeProvider timeProvider)
{
	public DateOnly Today => DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);

	public bool IsNotFuture(DateOnly date) => date <= Today;
}
