using System.Text.RegularExpressions;

namespace Application.Normalization;

/// <summary>How a description splits into the terms retrieval probes (RECEIPTS-1100).</summary>
public enum DescriptionTokenMode
{
	/// <summary>Receipt entry: product words only, so "KS ORG CHKN BRST 2/PK" probes org, chkn and brst.</summary>
	Receipt,

	/// <summary>Catalog search: every space, tab, hyphen or slash separated token of two or more characters.</summary>
	Catalog,
}

/// <summary>
/// The one text normaliser for description retrieval (RECEIPTS-1100): candidate retrieval, the receipt
/// chooser and its fallback search, and autocomplete's completion filter all tokenise and lower-case here.
/// The source key keeps its own trim (<see cref="NormalizationCatalogPolicy.Normalize"/>): it must match
/// PostgreSQL btrim and so the stored hashes.
/// </summary>
public static class DescriptionText
{
	/// <summary>The most terms one description probes.</summary>
	public const int MaximumTokens = 8;

	private static readonly char[] CatalogSeparators = [' ', '\t', '-', '/'];

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

	/// <summary>
	/// Culture-invariant lower case. It agrees with PostgreSQL lower() for non-ASCII letters under the
	/// UTF-8 locale the deployed database uses, so terms lowered here compare with lowered columns.
	/// </summary>
	public static string Lower(string text) => text.ToLowerInvariant();

	/// <summary>Equal once outer whitespace of any kind is ignored, without regard to case.</summary>
	public static bool SameText(string left, string right)
		=> string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Runs of letters at least <paramref name="minimumLength"/> long, other than units, packaging and
	/// generic modifiers. Digits and punctuation separate words; case is kept.
	/// </summary>
	public static IEnumerable<string> ProductWords(string text, int minimumLength = 3)
		=> Regex.Matches(text, @"\p{L}+", RegexOptions.CultureInvariant)
			.Select(match => match.Value)
			.Where(word => word.Length >= minimumLength && !NonProductWords.Contains(word));

	/// <summary>The distinct lower-case terms the lexical channel probes, at most <see cref="MaximumTokens"/>.</summary>
	public static string[] LexicalTokens(string text, DescriptionTokenMode mode)
	{
		IEnumerable<string> terms = mode == DescriptionTokenMode.Receipt
			? ProductWords(text)
			: text.Split(CatalogSeparators, StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length >= 2);
		return terms.Select(Lower).Distinct().Take(MaximumTokens).ToArray();
	}
}
