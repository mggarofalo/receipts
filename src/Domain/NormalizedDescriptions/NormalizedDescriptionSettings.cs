namespace Domain.NormalizedDescriptions;

public class NormalizedDescriptionSettings
{
	public Guid Id { get; set; }
	public double AutoAcceptThreshold { get; set; }
	public double PendingReviewThreshold { get; set; }
	public DateTimeOffset UpdatedAt { get; set; }

	public const string ThresholdsMustBeInRange = "Thresholds must satisfy 0 <= pendingReviewThreshold < autoAcceptThreshold <= 1";
	public const string PendingMustBeLessThanAuto = "pendingReviewThreshold must be strictly less than autoAcceptThreshold";

	public NormalizedDescriptionSettings(Guid id, double autoAcceptThreshold, double pendingReviewThreshold, DateTimeOffset updatedAt)
	{
		Validate(autoAcceptThreshold, pendingReviewThreshold);

		Id = id;
		AutoAcceptThreshold = autoAcceptThreshold;
		PendingReviewThreshold = pendingReviewThreshold;
		UpdatedAt = updatedAt;
	}

	public static void Validate(double autoAcceptThreshold, double pendingReviewThreshold)
	{
		if (!double.IsFinite(autoAcceptThreshold) || !double.IsFinite(pendingReviewThreshold) || autoAcceptThreshold < 0 || autoAcceptThreshold > 1 || pendingReviewThreshold < 0 || pendingReviewThreshold > 1)
		{
			throw new ArgumentException(ThresholdsMustBeInRange);
		}

		if (pendingReviewThreshold >= autoAcceptThreshold)
		{
			throw new ArgumentException(PendingMustBeLessThanAuto);
		}
	}
}
