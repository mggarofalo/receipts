using Application.NormalizationAi;
using Infrastructure.NormalizationAi;
using Microsoft.Extensions.Options;

namespace API.Services;

/// <summary>Operator-only device authentication. Never exposes credentials through HTTP.</summary>
public static class ChatGptCommand
{
	public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
	{
		if (args.Length == 0 || !(args.Length == 1 && args[0] is "--chatgpt-status" or "--chatgpt-logout"
						|| args.Length == 4 && args[0] == "--chatgpt-login" && args[1] == "--owner" && args[3] == "--consent"
			|| args.Length == 2 && args[0] == "--chatgpt-probe"
						|| args.Length == 4 && args[0] is "--chatgpt-evaluate" or "--chatgpt-evaluate-catalog"))
		{
			await error.WriteLineAsync("Usage: --chatgpt-login --owner OWNER --consent | --chatgpt-status | --chatgpt-logout | --chatgpt-probe MODEL | --chatgpt-evaluate MODEL example|holdout REPORT_PATH | --chatgpt-evaluate-catalog MODEL example|holdout REPORT_PATH");
			return 2;
		}
		using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
		Console.CancelKeyPress += cancel;
		try
		{
			IChatGptRunner runner = services.GetRequiredService<IChatGptRunner>();
			CancellationToken token = cancellation.Token;
			switch (args[0])
			{
				case "--chatgpt-login":
					await ChangeConnectionAsync(services, () => runner.LoginAsync(device => output.WriteLine($"Open {device.Url} and enter code {device.Code}"), token), token, args[2]);
					await output.WriteLineAsync("Credentials saved in the dedicated ChatGPT volume. Run a synthetic probe to verify inference access.");
					break;
				case "--chatgpt-logout":
					await ChangeConnectionAsync(services, () => runner.LogoutAsync(token), token);
					await output.WriteLineAsync("Local ChatGPT credentials removed and outstanding runs invalidated.");
					break;
				case "--chatgpt-status":
					ChatGptRunnerStatus status = await runner.GetStatusAsync(token);
					if (status.Busy)
					{
						await output.WriteLineAsync("ChatGPT is busy. Follow or cancel the current run in Receipts, then try status again.");
						return 1;
					}
					await output.WriteLineAsync($"Credentials present: {status.CredentialsPresent}. This does not prove inference entitlement.");
					foreach (string model in await runner.GetModelsAsync(token)) { await output.WriteLineAsync(model); }
					break;
				case "--chatgpt-evaluate":
				case "--chatgpt-evaluate-catalog":
					// Keep one account stable across all cases; login/logout acquire the same operator lease.
					using (ChatGptOperatorLease.Acquire(services.GetRequiredService<IOptions<PiNormalizationRunnerOptions>>().Value))
					{
						IChatGptRunner boundRunner = await BindInferenceRunnerAsync(services, runner, args[1], token);
						return args[0] == "--chatgpt-evaluate-catalog"
							? await ChatGptCatalogEvaluationCommand.RunAsync(args[1], args[2], args[3], boundRunner, output, token)
							: await ChatGptEvaluationCommand.RunAsync(args[1], args[2], args[3], boundRunner, output, token);
					}
				case "--chatgpt-probe":
					using (ChatGptOperatorLease.Acquire(services.GetRequiredService<IOptions<PiNormalizationRunnerOptions>>().Value))
					{
						IChatGptRunner boundRunner = await BindInferenceRunnerAsync(services, runner, args[1], token);
						NormalizationAiInput[] inputs = [new(Guid.NewGuid(), "HOT DOG BUNS 8CT", [])];
						ChatGptInferenceResult result = await boundRunner.InferAsync(new(args[1], NormalizationAiPolicy.Instructions,
							NormalizationAiPolicy.BuildPrompt(inputs), NormalizationAiPolicy.Schema), token);
						NormalizationAiPolicy.Validate(result.Text, inputs);
						await output.WriteLineAsync("Synthetic inference succeeded and passed normalization schema validation. No receipt data was sent or changed.");
					}
					break;
			}
			return 0;
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
		catch (ChatGptRunnerException exception)
		{
			await error.WriteLineAsync($"ChatGPT operation failed ({exception.Code}). Credentials and provider responses are not printed.");
			return 1;
		}
		catch (NormalizationAiException exception)
		{
			await error.WriteLineAsync($"Normalization operation failed ({exception.Code}).");
			return args[0] == "--chatgpt-probe" && exception.Code == "invalid_model" ? 2 : 1;
		}
		catch (Exception exception) when (args[0] is "--chatgpt-evaluate" or "--chatgpt-evaluate-catalog"
	&& exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			await error.WriteLineAsync("Evaluation requires an explicit model, example or holdout split, and a new writable report path. Existing reports are never overwritten.");
			return 1;
		}
		catch (Exception)
		{
			await error.WriteLineAsync("ChatGPT setup failed. Verify database connectivity and private volume permissions; no credentials have been printed.");
			return 1;
		}
		finally { Console.CancelKeyPress -= cancel; }
	}

	private static async Task<(string Owner, NormalizationAiConnection Connection)> RequireBoundInferenceAsync(IServiceProvider services, string model, CancellationToken cancellationToken)
	{
		NormalizationAiOptions settings = services.GetRequiredService<IOptions<NormalizationAiOptions>>().Value;
		if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.OwnerUserId))
		{ throw new NormalizationAiException(503, "not_configured", "ChatGPT normalization is not configured."); }
		NormalizationAiConnection connection = await services.GetRequiredService<INormalizationAiService>().GetConnectionAsync(settings.OwnerUserId, cancellationToken);
		if (connection.Busy) { throw new NormalizationAiException(409, "busy", "ChatGPT is busy."); }
		if (!connection.Configured || !connection.IsOwner || !connection.CredentialsPresent)
		{ throw new NormalizationAiException(409, "connection_required", "Sign in with explicit owner consent before using inference."); }
		if (!connection.Models.Contains(model, StringComparer.Ordinal))
		{ throw new NormalizationAiException(400, "invalid_model", "Choose an available model."); }
		return (settings.OwnerUserId, connection);
	}

	private static async Task<IChatGptRunner> BindInferenceRunnerAsync(IServiceProvider services, IChatGptRunner runner, string model, CancellationToken token)
	{
		var binding = await RequireBoundInferenceAsync(services, model, token);
		return new BoundInferenceRunner(services, runner, binding.Owner, binding.Connection.Generation);
	}

	// Operator leases prevent credential changes, but restores can rotate the database generation.
	// Recheck outside the runner's credential lock before every request and before accepting its result.
	private sealed class BoundInferenceRunner(IServiceProvider services, IChatGptRunner runner, string owner, Guid generation) : IChatGptRunner
	{
		public Task<ChatGptRunnerStatus> GetStatusAsync(CancellationToken token) => runner.GetStatusAsync(token);
		public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken token) => runner.GetModelsAsync(token);
		public Task LoginAsync(Action<ChatGptDeviceCode> callback, CancellationToken token) => throw new NotSupportedException();
		public Task LogoutAsync(CancellationToken token) => throw new NotSupportedException();
		public async Task<ChatGptInferenceResult> InferAsync(ChatGptInferenceRequest request, CancellationToken token)
		{
			await CheckAsync(request.Model, token);
			ChatGptInferenceResult result = await runner.InferAsync(request, token);
			await CheckAsync(request.Model, token);
			return result;
		}
		private async Task CheckAsync(string model, CancellationToken token)
		{
			var current = await RequireBoundInferenceAsync(services, model, token);
			if (current.Owner != owner || current.Connection.Generation != generation)
			{ throw new NormalizationAiException(409, "connection_changed", "The ChatGPT connection changed during inference."); }
		}
	}

	private static async Task ChangeConnectionAsync(IServiceProvider services, Func<Task> change, CancellationToken cancellationToken, string? consentingOwner = null)
	{
		if (consentingOwner is not null)
		{
			NormalizationAiOptions settings = services.GetRequiredService<IOptions<NormalizationAiOptions>>().Value;
			if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.OwnerUserId) || settings.OwnerUserId != consentingOwner)
			{ throw new NormalizationAiException(403, "owner_required", "Explicit consent must name the configured Receipts owner."); }
		}
		using ChatGptOperatorLease operatorLease = ChatGptOperatorLease.Acquire(services.GetRequiredService<IOptions<PiNormalizationRunnerOptions>>().Value);
		INormalizationAiService service = services.GetRequiredService<INormalizationAiService>();
		Guid changeId = await service.BeginConnectionChangeAsync(cancellationToken);
		bool succeeded = false;
		try
		{
			// Fencing an active run asks its worker to stop. Give that process time
			// to release the SDK lock; never retry a started OAuth exchange.
			for (int attempt = 0; ; attempt++)
			{
				try { await change(); cancellationToken.ThrowIfCancellationRequested(); succeeded = true; break; }
				catch (ChatGptRunnerException exception) when (exception.Code == "busy" && attempt < 20)
				{
					await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
				}
			}
		}
		finally
		{
			using CancellationTokenSource completion = new(TimeSpan.FromSeconds(15));
			if (succeeded && consentingOwner is not null) { await service.BindConnectionChangeAsync(changeId, consentingOwner, completion.Token); }
			else { await service.CompleteConnectionChangeAsync(changeId, completion.Token); }
		}
	}
}
