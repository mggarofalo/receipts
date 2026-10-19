namespace Infrastructure.NormalizationAi;

public sealed class PiNormalizationRunnerOptions
{
	public string HomeDirectory { get; set; } = "/data/chatgpt";
	public string BridgePath { get; set; } = "/app/runner/bridge.mjs";
	public string NodeExecutable { get; set; } = "node";
	public int TimeoutSeconds { get; set; } = 180;
}
