using Application.NormalizationAi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.NormalizationAi;

/// <summary>
/// Register as a singleton. Attempts remain in memory, outside database backups. The process-wide
/// operator lease excludes CLI changes and other hosts sharing the protected credential volume.
/// Restart loses the ceremony (Get returns restart); it never resumes or replays an OAuth exchange.
/// </summary>
public sealed class ChatGptDeviceSignIn(IChatGptRunner runner, IServiceScopeFactory scopes,
	IOptions<NormalizationAiOptions> options, IOptions<PiNormalizationRunnerOptions> runnerOptions,
	TimeProvider clock) : IChatGptDeviceSignIn, IAsyncDisposable
{
	private readonly object gate = new();
	private Attempt? current;
	private bool disposed;

	private void RequireOwner(string actor)
	{
		if (!options.Value.Enabled || string.IsNullOrWhiteSpace(options.Value.OwnerUserId))
		{ throw new NormalizationAiException(503, "not_configured", "ChatGPT is not configured for this deployment."); }
		if (actor != options.Value.OwnerUserId)
		{ throw new NormalizationAiException(403, "owner_required", "Only the designated ChatGPT connection owner can manage sign-in."); }
	}

	public ChatGptSignInAttempt Start(string actor, bool ownerConsent)
	{
		RequireOwner(actor);
		if (!ownerConsent)
		{ throw new NormalizationAiException(400, "consent_required", "Confirm that you own and authorize use of the ChatGPT plan for this deployment."); }
		lock (gate)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			if (current is { Work.IsCompleted: false })
			{ return new(current.Snapshot.Id, "busy"); }
			ChatGptOperatorLease lease;
			try { lease = ChatGptOperatorLease.Acquire(runnerOptions.Value); }
			catch (ChatGptRunnerException) { return new(Guid.Empty, "busy"); }
			current?.Cancellation.Dispose();
			Attempt attempt = new();
			current = attempt;
			attempt.Work = RunAsync(attempt, lease, actor);
			return attempt.Snapshot;
		}
	}

	public ChatGptSignInAttempt Get(string actor, Guid attemptId)
	{
		RequireOwner(actor);
		lock (gate) { return current?.Snapshot.Id == attemptId ? current.Snapshot : new(attemptId, "restart"); }
	}

	public async Task<ChatGptSignInAttempt> CancelAsync(string actor, Guid attemptId, CancellationToken cancellationToken)
	{
		RequireOwner(actor);
		Task work;
		lock (gate)
		{
			if (current?.Snapshot.Id != attemptId) { return new(attemptId, "restart"); }
			current.Cancellation.Cancel();
			work = current.Work;
		}
		await work.WaitAsync(cancellationToken);
		return Get(actor, attemptId);
	}

	public async Task DisconnectAsync(string actor, CancellationToken cancellationToken)
	{
		RequireOwner(actor);
		// A caller cancels a pending attempt first; never replace an exchange while it can still save tokens.
		using ChatGptOperatorLease lease = ChatGptOperatorLease.Acquire(runnerOptions.Value);
		await using AsyncServiceScope scope = scopes.CreateAsyncScope();
		INormalizationAiService service = scope.ServiceProvider.GetRequiredService<INormalizationAiService>();
		Guid generation = await service.BeginConnectionChangeAsync(cancellationToken);
		try { await runner.LogoutAsync(cancellationToken); }
		finally { await CompleteAsync(service, generation); }
	}

	private async Task RunAsync(Attempt attempt, ChatGptOperatorLease lease, string consentingOwner)
	{
		using (lease)
		{
			try
			{
				await using AsyncServiceScope scope = scopes.CreateAsyncScope();
				INormalizationAiService service = scope.ServiceProvider.GetRequiredService<INormalizationAiService>();
				Guid generation = await service.BeginConnectionChangeAsync(attempt.Cancellation.Token);
				bool succeeded = false;
				try
				{
					await runner.LoginAsync(code =>
					{
						if (code.ExpiresInSeconds is not > 0) { throw new ChatGptRunnerException("invalid_device_code"); }
						lock (gate)
						{
							attempt.Snapshot = new(attempt.Snapshot.Id, "waiting", code.Url, code.Code,
							clock.GetUtcNow().AddSeconds(code.ExpiresInSeconds.Value));
						}
					}, attempt.Cancellation.Token);
					attempt.Cancellation.Token.ThrowIfCancellationRequested();
					succeeded = true;
				}
				finally
				{
					using CancellationTokenSource completion = new(TimeSpan.FromSeconds(15));
					if (succeeded) { await service.BindConnectionChangeAsync(generation, consentingOwner, completion.Token); }
					else { await service.CompleteConnectionChangeAsync(generation, completion.Token); }
				}
				SetTerminal(attempt, "success");
			}
			catch (OperationCanceledException) when (attempt.Cancellation.IsCancellationRequested) { SetTerminal(attempt, "cancelled"); }
			catch (ChatGptRunnerException error)
			{
				SetTerminal(attempt, error.Code switch
				{
					"device_expired" => "expired",
					"device_rejected" => "rejected",
					"busy" or "operator_busy" => "busy",
					_ => "reconnect"
				});
			}
			catch (Exception) { SetTerminal(attempt, "reconnect"); }
		}
	}

	private static async Task CompleteAsync(INormalizationAiService service, Guid generation)
	{
		using CancellationTokenSource completion = new(TimeSpan.FromSeconds(15));
		await service.CompleteConnectionChangeAsync(generation, completion.Token);
	}

	private void SetTerminal(Attempt attempt, string state)
	{
		lock (gate) { attempt.Snapshot = new(attempt.Snapshot.Id, state); }
	}

	public async ValueTask DisposeAsync()
	{
		Task work;
		lock (gate)
		{
			if (disposed) { return; }
			disposed = true; current?.Cancellation.Cancel(); work = current?.Work ?? Task.CompletedTask;
		}
		await work;
		current?.Cancellation.Dispose();
	}

	private sealed class Attempt
	{
		public ChatGptSignInAttempt Snapshot { get; set; } = new(Guid.NewGuid(), "starting");
		public CancellationTokenSource Cancellation { get; } = new();
		public Task Work { get; set; } = Task.CompletedTask;
	}
}
