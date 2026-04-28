namespace VlmEval;

public sealed class VlmEvalOptions
{
	public string FixturesPath { get; set; } = "fixtures/vlm-eval";

	public int OllamaTimeoutSeconds { get; set; } = 180;

	public bool FailOnAnyFixtureFailure { get; set; } = true;

	/// <summary>
	/// Default tolerance applied when comparing money fields (subtotal, total, tax line amounts,
	/// item totalPrice). The check is inclusive: <c>|expected - actual| &lt;= MoneyTolerance</c>
	/// passes. A fixture sidecar may override this via <c>"moneyTolerance": 0.05</c>.
	/// </summary>
	public decimal MoneyTolerance { get; set; } = 0.01m;

	/// <summary>
	/// When set, a structured report of the run is written to this file path. The format is
	/// determined by <see cref="OutputFormat"/>.
	/// </summary>
	public string? ReportPath { get; set; }

	/// <summary>
	/// Format used when writing <see cref="ReportPath"/>. Console output to logs always happens
	/// regardless. <c>Console</c> means the file output is skipped.
	/// </summary>
	public ReportOutputFormat OutputFormat { get; set; } = ReportOutputFormat.Console;

	/// <summary>
	/// VLM provider used for the eval run (RECEIPTS-652). Allowed values: <c>ollama</c> (default,
	/// matches the production default) and <c>anthropic</c> (POC hosted-VLM path). The reporter
	/// stamps this value into the run header and the JSON artifact so two runs (Ollama vs
	/// Anthropic) over the same fixtures can be diffed by an external script. Override via
	/// <c>VlmEval:Provider</c> in config or <c>--provider anthropic</c> on the CLI.
	/// </summary>
	public string Provider { get; set; } = "ollama";
}

public enum ReportOutputFormat
{
	Console,
	Json,
	Markdown,
}
