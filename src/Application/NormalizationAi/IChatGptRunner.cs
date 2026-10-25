using System.Text.Json;

namespace Application.NormalizationAi;

public interface IChatGptRunner
{
	Task<ChatGptRunnerStatus> GetStatusAsync(CancellationToken cancellationToken);
	Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken);
	Task<ChatGptInferenceResult> InferAsync(ChatGptInferenceRequest request, CancellationToken cancellationToken);
	Task LoginAsync(Action<ChatGptDeviceCode> onDeviceCode, CancellationToken cancellationToken);
	Task LogoutAsync(CancellationToken cancellationToken);
}

public sealed record ChatGptRunnerStatus(bool CredentialsPresent, bool Busy = false);
public sealed record ChatGptDeviceCode(string Url, string Code);
public sealed record ChatGptInferenceRequest(string Model, string Instructions, string Prompt, JsonElement Schema, string Reasoning = "medium");
public sealed record ChatGptInferenceResult(string Text, ChatGptUsage? Usage = null);
public sealed record ChatGptUsage(long Input, long Output, long CacheRead, long CacheWrite);
public sealed class ChatGptRunnerException(string code, bool requestMayHaveStarted = true) : Exception(code)
{
	public string Code { get; } = code;
	// False is reserved for failures proven to precede starting the inference process.
	// Unknown runners default to uncertainty so they cannot accidentally permit replay.
	public bool RequestMayHaveStarted { get; } = requestMayHaveStarted;
}
