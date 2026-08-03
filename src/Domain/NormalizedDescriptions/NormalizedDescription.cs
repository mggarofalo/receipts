namespace Domain.NormalizedDescriptions;

public class NormalizedDescription
{
	public Guid Id { get; set; }
	public string CanonicalName { get; set; }
	public NormalizedDescriptionStatus Status { get; set; }
	public DateTimeOffset CreatedAt { get; set; }

	// The near-miss that caused this entry to land in PendingReview (RECEIPTS-873). Written only
	// by the PendingReview branch of NormalizedDescriptionService.GetOrCreateAsync: every other
	// branch leaves both null, because there is genuinely nothing to compare against. Reviewers
	// must be able to tell "scored 0.00 against a neighbour" apart from "no comparison recorded",
	// so the absence is modelled as null rather than a zero default.
	public Guid? NearestNeighbourId { get; set; }
	public double? NearestNeighbourSimilarity { get; set; }

	public const string CanonicalNameCannotBeEmpty = "Canonical name cannot be empty";

	public NormalizedDescription(
		Guid id,
		string canonicalName,
		NormalizedDescriptionStatus status,
		DateTimeOffset createdAt,
		Guid? nearestNeighbourId = null,
		double? nearestNeighbourSimilarity = null)
	{
		if (string.IsNullOrWhiteSpace(canonicalName))
		{
			throw new ArgumentException(CanonicalNameCannotBeEmpty, nameof(canonicalName));
		}

		Id = id;
		CanonicalName = canonicalName;
		Status = status;
		CreatedAt = createdAt;
		NearestNeighbourId = nearestNeighbourId;
		NearestNeighbourSimilarity = nearestNeighbourSimilarity;
	}
}
