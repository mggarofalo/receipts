using System.Text.RegularExpressions;

namespace Application.Normalization;

/// <summary>Conservative evidence for suggestions shown during receipt entry.</summary>
public static class ReceiptSuggestionEvidence
{
	/// <summary>
	/// Cosine similarity at or above which a nearest semantic neighbour is offered without other evidence
	/// (RECEIPTS-1095). Chosen from the RECEIPTS-1089 recall measurement
	/// (docs/normalization-evaluation/catalog-recall.md): on real catalog text the nearest wrong name
	/// reached 0.80 for 0.3% of sources (3.4% at 0.75); on the synthetic fixture 0.80 admitted as many
	/// missed targets as 0.75 with fewer than half the wrong admissions. Re-check it with that harness.
	/// </summary>
	public const double SemanticFloor = 0.80;

	private static readonly HashSet<string> NonProductWords = new(StringComparer.OrdinalIgnoreCase)
		{
				"oz", "lb", "lbs", "kg", "g", "ml", "l", "ct", "pc", "pcs", "in", "cm", "mm", "fl",
				"ounce", "ounces", "gram", "grams", "kilogram", "kilograms", "pound", "pounds",
				"inch", "inches", "liter", "liters", "litre", "litres", "gallon", "gallons", "gal",
				"pack", "packs", "count", "each", "piece", "pieces", "can", "cans",
				"bottle", "bottles", "bag", "bags", "box", "boxes", "jar", "jars",
				"whole", "organic", "baked", "fresh", "natural", "original", "classic", "large", "small", "medium",
				"for", "and", "the", "with", "without", "of", "to", "or"
		};

	public static bool IsUseful(NormalizationSourceKey source, NormalizationCatalogCandidate candidate)
	{
		if (candidate.Channels.Contains("exact") || candidate.Channels.Contains("approved_example"))
		{
			return true;
		}

		if (candidate.SemanticSimilarity >= SemanticFloor)
		{
			return true;
		}

		if (!candidate.Channels.Contains("lexical"))
		{
			return false;
		}

		HashSet<string> sourceWords = Words(source.Description);
		return HasProductOverlap(sourceWords, Words(candidate.CanonicalName))
				|| (candidate.DisplayLabel is not null && HasProductOverlap(sourceWords, Words(candidate.DisplayLabel)));
	}

	private static bool HasProductOverlap(HashSet<string> source, HashSet<string> target)
	{
		int shared = source.Count(target.Contains);
		// One complete product word can match a generic one-word name.
		// For two multiword names, a single shared modifier or brand is
		// too weak to present as a product suggestion.
		return shared >= 2 || (shared == 1 && target.Count == 1);
	}

	/// <summary>History is a text completion, not an assertion of product identity.</summary>
	public static bool IsDescriptionCompletion(string source, string candidate)
	{
		if (string.Equals(source.Trim(), candidate.Trim(), StringComparison.OrdinalIgnoreCase)) { return true; }
		HashSet<string> sourceWords = Words(source, 2);
		HashSet<string> targetWords = Words(candidate, 2);
		int matched = targetWords.Count(word => sourceWords.Any(prefix => word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
		return matched >= 2 || (matched == 1 && (sourceWords.Count == 1 || targetWords.Count == 1));
	}

	public static IEnumerable<string> LexicalTerms(string text) => Terms(text, 3);

	private static HashSet<string> Words(string text, int minimumLength = 3) => Terms(text, minimumLength)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

	private static IEnumerable<string> Terms(string text, int minimumLength) => Regex.Matches(text, @"\p{L}+", RegexOptions.CultureInvariant)
			.Select(match => match.Value)
			.Where(word => word.Length >= minimumLength && !NonProductWords.Contains(word));
}
