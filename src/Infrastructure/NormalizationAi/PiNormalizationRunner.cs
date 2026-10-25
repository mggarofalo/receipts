using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Application.NormalizationAi;
using Microsoft.Extensions.Options;

namespace Infrastructure.NormalizationAi;

/// <summary>Isolated provider runtime. OAuth secrets never cross the process protocol.</summary>
public sealed class PiNormalizationRunner(IOptions<PiNormalizationRunnerOptions> options) : IChatGptRunner
{
	private const int MaxOutputCharacters = 2 * 1024 * 1024;
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private static readonly HashSet<string> SafeErrorCodes = [
		"auth_required", "rate_limited", "access_denied", "model_access_denied", "transport_failed",
		"provider_unavailable", "request_rejected", "auth_failed", "inference_failed", "context_limit",
		"input_limit", "output_limit", "model_unavailable", "incomplete_response", "invalid_device_code",
		"unsupported_auth_interaction", "unknown_command"];

	public async Task<ChatGptRunnerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
	{
		List<JsonElement> messages;
		try { messages = await ExecuteAsync("status", null, null, cancellationToken); }
		catch (ChatGptRunnerException exception) when (exception.Code == "busy")
		{
			// Lock contention is normal during inference. Do not read rotating credentials
			// without the lock, or misrepresent this as disconnected/provider failure.
			return new(false, Busy: true);
		}
		if (messages.Count != 1 || MessageType(messages[0]) is not ("ready" or "auth_required"))
		{
			throw new ChatGptRunnerException("protocol_invalid");
		}

		return new(MessageType(messages[0]) == "ready");
	}

	public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default)
	{
		List<JsonElement> messages = await ExecuteAsync("models", null, null, cancellationToken);
		JsonElement result = RequireResult(messages, "models");
		if (!result.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
		{
			throw new ChatGptRunnerException("protocol_invalid");
		}

		List<string> names = [];
		foreach (JsonElement model in models.EnumerateArray())
		{
			if (model.ValueKind != JsonValueKind.String || model.GetString() is not { Length: > 0 and <= 128 } name)
			{
				throw new ChatGptRunnerException("protocol_invalid");
			}

			names.Add(name);
		}
		return names.Distinct(StringComparer.Ordinal).ToArray();
	}

	public async Task<ChatGptInferenceResult> InferAsync(ChatGptInferenceRequest request, CancellationToken cancellationToken = default)
	{
		string input = JsonSerializer.Serialize(request, JsonOptions);
		if (Encoding.UTF8.GetByteCount(input) > 256 * 1024)
		{
			throw new ChatGptRunnerException("input_limit", requestMayHaveStarted: false);
		}

		List<JsonElement> messages = await ExecuteAsync("infer", input, null, cancellationToken);
		JsonElement result = RequireResult(messages, "result");
		if (!result.TryGetProperty("text", out JsonElement text) || text.ValueKind != JsonValueKind.String ||
			text.GetString() is not { Length: > 0 and <= 524288 } content)
		{
			throw new ChatGptRunnerException("protocol_invalid");
		}

		ChatGptUsage? usage = null;
		if (result.TryGetProperty("usage", out JsonElement values))
		{
			long[] counts = new long[4];
			string[] names = ["input", "output", "cacheRead", "cacheWrite"];
			for (int i = 0; i < names.Length; i++)
			{
				if (values.ValueKind != JsonValueKind.Object || !values.TryGetProperty(names[i], out JsonElement value) ||
					value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out counts[i]) || counts[i] < 0)
				{
					throw new ChatGptRunnerException("protocol_invalid");
				}
			}
			usage = new(counts[0], counts[1], counts[2], counts[3]);
		}
		return new(content, usage);
	}

	public async Task LoginAsync(Action<ChatGptDeviceCode> displayCode, CancellationToken cancellationToken = default)
		=> RequireResult(await ExecuteAsync("login", null, displayCode, cancellationToken), "ready", allowDeviceCode: true);

	public async Task LogoutAsync(CancellationToken cancellationToken = default)
		=> RequireResult(await ExecuteAsync("logout", null, null, cancellationToken), "logged_out");

	private async Task<List<JsonElement>> ExecuteAsync(string command, string? input, Action<ChatGptDeviceCode>? displayCode, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		PiNormalizationRunnerOptions settings = options.Value;
		string home;
		FileStream homeLock;
		try
		{
			home = Path.GetFullPath(settings.HomeDirectory);
			Directory.CreateDirectory(home);
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
			try { homeLock = new FileStream(Path.Combine(home, ".runner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
			catch (IOException exception) when (IsLockContention(exception))
			{ throw new ChatGptRunnerException("busy", requestMayHaveStarted: false); }
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{ throw new ChatGptRunnerException("runner_setup_failed", requestMayHaveStarted: false); }
		await using FileStream heldLock = homeLock;
		string work = Path.Combine(home, "work", Guid.NewGuid().ToString("N"));
		try { Directory.CreateDirectory(work); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{ throw new ChatGptRunnerException("runner_setup_failed", requestMayHaveStarted: false); }
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(command == "login" ? TimeSpan.FromMinutes(20) : TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 600)));
		using Process process = new();
		try
		{
			try
			{
				process.StartInfo = CreateStartInfo(settings, command, home, work);
				if (!process.Start())
				{
					throw new ChatGptRunnerException("unavailable", requestMayHaveStarted: false);
				}
			}
			catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or ArgumentException or IOException or UnauthorizedAccessException)
			{ throw new ChatGptRunnerException("unavailable", requestMayHaveStarted: false); }
			using CancellationTokenRegistration cancellation = timeout.Token.Register(() => Kill(process));
			Task<List<JsonElement>> output = ReadOutputAsync(process.StandardOutput, displayCode, timeout.Token);
			Task errors = DrainErrorsAsync(process.StandardError, timeout.Token);
			_ = output.ContinueWith(_ => Kill(process), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			if (input is not null)
			{
				await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
			}

			process.StandardInput.Close();
			await process.WaitForExitAsync(timeout.Token);
			List<JsonElement> messages = await output;
			await errors;
			timeout.Token.ThrowIfCancellationRequested();
			JsonElement failure = messages.FirstOrDefault(x => MessageType(x) == "error");
			if (failure.ValueKind != JsonValueKind.Undefined || process.ExitCode != 0)
			{
				string? code = failure.ValueKind == JsonValueKind.Object && failure.TryGetProperty("code", out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
				throw new ChatGptRunnerException(code is not null && SafeErrorCodes.Contains(code) ? code : "request_failed");
			}
			return messages;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ChatGptRunnerException("timeout"); }
		catch (JsonException) { throw new ChatGptRunnerException("protocol_invalid"); }
		catch (IOException) when (timeout.IsCancellationRequested)
		{
			cancellationToken.ThrowIfCancellationRequested();
			throw new ChatGptRunnerException("timeout");
		}
		catch (IOException) { throw new ChatGptRunnerException("transport_failed"); }
		finally
		{
			Kill(process);
			try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
			try { Directory.Delete(work, recursive: true); } catch (IOException) { }
		}
	}

	private static bool IsLockContention(IOException exception)
		// Windows sharing/lock violations; Unix FileStream advisory lock reports EAGAIN.
		=> (exception.HResult & 0xffff) is 32 or 33 || (!OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 11);

	private static ProcessStartInfo CreateStartInfo(PiNormalizationRunnerOptions settings, string command, string home, string work)
	{
		ProcessStartInfo start = new(settings.NodeExecutable)
		{
			WorkingDirectory = work,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		start.ArgumentList.Add(Path.GetFullPath(settings.BridgePath));
		start.ArgumentList.Add(command);
		start.Environment.Clear();
		foreach (string name in new[] { "PATH", "SystemRoot", "WINDIR", "PATHEXT" })
		{
			if (Environment.GetEnvironmentVariable(name) is { } value)
			{
				start.Environment[name] = value;
			}
		}

		foreach (string name in new[] { "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "TMPDIR", "TMP", "TEMP", "PI_CODING_AGENT_DIR" })
		{
			start.Environment[name] = work;
		}

		start.Environment["RECEIPTS_CHATGPT_HOME"] = home;
		start.Environment["NO_COLOR"] = "1";
		return start;
	}

	private static async Task<List<JsonElement>> ReadOutputAsync(StreamReader reader, Action<ChatGptDeviceCode>? displayCode, CancellationToken cancellationToken)
	{
		List<JsonElement> messages = [];
		StringBuilder line = new();
		char[] buffer = new char[4096];
		int total = 0;
		int count;
		while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
		{
			total += count;
			if (total > MaxOutputCharacters)
			{
				throw new ChatGptRunnerException("output_limit");
			}

			for (int i = 0; i < count; i++)
			{
				if (buffer[i] != '\n') { line.Append(buffer[i]); continue; }
				using JsonDocument document = JsonDocument.Parse(line.ToString());
				line.Clear();
				JsonElement message = document.RootElement.Clone();
				string type = MessageType(message);
				if (type is not ("ready" or "auth_required" or "models" or "result" or "logged_out" or "device_code" or "error") || messages.Count >= 16)
				{
					throw new ChatGptRunnerException("protocol_invalid");
				}

				messages.Add(message);
				if (type == "device_code")
				{
					if (displayCode is null || !message.TryGetProperty("url", out JsonElement url) || url.ValueKind != JsonValueKind.String ||
						url.GetString() != "https://auth.openai.com/codex/device" || !message.TryGetProperty("code", out JsonElement code) || code.ValueKind != JsonValueKind.String ||
						code.GetString() is not { Length: > 0 and <= 32 } userCode || userCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
					{
						throw new ChatGptRunnerException("invalid_device_code");
					}

					displayCode(new(url.GetString()!, userCode));
				}
			}
		}
		if (line.Length != 0)
		{
			throw new ChatGptRunnerException("protocol_invalid");
		}

		return messages;
	}

	private static string MessageType(JsonElement value)
		=> value.ValueKind == JsonValueKind.Object && value.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String ? type.GetString()! : "";

	private static JsonElement RequireResult(List<JsonElement> messages, string type, bool allowDeviceCode = false)
	{
		if (messages.Count == 0 || MessageType(messages[^1]) != type ||
			messages.Take(messages.Count - 1).Any(x => !allowDeviceCode || MessageType(x) != "device_code"))
		{
			throw new ChatGptRunnerException("protocol_invalid");
		}

		return messages[^1];
	}

	private static void Kill(Process process)
	{
		try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } } catch (InvalidOperationException) { }
	}

	private static async Task DrainErrorsAsync(StreamReader reader, CancellationToken cancellationToken)
	{
		char[] buffer = new char[4096];
		while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) > 0) { }
	}
}
