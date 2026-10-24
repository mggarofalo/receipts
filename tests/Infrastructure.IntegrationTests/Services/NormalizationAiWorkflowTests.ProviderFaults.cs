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
		var run = await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);

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
