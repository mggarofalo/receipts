using System.Diagnostics;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.NormalizationAi;
using FluentAssertions;
using Infrastructure.Entities.NormalizationAi;
using Infrastructure.NormalizationAi;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Infrastructure.Tests.NormalizationAi;

public sealed class ChatGptConnectionRecoveryTests : IDisposable
{
	private readonly string home = Path.Combine(Path.GetTempPath(), "receipts-recovery-" + Guid.NewGuid().ToString("N"));
	private readonly Failure failure = new();
	private readonly Factory factory;
	private readonly Mock<IChatGptRunner> runner = new(MockBehavior.Strict);
	private PiNormalizationRunnerOptions RunnerOptions => new() { HomeDirectory = home };
	public ChatGptConnectionRecoveryTests()
	{
		Directory.CreateDirectory(home);
		factory = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(failure).Options);
	}
	private NormalizationAiService Service(string owner = "owner") => new(factory, runner.Object,
		Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = owner }), TimeProvider.System,
		Mock.Of<ICommittedChangePublisher>(), runnerOptions: Options.Create(RunnerOptions));

	[Fact]
	public async Task RestartRotatesFenceCancelsWorkAndNeverActivatesOldBinding()
	{
		Guid change = await Service().BeginConnectionChangeAsync(default);
		var binding = new ChatGptCredentialBinding(RunnerOptions);
		binding.Publish("owner", change, DateTimeOffset.UtcNow);
		await using (var db = factory.CreateDbContext())
		{
			db.NormalizationAssistancePreferences.Add(new() { Enabled = true });
			db.NormalizationAiRuns.Add(new() { OwnerUserId = "owner", State = "queued", Model = "fake", ClientRequestId = Guid.NewGuid(), RequestHash = "fake" });
			await db.SaveChangesAsync();
		}
		(await Service("changed-owner").RecoverConnectionChangeAsync(default)).Should().BeTrue();
		await using (var db = factory.CreateDbContext())
		{
			var current = await db.NormalizationAiConnection.SingleAsync();
			current.Changing.Should().BeFalse(); current.Generation.Should().NotBe(change);
			(await db.NormalizationAssistancePreferences.SingleAsync()).Enabled.Should().BeFalse();
			(await db.NormalizationAiRuns.SingleAsync()).State.Should().Be("cancelled");
			current.Generation = change; await db.SaveChangesAsync();
		}
		binding.Exists.Should().BeFalse();
		(await Service().GetConnectionAsync("owner", default)).CredentialsPresent.Should().BeFalse("restored old generations cannot restore volume consent");
		runner.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task AnotherOperatorAndParentRunnerLeaseBothKeepFenceBusy()
	{
		Guid change = await Service().BeginConnectionChangeAsync(default);
		using (ChatGptOperatorLease.Acquire(RunnerOptions))
		{
			(await Service().RecoverConnectionChangeAsync(default)).Should().BeFalse();
			(await Service().GetConnectionAsync("owner", default)).Busy.Should().BeTrue();
		}
		using (new FileStream(Path.Combine(home, ".runner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
		{ (await Service().RecoverConnectionChangeAsync(default)).Should().BeFalse(); }
		await using var db = factory.CreateDbContext();
		(await db.NormalizationAiConnection.SingleAsync()).Generation.Should().Be(change);
		(await Service().RecoverConnectionChangeAsync(default)).Should().BeTrue();
	}

	[Fact]
	public async Task ActualOrphanNodeBlocksRecoveryUntilItsProcessExits()
	{
		await Service().BeginConnectionChangeAsync(default);
		string root = Directory.GetCurrentDirectory();
		while (!File.Exists(Path.Combine(root, "runner", "lifetime-lock.mjs"))) { root = Directory.GetParent(root)!.FullName; }
		string permit = Guid.NewGuid().ToString("N");
		File.WriteAllText(Path.Combine(home, ".permit-" + permit), "");
		string module = new Uri(Path.Combine(root, "runner", "lifetime-lock.mjs")).AbsoluteUri;
		var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
		start.ArgumentList.Add("--input-type=module"); start.ArgumentList.Add("-e");
		start.ArgumentList.Add($"import {{ acquireLifetimeLock }} from '{module}'; acquireLifetimeLock(process.argv[1], process.argv[2]); console.log('locked'); setInterval(()=>{{}},1000);");
		start.ArgumentList.Add(home); start.ArgumentList.Add(permit);
		using Process child = Process.Start(start)!;
		try
		{
			(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("locked");
			(await Service().RecoverConnectionChangeAsync(default)).Should().BeFalse("the API operator lease has gone but its child is alive");
		}
		finally { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
		(await Service().RecoverConnectionChangeAsync(default)).Should().BeTrue("OS-confirmed child exit permits safe reconciliation");
		File.Exists(Path.Combine(home, ".permit-" + permit)).Should().BeFalse("a delayed orphan may never reuse an old launch permit");
	}

	[Fact]
	public async Task UnknownChildIdentityFailsClosedWithoutAgeBasedStealing()
	{
		await Service().BeginConnectionChangeAsync(default);
		string path = Path.Combine(home, ".node.lock");
		File.WriteAllText(path, ""); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddYears(-1));
		(await Service().RecoverConnectionChangeAsync(default)).Should().BeFalse();
		File.Exists(path).Should().BeTrue();
	}

	[Fact]
	public async Task ObservedStaleGenerationCannotClearANewerChange()
	{
		await Service().BeginConnectionChangeAsync(default);
		Guid newer = Guid.NewGuid();
		int calls = 0;
		factory.Creating = () =>
		{
			if (++calls != 2) { return; }
			using var db = new ApplicationDbContext(factory.Options);
			var row = db.NormalizationAiConnection.Single(); row.Generation = newer; db.SaveChanges();
		};
		(await Service().RecoverConnectionChangeAsync(default)).Should().BeFalse();
		factory.Creating = null;
		await using var check = factory.CreateDbContext();
		var current = await check.NormalizationAiConnection.SingleAsync();
		current.Generation.Should().Be(newer); current.Changing.Should().BeTrue();
	}

	[Fact]
	public async Task FailedFinalizationInSameHostIsRepairedByAwaitedStatusGate()
	{
		var service = Service();
		Guid change = await service.BeginConnectionChangeAsync(default);
		failure.FailNext = true;
		Func<Task> finish = () => service.BindConnectionChangeAsync(change, "owner", default);
		await finish.Should().ThrowAsync<InvalidOperationException>();
		await using (var db = factory.CreateDbContext()) { (await db.NormalizationAiConnection.SingleAsync()).Changing.Should().BeTrue(); }
		(await service.GetConnectionAsync("owner", default)).CredentialsPresent.Should().BeFalse();
		await using (var db = factory.CreateDbContext()) { (await db.NormalizationAiConnection.SingleAsync()).Changing.Should().BeFalse(); }
		new ChatGptCredentialBinding(RunnerOptions).Exists.Should().BeFalse(); runner.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task HostedRetryRepairsStillRunningHostAndShutdownStopsFurtherWork()
	{
		var clock = new FakeTimeProvider();
		var service = new Mock<INormalizationAiService>();
		var repaired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int calls = 0;
		service.Setup(x => x.RecoverConnectionChangeAsync(It.IsAny<CancellationToken>())).Returns(() =>
		{
			if (Interlocked.Increment(ref calls) == 2) { repaired.TrySetResult(); }
			return Task.FromResult(calls > 1);
		});
		await using var provider = new ServiceCollection().AddScoped(_ => service.Object).BuildServiceProvider();
		using var worker = new ChatGptConnectionRecoveryWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger<ChatGptConnectionRecoveryWorker>.Instance);
		await worker.StartAsync(default);
		while (Volatile.Read(ref calls) == 0) { await Task.Yield(); }
		clock.Advance(TimeSpan.FromSeconds(10));
		await repaired.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await worker.StopAsync(default);
		int stoppedCalls = calls; clock.Advance(TimeSpan.FromMinutes(1));
		calls.Should().Be(stoppedCalls);
	}

	[Fact]
	public async Task RecoveryWriteFailureRemainsUnavailableAndCanBeRetried()
	{
		Guid change = await Service().BeginConnectionChangeAsync(default);
		failure.FailNext = true;
		Func<Task> recover = () => Service().RecoverConnectionChangeAsync(default);
		await recover.Should().ThrowAsync<InvalidOperationException>();
		await using (var db = factory.CreateDbContext())
		{
			var current = await db.NormalizationAiConnection.SingleAsync();
			current.Changing.Should().BeTrue(); current.Generation.Should().Be(change);
		}
		(await Service().RecoverConnectionChangeAsync(default)).Should().BeTrue();
		runner.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task ActiveCoordinatorRetainsItsFenceUntilShutdownFinalizes()
	{
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		runner.Setup(x => x.LoginAsync(It.IsAny<Action<ChatGptDeviceCode>>(), It.IsAny<CancellationToken>()))
			.Returns(async (Action<ChatGptDeviceCode> _, CancellationToken token) =>
			{
				started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
			});
		await using var provider = new ServiceCollection().AddScoped<INormalizationAiService>(_ => Service()).BuildServiceProvider();
		await using var signIn = new ChatGptDeviceSignIn(runner.Object, provider.GetRequiredService<IServiceScopeFactory>(),
			Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = "owner" }), Options.Create(RunnerOptions), TimeProvider.System);
		var attempt = signIn.Start("owner", true);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		(await Service().RecoverConnectionChangeAsync(default)).Should().BeFalse();
		await signIn.DisposeAsync();
		signIn.Get("owner", attempt.Id).State.Should().Be("cancelled");
		await using var db = factory.CreateDbContext();
		(await db.NormalizationAiConnection.SingleAsync()).Changing.Should().BeFalse();
		new ChatGptCredentialBinding(RunnerOptions).Exists.Should().BeFalse();
	}

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public DbContextOptions<ApplicationDbContext> Options => options;
		public Action? Creating { get; set; }
		public ApplicationDbContext CreateDbContext() { Creating?.Invoke(); return new(options); }
	}
	private sealed class Failure : SaveChangesInterceptor
	{
		public bool FailNext { get; set; }
		public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
		{
			if (FailNext) { FailNext = false; throw new InvalidOperationException("synthetic finalization failure"); }
			return base.SavingChangesAsync(eventData, result, cancellationToken);
		}
	}
	public void Dispose() { Directory.Delete(home, recursive: true); }
}
