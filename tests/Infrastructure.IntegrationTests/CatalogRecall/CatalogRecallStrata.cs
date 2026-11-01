using System.Text.RegularExpressions;
using Application.Normalization;
using Infrastructure.Services;

namespace Infrastructure.IntegrationTests.CatalogRecall;

/// <summary>RECEIPTS-1089 strata. Each is reported on its own so one easy group cannot hide another.</summary>
public static partial class CatalogRecallStrata
{
	public const string Readable = "readable";
	public const string Abbreviated = "abbreviated";
	public const string NovelDescription = "novel_description";
	public const string SameDescriptionOtherStore = "same_description_other_store";
	public const string CrowdedToken = "crowded_token";
	public const string ExactName = "exact_name";
	public const string NotExactName = "not_exact_name";

	/// <summary>
	/// RECEIPTS-1089's "20+ names sharing a token": one catalog lexical query returns at most 20 names, so at
	/// this count the query is saturated and any further name can push the target out. Measured with the
	/// catalog-mode tokens; the receipt chooser scans further, so read this stratum mainly in catalog mode.
	/// </summary>
	public const int CrowdedTokenThreshold = 20;

	public static readonly string[] All =
		[Readable, Abbreviated, NovelDescription, SameDescriptionOtherStore, CrowdedToken, ExactName, NotExactName];

	/// <summary>
	/// Readable when every product word (3+ letters, <see cref="ReceiptSuggestionEvidence"/> non-product
	/// words removed) appears as a whole word in the target's display name, ignoring case. A description
	/// with no product word at all is abbreviated: nothing in it can be read.
	/// </summary>
	public static bool IsReadable(string description, string targetDisplayName)
	{
		string[] words = DescriptionText.ProductWords(description).ToArray();
		if (words.Length == 0) { return false; }
		HashSet<string> name = Letters().Matches(targetDisplayName).Select(match => match.Value)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		return words.All(name.Contains);
	}

	/// <summary>The exact channel's own rule, so this stratum is precisely what that channel solves.</summary>
	public static bool IsExactName(string description, string canonicalName, string? displayLabel)
	{
		string text = DescriptionText.Lower(description);
		return DescriptionText.Lower(canonicalName) == text || DescriptionText.Lower(displayLabel ?? canonicalName) == text;
	}

	/// <summary>Some catalog-mode lexical token matches at least <see cref="CrowdedTokenThreshold"/> active names.</summary>
	public static bool HasCrowdedToken(string description, IReadOnlyCollection<(string CanonicalName, string? DisplayLabel)> catalog)
		=> DescriptionText.LexicalTokens(description, DescriptionTokenMode.Catalog).Any(token => catalog.Count(name =>
			name.CanonicalName.ToLowerInvariant().Contains(token)
			|| (name.DisplayLabel ?? name.CanonicalName).ToLowerInvariant().Contains(token)) >= CrowdedTokenThreshold);

	/// <param name="sharesDescription">Another evaluated source (another store or code) has the same description.</param>
	public static IReadOnlyList<string> Classify(CatalogRecallCase recallCase, bool sharesDescription, bool crowded)
	{
		string description = recallCase.Source.Description;
		List<string> strata =
		[
			IsReadable(description, recallCase.TargetDisplayName) ? Readable : Abbreviated,
			sharesDescription ? SameDescriptionOtherStore : NovelDescription,
			IsExactName(description, recallCase.TargetCanonicalName, recallCase.TargetDisplayLabel) ? ExactName : NotExactName,
		];
		if (crowded) { strata.Add(CrowdedToken); }
		return strata;
	}

	[GeneratedRegex(@"\p{L}+", RegexOptions.CultureInvariant)]
	private static partial Regex Letters();
}
