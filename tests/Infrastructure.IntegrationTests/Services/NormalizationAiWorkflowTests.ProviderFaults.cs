using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.NormalizationAi;
using FluentAssertions;
using Infrastructure.NormalizationAi;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationAiWorkflowTests
{
	[Fact]
	public async Task PreDispatchContention_ResumedInferenceReportsRunningWithoutWaitingDiagnostics()
	{
		var ids = await SeedAsync();
		var service = Service();
		var run = await StartSourcesAsync(service);
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new ChatGptRunnerException("busy", requestMayHaveStarted: false));
		await service.ProcessNextAsync(CancellationToken.None);
		var waiting = (await Service().GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		waiting.State.Should().Be("queued");
		waiting.ErrorCode.Should().Be("runner_busy");
		waiting.ErrorDetail.Should().Contain("No inference was started");
		await using (var db = fixture.CreateDbContext())
		{
			var saved = await db.NormalizationAiRuns.FindAsync(run.Id);
			saved!.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
			await db.SaveChangesAsync();
		}

		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.Returns(async (ChatGptInferenceRequest request, CancellationToken _) =>
			{
				entered.TrySetResult();
				await release.Task;
				return Response(request);
			});
		var work = Service().ProcessNextAsync(CancellationToken.None);
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
			var running = (await Service().GetRunAsync(Owner, run.Id, CancellationToken.None))!;
			running.State.Should().Be("running");
			running.ErrorCode.Should().BeNull("inference has resumed after the local contention backoff");
			running.ErrorDetail.Should().BeNull("an active inference must not claim that no inference was started");
			(await Service().ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
		}
		finally
		{
			release.TrySetResult();
			await work.WaitAsync(TimeSpan.FromSeconds(15));
		}
		var ready = (await Service().GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		ready.State.Should().Be("ready");
		ready.Proposals.Should().NotBeEmpty();
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
	}

	[Fact]
	public async Task PreDispatchContention_RestartDispatchesSameRequestOnlyAfterBackoff()
	{
		var ids = await SeedAsync();
		var service = Service();
		var run = await StartSourcesAsync(service);
		int attempts = 0;
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.Returns((ChatGptInferenceRequest request, CancellationToken _) => ++attempts == 1
				? Task.FromException<ChatGptInferenceResult>(new ChatGptRunnerException("busy", requestMayHaveStarted: false))
				: Task.FromResult(Response(request)));
		await service.ProcessNextAsync(CancellationToken.None);
		(await Service().GetConnectionAsync(Owner, CancellationToken.None)).Busy.Should().BeTrue();
		runner.Verify(x => x.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once, "queued refresh must not contend with inference");
		(await Service().ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
		await using (var db = fixture.CreateDbContext())
		{
			var saved = await db.NormalizationAiRuns.FindAsync(run.Id);
			saved!.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
			await db.SaveChangesAsync();
		}
		await Service().ProcessNextAsync(CancellationToken.None);
		var ready = (await Service().GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		ready.State.Should().Be("ready");
		ready.Id.Should().Be(run.Id);
		ready.ErrorCode.Should().BeNull();
		(await Service().ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
		attempts.Should().Be(2, "only the second local attempt dispatched inference");
	}

	[Fact]
	public async Task PreDispatchContention_BackoffSurvivesRestartAndCancellationPreventsDispatch()
	{
		var ids = await SeedAsync();
		var service = Service();
		var run = await StartSourcesAsync(service);
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new ChatGptRunnerException("busy", requestMayHaveStarted: false));
		await service.ProcessNextAsync(CancellationToken.None);
		var waiting = (await Service().GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		waiting.State.Should().Be("queued");
		waiting.ErrorCode.Should().Be("runner_busy");
		waiting.ErrorDetail.Should().NotContain("may have occurred");
		(await Service().ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
		await service.CancelAsync(Owner, run.Id, CancellationToken.None);
		(await Service().ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PreDispatchFailure_IsActionableWithoutUnknownUsage(bool contentionExhausted)
	{
		var ids = await SeedAsync();
		var service = Service();
		var run = await StartSourcesAsync(service);
		if (contentionExhausted)
		{
			await using var db = fixture.CreateDbContext();
			var saved = await db.NormalizationAiRuns.FindAsync(run.Id);
			saved!.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-6);
			await db.SaveChangesAsync();
		}
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new ChatGptRunnerException(contentionExhausted ? "busy" : "runner_setup_failed", requestMayHaveStarted: false));
		await service.ProcessNextAsync(CancellationToken.None);
		var failed = (await Service().GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		failed.State.Should().Be("failed");
		failed.ErrorCode.Should().Be(contentionExhausted ? "runner_busy_timeout" : "runner_setup_failed");
		failed.ErrorDetail.Should().Contain("No inference was started");
		(await Service().ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Theory]
	[InlineData("rate_limited")]
	[InlineData("auth_failed")]
	[InlineData("transport_failed")]
	public async Task ScriptedProvider_DispatchedFailureSurvivesRestartAndRequiresExplicitRetry(string providerCode)
	{
		var ids = await SeedAsync();
		var script = new ProviderScript(runner);
		script.Statuses.Enqueue(new(true));
		script.Models.Enqueue(["test-model"]);
		script.Inferences.Enqueue((_, _) => Task.FromException<ChatGptInferenceResult>(new ChatGptRunnerException(providerCode)));
		script.Inferences.Enqueue((request, _) => Task.FromResult(Response(request)));
		var service = Service();
		int audits = await AuditCountAsync(ids);
		var run = await StartSourcesAsync(service);

		(await service.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();
		var restarted = Service();
		var unknown = (await restarted.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		unknown.State.Should().Be("unknown", "a dispatched provider failure cannot prove no billable execution occurred");
		unknown.ErrorCode.Should().Be("unknown_outcome");
		(await restarted.ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
		script.Calls.Should().HaveCount(1, "restarting the application must not automatically replay uncertain execution");
		await AssertUnchangedAsync(ids);
		(await AuditCountAsync(ids)).Should().Be(audits);

		Guid retryKey = Guid.NewGuid();
		var retry = await restarted.RetryAsync(Owner, run.Id, retryKey, CancellationToken.None);
		retry.Id.Should().NotBe(run.Id);
		retry.State.Should().Be("queued");
		(await restarted.RetryAsync(Owner, run.Id, retryKey, CancellationToken.None)).Id.Should().Be(retry.Id);
		script.Calls.Should().HaveCount(1, "an explicit retry must enqueue once before a worker dispatches it");
		// Retry supersedes the old reservation; its terminal tombstone records the deliberate action.
		var superseded = (await restarted.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		superseded.State.Should().Be("cancelled");
		superseded.ErrorCode.Should().Be("retried");
		(await restarted.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();
		(await restarted.GetRunAsync(Owner, retry.Id, CancellationToken.None))!.State.Should().Be("ready");
		(await restarted.GetRunAsync(Owner, run.Id, CancellationToken.None))!.ErrorCode.Should().Be("retried");
		(await restarted.ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
		script.Calls.Should().HaveCount(2, "a completed explicit retry must leave no queued work");
		script.Calls.Should().OnlyContain(call => call.Model == "test-model" && call.PromptHash.Length == 64 && call.Schema.ValueKind == JsonValueKind.Object);
		script.AssertConsumed();
		await AssertUnchangedAsync(ids);
		(await AuditCountAsync(ids)).Should().Be(audits);
	}

	// Test-only scripts use the existing strict Moq runner. Delegates may gate responses,
	// honor/ignore cancellation, throw after dispatch, or return malformed structured output.
	// No credentials or raw receipt prompts are retained in the diagnostic ledger.
	private sealed class ProviderScript
	{
		public Queue<ChatGptRunnerStatus> Statuses { get; } = new();
		public Queue<IReadOnlyList<string>> Models { get; } = new();
		public Queue<Func<ChatGptInferenceRequest, CancellationToken, Task<ChatGptInferenceResult>>> Inferences { get; } = new();
		public List<(string Model, string PromptHash, JsonElement Schema)> Calls { get; } = [];

		public ProviderScript(Mock<IChatGptRunner> runner)
		{
			runner.Reset();
			runner.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
				.Returns(() => Task.FromResult(Next(Statuses, "status")));
			runner.Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
				.Returns(() => Task.FromResult(Next(Models, "models")));
			runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
				.Returns((ChatGptInferenceRequest request, CancellationToken token) =>
				{
					Calls.Add((request.Model, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Prompt))), request.Schema.Clone()));
					return Next(Inferences, "inference")(request, token);
				});
		}

		private static T Next<T>(Queue<T> steps, string operation) => steps.Count > 0
			? steps.Dequeue() : throw new InvalidOperationException("Unexpected scripted provider " + operation + " call");

		public void AssertConsumed()
		{
			Statuses.Should().BeEmpty();
			Models.Should().BeEmpty();
			Inferences.Should().BeEmpty();
		}
	}
}
