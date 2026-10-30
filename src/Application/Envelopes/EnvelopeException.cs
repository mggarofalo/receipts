namespace Application.Envelopes;

/// <summary>A rejected envelope or occasion choice; the status is the HTTP problem status.</summary>
public sealed class EnvelopeException(int status, string message, IDictionary<string, object?>? extensions = null) : Exception(message)
{
	public int Status { get; } = status;
	public IDictionary<string, object?> Extensions { get; } = extensions ?? new Dictionary<string, object?>();
}
