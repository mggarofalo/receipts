using System.Text.Json;
using API.Services;
using Application.NormalizationAi;
using FluentAssertions;
using Infrastructure.NormalizationAi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Presentation.API.Tests.Services;

public sealed class ChatGptCommandTests : IDisposable
{
	private readonly string home = Path.Combine(Path.GetTempPath(), "receipts-cli-test-" + Guid.NewGuid().ToString("N"));
	private readonly Mock<IChatGptRunner> runner = new(MockBehavior.Strict);
	private readonly Mock<INormalizationAiService> service = new(MockBehavior.Strict);
	private readonly StringWriter output = new();
	private readonly StringWriter error = new();
	private readonly NormalizationAiOptions settings = new() { Enabled = true, OwnerUserId = "owner" };
	private NormalizationAiConnection connection = new(true, true, true, Guid.NewGuid(), ["model-a"]);

	public ChatGptCommandTests()
	{
		service.Setup(x => x.GetConnectionAsync("owner", It.IsAny<CancellationToken>()))
			.ReturnsAsync(() => connection);
	}

	[Theory]
	[InlineData("")]
	[InlineData("--chatgpt-probe")]
	[InlineData("--chatgpt-login extra")]
	[InlineData("--chatgpt-unknown")]
	public async Task InvalidArguments_RejectBeforeResolvingServices(string arguments)
	{
		using var services = new ServiceCollection().BuildServiceProvider();
		(await ChatGptCommand.RunAsync(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), services, output, error)).Should().Be(2);
		error.ToString().Should().Contain("Usage:");
		output.ToString().Should().BeEmpty();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Status_ReportsPresenceWithoutClaimingInferenceOrChangingConnection(bool present)
	{
		runner.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptRunnerStatus(present));
		runner.Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "model-a" });
		(await Run("--chatgpt-status")).Should().Be(0);
		output.ToString().Should().Contain($"Credentials present: {present}").And.Contain("does not prove inference entitlement").And.Contain("model-a");
		service.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData("--chatgpt-login", false)]
	[InlineData("--chatgpt-logout", false)]
	[InlineData("--chatgpt-login", true)]
	[InlineData("--chatgpt-logout", true)]
	public async Task CredentialChange_FencesBeforeRunnerAndCompletesEpochEvenOnFailure(string command, bool fail)
	{
		Guid epoch = Guid.NewGuid();
		List<string> events = [];
		service.Setup(x => x.BeginConnectionChangeAsync(It.IsAny<CancellationToken>())).Callback(() => events.Add("begin")).ReturnsAsync(epoch);
		service.Setup(x => x.CompleteConnectionChangeAsync(epoch, It.IsAny<CancellationToken>())).Callback(() => events.Add("complete")).Returns(Task.CompletedTask);
		service.Setup(x => x.BindConnectionChangeAsync(epoch, "owner", It.IsAny<CancellationToken>())).Callback(() => events.Add("bind")).Returns(Task.CompletedTask);
		Task Change()
		{
			events.Add("runner");
			return fail ? Task.FromException(new ChatGptRunnerException("auth_required")) : Task.CompletedTask;
		}
		if (command == "--chatgpt-login")
		{
			runner.Setup(x => x.LoginAsync(It.IsAny<Action<ChatGptDeviceCode>>(), It.IsAny<CancellationToken>())).Returns((Action<ChatGptDeviceCode> show, CancellationToken _) => { show(new("https://auth.openai.com/codex/device", "ABCD-1234")); return Change(); });
		}
		else
		{
			runner.Setup(x => x.LogoutAsync(It.IsAny<CancellationToken>())).Returns(Change);
		}

		(await Run(command)).Should().Be(fail ? 1 : 0);
		events.Should().Equal("begin", "runner", command == "--chatgpt-login" && !fail ? "bind" : "complete");
		if (fail)
		{
			output.ToString().Should().NotContain("Credentials saved").And.NotContain("credentials removed");
		}

		if (command == "--chatgpt-login")
		{
			output.ToString().Should().Contain("ABCD-1234");
		}

		using var released = ChatGptOperatorLease.Acquire(new() { HomeDirectory = home });
	}

	[Fact]
	public async Task Probe_UsesOnlySyntheticInputAndValidatesOutputBeforeClaimingSuccess()
	{
		runner.Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "model-a" });
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((ChatGptInferenceRequest request, CancellationToken _) =>
		{
			request.Model.Should().Be("model-a");
			using var prompt = JsonDocument.Parse(request.Prompt);
			prompt.RootElement.GetArrayLength().Should().Be(1);
			var row = prompt.RootElement[0];
			row.GetProperty("description").GetString().Should().Be("HOT DOG BUNS 8CT");
			row.GetProperty("candidates").GetArrayLength().Should().Be(0);
			return new ChatGptInferenceResult(JsonSerializer.Serialize(new { results = new[] { new { id = row.GetProperty("id").GetGuid(), action = "abstain", targetId = (Guid?)null, displayLabel = (string?)null, reason = "Synthetic test" } } }));
		});
		(await Run("--chatgpt-probe", "model-a")).Should().Be(0);
		output.ToString().Should().Contain("passed normalization schema validation").And.Contain("No receipt data was sent or changed");
		service.Verify(x => x.GetConnectionAsync("owner", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
	}

	[Fact]
	public async Task Probe_RejectsUnknownModelWithoutInference()
	{
		runner.Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "model-a" });
		(await Run("--chatgpt-probe", "MODEL-A")).Should().Be(2);
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Probe_RejectsInvalidModelOutputWithoutPrintingProviderText()
	{
		runner.Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "model-a" });
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptInferenceResult("provider-private-response"));
		(await Run("--chatgpt-probe", "model-a")).Should().Be(1);
		output.ToString().Should().NotContain("succeeded");
		error.ToString().Should().Contain("invalid_output").And.NotContain("provider-private-response");
	}

	[Fact]
	public async Task BusyStatus_DoesNotRaceModelDiscoveryOrClaimMissingCredentials()
	{
		runner.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptRunnerStatus(false, true));
		(await Run("--chatgpt-status")).Should().Be(1);
		output.ToString().Should().Contain("busy").And.NotContain("Credentials present: False");
		runner.Verify(x => x.GetModelsAsync(It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task BusyOperator_DoesNotFenceOrStartAnotherLogin()
	{
		using var lease = ChatGptOperatorLease.Acquire(new() { HomeDirectory = home });
		(await Run("--chatgpt-login")).Should().Be(1);
		error.ToString().Should().Contain("operator_busy");
		service.VerifyNoOtherCalls(); runner.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task Cancellation_CompletesFencingWithIndependentTokenAndReturns130()
	{
		Guid epoch = Guid.NewGuid();
		using var cancellation = new CancellationTokenSource();
		service.Setup(x => x.BeginConnectionChangeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(epoch);
		service.Setup(x => x.CompleteConnectionChangeAsync(epoch, It.IsAny<CancellationToken>()))
			.Callback((Guid _, CancellationToken token) => token.IsCancellationRequested.Should().BeFalse()).Returns(Task.CompletedTask);
		runner.Setup(x => x.LoginAsync(It.IsAny<Action<ChatGptDeviceCode>>(), It.IsAny<CancellationToken>()))
			.Returns((Action<ChatGptDeviceCode> _, CancellationToken token) => { cancellation.Cancel(); return Task.FromCanceled(token); });
		using var services = new ServiceCollection().AddSingleton(runner.Object).AddSingleton(service.Object)
			.AddSingleton<IOptions<NormalizationAiOptions>>(Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = "owner" }))
						.AddSingleton<IOptions<PiNormalizationRunnerOptions>>(Options.Create(new PiNormalizationRunnerOptions { HomeDirectory = home })).BuildServiceProvider();
		(await ChatGptCommand.RunAsync(["--chatgpt-login", "--owner", "owner", "--consent"], services, output, error, cancellation.Token)).Should().Be(130);
		service.Verify(x => x.CompleteConnectionChangeAsync(epoch, It.IsAny<CancellationToken>()), Times.Once);
		output.ToString().Should().NotContain("Credentials saved");
	}

	[Fact]
	public async Task UnexpectedFailure_DoesNotPrintExceptionSecrets()
	{
		runner.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("secret-token-and-provider-body"));
		(await Run("--chatgpt-status")).Should().Be(1);
		error.ToString().Should().Contain("setup failed").And.NotContain("secret-token");
	}

	[Theory]
	[InlineData("--chatgpt-probe")]
	[InlineData("--chatgpt-evaluate")]
	[InlineData("--chatgpt-evaluate-catalog")]
	public async Task Inference_RejectsUnavailableBindingsBeforeRunnerCalls(string command)
	{
		NormalizationAiConnection ready = connection;
		foreach (NormalizationAiConnection denied in new[]
		{
			ready with { CredentialsPresent = false }, // Includes legacy credentials without consent binding.
			ready with { IsOwner = false },
			ready with { Busy = true },
			ready with { Configured = false },
		})
		{
			connection = denied;
			(await RunInference(command)).Should().Be(1);
		}
		runner.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData("--chatgpt-probe")]
	[InlineData("--chatgpt-evaluate")]
	[InlineData("--chatgpt-evaluate-catalog")]
	public async Task Inference_RejectsUnconfiguredOwnerAndUnknownModelBeforeRunnerCalls(string command)
	{
		settings.Enabled = false;
		(await RunInference(command)).Should().Be(1);
		settings.Enabled = true;
		settings.OwnerUserId = "";
		(await RunInference(command)).Should().Be(1);
		service.VerifyNoOtherCalls();
		settings.OwnerUserId = "owner";
		connection = connection with { Models = ["other-model"] };
		(await RunInference(command)).Should().Be(command == "--chatgpt-probe" ? 2 : 1);
		runner.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData("--chatgpt-probe")]
	[InlineData("--chatgpt-evaluate")]
	[InlineData("--chatgpt-evaluate-catalog")]
	public async Task Inference_RejectsGenerationChangedDuringResponseAndStopsLaterCases(string command)
	{
		runner.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptRunnerStatus(true));
		runner.Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "model-a" });
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(() =>
			{
				connection = connection with { Generation = Guid.NewGuid() };
				return new ChatGptInferenceResult("private-stale-provider-response");
			});
		(await RunInference(command)).Should().Be(1);
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Once);
		output.ToString().Should().NotContain("succeeded").And.NotContain("validated");
		error.ToString().Should().NotContain("private-stale-provider-response");
		if (command != "--chatgpt-probe")
		{
			string report = await File.ReadAllTextAsync(Path.Combine(home, "report.json"));
			report.Should().Contain("connection_changed").And.NotContain("private-stale-provider-response");
		}
	}

	[Theory]
	[InlineData("--chatgpt-evaluate", false)]
	[InlineData("--chatgpt-evaluate", true)]
	[InlineData("--chatgpt-evaluate-catalog", false)]
	[InlineData("--chatgpt-evaluate-catalog", true)]
	public async Task Evaluation_RechecksBeforeLaterCasesAndAllowsStableBinding(string command, bool rotateBeforeNextCase)
	{
		int checks = 0;
		service.Setup(x => x.GetConnectionAsync("owner", It.IsAny<CancellationToken>())).ReturnsAsync(() =>
		{
			if (++checks == 4 && rotateBeforeNextCase) { connection = connection with { Generation = Guid.NewGuid() }; }
			return connection;
		});
		runner.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptRunnerStatus(true));
		runner.Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "model-a" });
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((ChatGptInferenceRequest request, CancellationToken _) => SyntheticAbstention(request, command));
		(await RunInference(command)).Should().Be(rotateBeforeNextCase ? 1 : 0);
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()),
			rotateBeforeNextCase ? Times.Once() : Times.AtLeast(2));
		string report = await File.ReadAllTextAsync(Path.Combine(home, "report.json"));
		report.Should().Contain("validated");
		if (rotateBeforeNextCase) { report.Should().Contain("connection_changed"); }
	}

	[Theory]
	[InlineData("--chatgpt-probe", true)]
	[InlineData("--chatgpt-probe", false)]
	[InlineData("--chatgpt-evaluate", true)]
	[InlineData("--chatgpt-evaluate", false)]
	[InlineData("--chatgpt-evaluate-catalog", true)]
	[InlineData("--chatgpt-evaluate-catalog", false)]
	public async Task Inference_RejectsOwnerOrConsentChangedDuringResponse(string command, bool changeOwner)
	{
		service.Setup(x => x.GetConnectionAsync("other-owner", It.IsAny<CancellationToken>())).ReturnsAsync(() => connection);
		runner.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptRunnerStatus(true));
		runner.Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "model-a" });
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((ChatGptInferenceRequest request, CancellationToken _) =>
			{
				if (changeOwner) { settings.OwnerUserId = "other-owner"; }
				else { connection = connection with { CredentialsPresent = false }; }
				return SyntheticAbstention(request, command);
			});
		(await RunInference(command)).Should().Be(1);
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Once);
		output.ToString().Should().NotContain("validated").And.NotContain("succeeded");
	}

	private static ChatGptInferenceResult SyntheticAbstention(ChatGptInferenceRequest request, string command)
	{
		using JsonDocument prompt = JsonDocument.Parse(request.Prompt);
		object[] results = prompt.RootElement.EnumerateArray().Select(row => command == "--chatgpt-evaluate-catalog"
			? (object)new { id = row.GetProperty("id").GetGuid(), identityDecision = "insufficient", candidateId = (Guid?)null, newName = (string?)null, suggestedDisplayLabel = (string?)null, reason = "Synthetic test" }
			: new { id = row.GetProperty("id").GetGuid(), action = "abstain", targetId = (Guid?)null, displayLabel = (string?)null, reason = "Synthetic test" }).ToArray();
		return new(JsonSerializer.Serialize(new { results }));
	}

	private Task<int> RunInference(string command)
	{
		Directory.CreateDirectory(home);
		return command == "--chatgpt-probe" ? Run(command, "model-a")
			: Run(command, "model-a", "example", Path.Combine(home, "report.json"));
	}

	private async Task<int> Run(params string[] args)
	{
		using var services = new ServiceCollection().AddSingleton(runner.Object).AddSingleton(service.Object)
			.AddSingleton<IOptions<NormalizationAiOptions>>(Options.Create(settings))
						.AddSingleton<IOptions<PiNormalizationRunnerOptions>>(Options.Create(new PiNormalizationRunnerOptions { HomeDirectory = home })).BuildServiceProvider();
		if (args is ["--chatgpt-login"]) { args = ["--chatgpt-login", "--owner", "owner", "--consent"]; }
		return await ChatGptCommand.RunAsync(args, services, output, error);
	}

	public void Dispose()
	{
		output.Dispose(); error.Dispose();
		if (Directory.Exists(home))
		{
			Directory.Delete(home, true);
		}
	}
}
