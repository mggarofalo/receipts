using Application.NormalizationAi;
using Infrastructure.NormalizationAi;
using Microsoft.Extensions.Options;

namespace API.Services;

/// <summary>Operator-only device authentication. Never exposes credentials through HTTP.</summary>
public static class ChatGptCommand
{
	public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
	{
		if (args.Length == 0 || !(args.Length == 1 && args[0] is "--chatgpt-login" or "--chatgpt-status" or "--chatgpt-logout"
			|| args.Length == 2 && args[0] == "--chatgpt-probe"
						|| args.Length == 4 && args[0] is "--chatgpt-evaluate" or "--chatgpt-evaluate-catalog"))
		{
			await error.WriteLineAsync("Usage: --chatgpt-login | --chatgpt-status | --chatgpt-logout | --chatgpt-probe MODEL | --chatgpt-evaluate MODEL example|holdout REPORT_PATH | --chatgpt-evaluate-catalog MODEL example|holdout REPORT_PATH");
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
					await ChangeConnectionAsync(services, () => runner.LoginAsync(device => output.WriteLine($"Open {device.Url} and enter code {device.Code}"), token), token);
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
						return args[0] == "--chatgpt-evaluate-catalog"
								? await ChatGptCatalogEvaluationCommand.RunAsync(args[1], args[2], args[3], runner, output, token)
								: await ChatGptEvaluationCommand.RunAsync(args[1], args[2], args[3], runner, output, token);
					}
				case "--chatgpt-probe":
					if (!(await runner.GetModelsAsync(token)).Contains(args[1], StringComparer.Ordinal))
					{
						await error.WriteLineAsync("Choose a model listed by --chatgpt-status.");
						return 2;
					}
					NormalizationAiInput[] inputs = [new(Guid.NewGuid(), "HOT DOG BUNS 8CT", [])];
					ChatGptInferenceResult result = await runner.InferAsync(new(args[1], NormalizationAiPolicy.Instructions,
						NormalizationAiPolicy.BuildPrompt(inputs), NormalizationAiPolicy.Schema), token);
					NormalizationAiPolicy.Validate(result.Text, inputs);
					await output.WriteLineAsync("Synthetic inference succeeded and passed normalization schema validation. No receipt data was sent or changed.");
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
			return 1;
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

	private static async Task ChangeConnectionAsync(IServiceProvider services, Func<Task> change, CancellationToken cancellationToken)
	{
		using ChatGptOperatorLease operatorLease = ChatGptOperatorLease.Acquire(services.GetRequiredService<IOptions<PiNormalizationRunnerOptions>>().Value);
		INormalizationAiService service = services.GetRequiredService<INormalizationAiService>();
		Guid changeId = await service.BeginConnectionChangeAsync(cancellationToken);
		try
		{
			// Fencing an active run asks its worker to stop. Give that process time
			// to release the SDK lock; never retry a started OAuth exchange.
			for (int attempt = 0; ; attempt++)
			{
				try { await change(); break; }
				catch (ChatGptRunnerException exception) when (exception.Code == "busy" && attempt < 20)
				{
					await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
				}
			}
		}
		finally
		{
			using CancellationTokenSource completion = new(TimeSpan.FromSeconds(15));
			await service.CompleteConnectionChangeAsync(changeId, completion.Token);
		}
	}
}
