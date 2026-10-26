using Application.NormalizationAi;
using FluentAssertions;
using Infrastructure.NormalizationAi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.Tests.NormalizationAi;

public sealed class ChatGptDeviceSignInTests : IAsyncDisposable
{
	private readonly string home = Path.Combine(Path.GetTempPath(), "receipts-device-tests-" + Guid.NewGuid().ToString("N"));
	private readonly Mock<IChatGptRunner> runner = new();
	private readonly Mock<INormalizationAiService> fence = new();
	private readonly ServiceProvider services;
	private readonly ChatGptDeviceSignIn signIn;
	private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Guid generation = Guid.NewGuid();

	public ChatGptDeviceSignInTests()
	{
		fence.Setup(x => x.BeginConnectionChangeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(generation);
		fence.Setup(x => x.CompleteConnectionChangeAsync(generation, It.IsAny<CancellationToken>()))
			.Callback(() => completed.TrySetResult()).Returns(Task.CompletedTask);
		fence.Setup(x => x.BindConnectionChangeAsync(generation, "owner", It.IsAny<CancellationToken>()))
			.Callback(() => completed.TrySetResult()).Returns(Task.CompletedTask);
		services = new ServiceCollection().AddSingleton(fence.Object).BuildServiceProvider();
		signIn = new(runner.Object, services.GetRequiredService<IServiceScopeFactory>(),
			Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = "owner" }),
			Options.Create(new PiNormalizationRunnerOptions { HomeDirectory = home }), TimeProvider.System);
		runner.Setup(x => x.LoginAsync(It.IsAny<Action<ChatGptDeviceCode>>(), It.IsAny<CancellationToken>()))
			.Returns(async (Action<ChatGptDeviceCode> notify, CancellationToken token) =>
			{
				notify(new("https://auth.openai.com/codex/device", "ABCD-1234", 900));
				started.TrySetResult();
				await finish.Task.WaitAsync(token);
			});
	}

	[Theory]
	[InlineData("admin-without-ownership", true, "owner_required")]
	[InlineData("owner", false, "consent_required")]
	public void Start_RequiresDesignatedOwnerAndExplicitConsent(string actor, bool consent, string code)
	{
		Action act = () => signIn.Start(actor, consent);
		act.Should().Throw<NormalizationAiException>().Which.Code.Should().Be(code);
		runner.Verify(x => x.LoginAsync(It.IsAny<Action<ChatGptDeviceCode>>(), It.IsAny<CancellationToken>()), Times.Never);
		Directory.Exists(home).Should().BeFalse();
	}

	[Fact]
	public async Task PendingAttempt_ExposesOnlyCeremonyAndExcludesCompetingOperations()
	{
		DateTimeOffset before = DateTimeOffset.UtcNow;
		ChatGptSignInAttempt attempt = signIn.Start("owner", true);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		attempt = signIn.Get("owner", attempt.Id);
		attempt.State.Should().Be("waiting");
		attempt.VerificationUrl.Should().Be("https://auth.openai.com/codex/device");
		attempt.UserCode.Should().Be("ABCD-1234");
		attempt.ExpiresAt.Should().BeOnOrAfter(before.AddSeconds(900));
		signIn.Start("owner", true).State.Should().Be("busy");
		Action competing = () => ChatGptOperatorLease.Acquire(new() { HomeDirectory = home });
		competing.Should().Throw<ChatGptRunnerException>();
		ChatGptSignInAttempt cancelled = await signIn.CancelAsync("owner", attempt.Id, CancellationToken.None);
		cancelled.Should().Be(new ChatGptSignInAttempt(attempt.Id, "cancelled"));
		fence.Verify(x => x.CompleteConnectionChangeAsync(generation, It.IsAny<CancellationToken>()), Times.Once);
		fence.Verify(x => x.BindConnectionChangeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
		using var released = ChatGptOperatorLease.Acquire(new() { HomeDirectory = home });
	}

	[Theory]
	[InlineData("device_expired", "expired")]
	[InlineData("device_rejected", "rejected")]
	[InlineData("provider-secret-text", "reconnect")]
	public async Task ProviderFailure_ReturnsSafeTerminalStateAndCompletesFence(string error, string state)
	{
		ChatGptSignInAttempt attempt = signIn.Start("owner", true);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		finish.SetException(new ChatGptRunnerException(error));
		await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await signIn.DisposeAsync();
		signIn.Get("owner", attempt.Id).Should().Be(new ChatGptSignInAttempt(attempt.Id, state));
		fence.Verify(x => x.CompleteConnectionChangeAsync(generation, It.IsAny<CancellationToken>()), Times.Once);
		fence.Verify(x => x.BindConnectionChangeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Success_RemovesOneTimeCodeAndDoesNotRunInference()
	{
		ChatGptSignInAttempt attempt = signIn.Start("owner", true);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		finish.SetResult();
		await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await signIn.DisposeAsync();
		signIn.Get("owner", attempt.Id).Should().Be(new ChatGptSignInAttempt(attempt.Id, "success"));
		fence.Verify(x => x.BindConnectionChangeAsync(generation, "owner", It.IsAny<CancellationToken>()), Times.Once);
		fence.Verify(x => x.CompleteConnectionChangeAsync(generation, It.IsAny<CancellationToken>()), Times.Never);
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task FailedFinalizationCannotReportSuccess()
	{
		var bindingAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fence.Setup(x => x.BindConnectionChangeAsync(generation, "owner", It.IsAny<CancellationToken>()))
			.Callback(() => bindingAttempted.TrySetResult())
			.ThrowsAsync(new InvalidOperationException("private failure"));
		ChatGptSignInAttempt attempt = signIn.Start("owner", true);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		finish.SetResult();
		await bindingAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await signIn.DisposeAsync();
		signIn.Get("owner", attempt.Id).State.Should().Be("reconnect");
		signIn.Get("owner", attempt.Id).UserCode.Should().BeNull();
	}

	[Fact]
	public void UnknownAttempt_RequiresRestartAndCannotBeInspectedByAnotherAdmin()
	{
		Guid unknown = Guid.NewGuid();
		signIn.Get("owner", unknown).Should().Be(new ChatGptSignInAttempt(unknown, "restart"));
		Action act = () => signIn.Get("another-admin", unknown);
		act.Should().Throw<NormalizationAiException>().Which.StatusCode.Should().Be(403);
	}

	public async ValueTask DisposeAsync()
	{
		await signIn.DisposeAsync();
		await services.DisposeAsync();
		if (Directory.Exists(home)) { Directory.Delete(home, recursive: true); }
	}
}
