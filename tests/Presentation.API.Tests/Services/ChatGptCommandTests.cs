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
		events.Should().Equal("begin", "runner", "complete");
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
		service.VerifyNoOtherCalls();
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
			.AddSingleton<IOptions<PiNormalizationRunnerOptions>>(Options.Create(new PiNormalizationRunnerOptions { HomeDirectory = home })).BuildServiceProvider();
		(await ChatGptCommand.RunAsync(["--chatgpt-login"], services, output, error, cancellation.Token)).Should().Be(130);
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

	private async Task<int> Run(params string[] args)
	{
		using var services = new ServiceCollection().AddSingleton(runner.Object).AddSingleton(service.Object)
			.AddSingleton<IOptions<PiNormalizationRunnerOptions>>(Options.Create(new PiNormalizationRunnerOptions { HomeDirectory = home })).BuildServiceProvider();
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
