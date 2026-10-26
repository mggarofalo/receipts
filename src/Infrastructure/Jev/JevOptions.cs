namespace Infrastructure.Jev;

public sealed class JevOptions
{
	public bool Enabled { get; set; } = true;
	// Administrative connectivity and a configured key never opt in to inference.
	public bool InferenceEnabled { get; set; }
	public string? ApiKey { get; set; }
}
