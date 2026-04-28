namespace VlmEval;

public sealed record FixtureResult(
	string FixtureName,
	bool Passed,
	TimeSpan Elapsed,
	IReadOnlyList<FieldDiff> FieldDiffs,
	string? Error);

public enum DiffStatus
{
	Pass,
	Fail,
	NotDeclared,
}

public sealed record FieldDiff(
	string Field,
	DiffStatus Status,
	string? Expected,
	string? Actual,
	string? Detail);

/// <summary>
/// Per-run metadata embedded in structured reports. Captured at run start so the report
/// reflects the configured environment (Ollama URL, fixtures path, provider) at the time
/// of the run. <see cref="Provider"/> was added in RECEIPTS-652 so external diff scripts
/// can distinguish Ollama and Anthropic runs over the same fixtures.
/// </summary>
public sealed record RunInfo(
	DateTimeOffset StartedAt,
	string Provider,
	string ProviderEndpoint,
	string FixturesPath)
{
	/// <summary>
	/// Backwards-compatible alias for <see cref="ProviderEndpoint"/>, kept so any external
	/// tooling still parsing the legacy <c>OllamaUrl</c> field name keeps working when the
	/// provider is Ollama. For the Anthropic provider this carries the API base URL
	/// (e.g. <c>https://api.anthropic.com</c>).
	/// </summary>
	public string OllamaUrl => ProviderEndpoint;
}
