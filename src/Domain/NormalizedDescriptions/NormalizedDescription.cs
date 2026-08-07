namespace Domain.NormalizedDescriptions;

public class NormalizedDescription
{
	public Guid Id { get; set; }

	/// <summary>
	/// The observed receipt text this entry matches on. Never edited after creation — the
	/// embedding is anchored to it, and rewriting it would silently change what future ANN
	/// searches match against.
	/// </summary>
	public string CanonicalName { get; set; }

	/// <summary>
	/// Optional human-chosen name shown wherever this entry is displayed (RECEIPTS-876). Null
	/// means "no one has renamed this", and <see cref="DisplayName"/> falls back to
	/// <see cref="CanonicalName"/>.
	/// </summary>
	/// <remarks>
	/// Deliberately separate from <see cref="CanonicalName"/> rather than an in-place edit. A
	/// clean human label like "Milk" may match receipt text markedly worse than the messy
	/// original "MILK 2% GAL", so re-embedding on rename would quietly degrade resolution for
	/// every future receipt. Keeping the two apart makes renaming purely cosmetic and unable to
	/// misroute a match.
	/// </remarks>
	public string? DisplayLabel { get; set; }

	/// <summary>What a user should see: the label if one was chosen, otherwise the matched text.</summary>
	public string DisplayName => DisplayLabel ?? CanonicalName;

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
	public const string DisplayLabelCannotBeWhitespace = "Display label cannot be whitespace";
	public const int DisplayLabelMaxLength = 200;
	public const string DisplayLabelTooLong = "Display label cannot exceed 200 characters";

	public NormalizedDescription(
		Guid id,
		string canonicalName,
		NormalizedDescriptionStatus status,
		DateTimeOffset createdAt,
		Guid? nearestNeighbourId = null,
		double? nearestNeighbourSimilarity = null,
		string? displayLabel = null)
	{
		if (string.IsNullOrWhiteSpace(canonicalName))
		{
			throw new ArgumentException(CanonicalNameCannotBeEmpty, nameof(canonicalName));
		}

		ValidateDisplayLabel(displayLabel);

		Id = id;
		CanonicalName = canonicalName;
		Status = status;
		CreatedAt = createdAt;
		NearestNeighbourId = nearestNeighbourId;
		NearestNeighbourSimilarity = nearestNeighbourSimilarity;
		DisplayLabel = displayLabel;
	}

	/// <summary>
	/// Null clears the label back to the matched text. A whitespace-only string is rejected
	/// rather than silently treated as a clear — an empty text box is more likely a mistake than
	/// an intent, and the two need different answers.
	/// </summary>
	public static void ValidateDisplayLabel(string? displayLabel)
	{
		if (displayLabel is null)
		{
			return;
		}

		if (string.IsNullOrWhiteSpace(displayLabel))
		{
			throw new ArgumentException(DisplayLabelCannotBeWhitespace, nameof(displayLabel));
		}

		if (displayLabel.Length > DisplayLabelMaxLength)
		{
			throw new ArgumentException(DisplayLabelTooLong, nameof(displayLabel));
		}
	}
}
