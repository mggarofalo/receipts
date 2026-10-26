using System.Diagnostics;
using System.Text.Json;
using Application.NormalizationAi;
using FluentAssertions;
using Infrastructure.NormalizationAi;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.NormalizationAi;

public sealed class PiNormalizationRunnerTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "receipts-runner-tests-" + Guid.NewGuid().ToString("N"));

	[Fact]
	public async Task InferAsync_PassesExplicitInputAndReturnsUsageWithoutInheritedSecrets()
	{
		const string secretName = "RECEIPTS_RUNNER_TEST_SECRET";
		Environment.SetEnvironmentVariable(secretName, "test-only-secret");
		try
		{
			var runner = Create("""
                let input = ''; for await (const chunk of process.stdin) input += chunk;
                const request = JSON.parse(input);
                console.log(JSON.stringify({type:'result',text:JSON.stringify({
                  request, secret: process.env.RECEIPTS_RUNNER_TEST_SECRET ?? null,
                  cwd:process.cwd(),home:process.env.HOME,authHome:process.env.RECEIPTS_CHATGPT_HOME
                }),usage:{input:4,output:2,cacheRead:0,cacheWrite:0}}));
                """);
			var result = await runner.InferAsync(Request(), CancellationToken.None);
			using var document = JsonDocument.Parse(result.Text);
			var root = document.RootElement;
			root.GetProperty("secret").ValueKind.Should().Be(JsonValueKind.Null);
			root.GetProperty("request").GetProperty("prompt").GetString().Should().Be("untrusted item text");
			root.GetProperty("cwd").GetString().Should().Be(root.GetProperty("home").GetString());
			root.GetProperty("cwd").GetString().Should().NotBe(root.GetProperty("authHome").GetString());
			result.Usage.Should().Be(new ChatGptUsage(4, 2, 0, 0));
			Directory.EnumerateDirectories(Path.Combine(directory, "home", "work")).Should().BeEmpty();
		}
		finally { Environment.SetEnvironmentVariable(secretName, null); }
	}

	[Theory]
	[InlineData("process.stdout.write('{\"type\":\"result\"');", "protocol_invalid")]
	[InlineData("console.log('not json');", "protocol_invalid")]
	[InlineData("console.log(JSON.stringify({type:'result',text:'{}'}));console.log(JSON.stringify({type:'result',text:'{}'}));", "protocol_invalid")]
	[InlineData("console.log(JSON.stringify({type:'result',text:'{}'}));console.log(JSON.stringify({type:'ready'}));", "protocol_invalid")]
	[InlineData("console.log(JSON.stringify({type:'result',text:'{}',usage:{input:-1,output:0,cacheRead:0,cacheWrite:0}}));", "protocol_invalid")]
	[InlineData("console.log(JSON.stringify({type:'error',code:'secret-token-from-provider'}));process.exitCode=21;", "request_failed")]
	[InlineData("console.error('secret raw failure');process.exitCode=9;", "request_failed")]
	[InlineData("process.stdout.write('x'.repeat(2*1024*1024+1));", "output_limit")]
	public async Task InferAsync_RejectsMalformedOrFailedProtocolWithoutLeakingDetails(string script, string code)
	{
		// The real bridge consumes stdin before returning a protocol response.
		var runner = Create("for await (const chunk of process.stdin) {}\n" + script);
		Func<Task> action = () => runner.InferAsync(Request(), CancellationToken.None);
		var exception = (await action.Should().ThrowAsync<ChatGptRunnerException>()).Which;
		exception.Code.Should().Be(code);
		exception.Message.Should().Be(code);
	}

	[Theory]
	[InlineData("console.log(JSON.stringify({type:'ready'}));", true)]
	[InlineData("console.log(JSON.stringify({type:'auth_required'}));", false)]
	public async Task GetStatusAsync_RequiresAnExplicitCredentialState(string script, bool connected)
		=> (await Create(script).GetStatusAsync(CancellationToken.None)).CredentialsPresent.Should().Be(connected);

	[Fact]
	public async Task GetStatusAsync_RejectsEmptySuccess()
	{
		Func<Task> action = () => Create("").GetStatusAsync(CancellationToken.None);
		(await action.Should().ThrowAsync<ChatGptRunnerException>()).Which.Code.Should().Be("protocol_invalid");
	}

	[Fact]
	public async Task Cancellation_KillsLiveProcessAndReleasesCrossInstanceCredentialLock()
	{
		var runner = Create("""
            if(process.argv[2] === 'login') {
              console.log(JSON.stringify({type:'device_code',url:'https://auth.openai.com/codex/device',code:String(process.pid)}));
              setInterval(()=>{},1000);
            } else console.log(JSON.stringify({type:'ready'}));
            """);
		using var cancellation = new CancellationTokenSource();
		TaskCompletionSource<int> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var login = runner.LoginAsync(code => started.TrySetResult(int.Parse(code.Code)), cancellation.Token);
		int pid = await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
		try
		{
			var competing = new PiNormalizationRunner(Options.Create(Settings()));
			var concurrent = await competing.GetStatusAsync(CancellationToken.None);
			concurrent.Should().Be(new ChatGptRunnerStatus(false, true));
		}
		finally { cancellation.Cancel(); }
		Func<Task> cancelled = () => login;
		await cancelled.Should().ThrowAsync<OperationCanceledException>();
		bool alive;
		try { using var process = Process.GetProcessById(pid); alive = !process.HasExited; }
		catch (ArgumentException) { alive = false; }
		alive.Should().BeFalse("cancelling the operation must terminate its child before returning");
		(await runner.GetStatusAsync(CancellationToken.None)).CredentialsPresent.Should().BeTrue();
	}

	[Fact]
	public async Task Login_ForwardsBoundedDeviceExpiry()
	{
		var runner = Create("console.log(JSON.stringify({type:'device_code',url:'https://auth.openai.com/codex/device',code:'ABCD-1234',expiresInSeconds:900}));console.log(JSON.stringify({type:'ready'}));");
		ChatGptDeviceCode? shown = null;
		await runner.LoginAsync(code => shown = code, CancellationToken.None);
		shown.Should().Be(new ChatGptDeviceCode("https://auth.openai.com/codex/device", "ABCD-1234", 900));
	}

	[Theory]
	[InlineData("0")]
	[InlineData("1201")]
	[InlineData("1.5")]
	public async Task Login_RejectsInvalidDeviceExpiry(string expiry)
	{
		var runner = Create("console.log(JSON.stringify({type:'device_code',url:'https://auth.openai.com/codex/device',code:'ABCD-1234',expiresInSeconds:" + expiry + "}));console.log(JSON.stringify({type:'ready'}));");
		bool shown = false;
		Func<Task> login = () => runner.LoginAsync(_ => shown = true, CancellationToken.None);
		(await login.Should().ThrowAsync<ChatGptRunnerException>()).Which.Code.Should().Be("invalid_device_code");
		shown.Should().BeFalse();
	}

	[Fact]
	public async Task Login_RejectsSpoofedDeviceUrlBeforeExposingItToOperator()
	{
		var runner = Create("console.log(JSON.stringify({type:'device_code',url:'https://auth.openai.com.evil.test/codex/device',code:'ABCD-1234'})); console.log(JSON.stringify({type:'result'}));");
		bool shown = false;
		Func<Task> login = () => runner.LoginAsync(_ => shown = true, CancellationToken.None);
		(await login.Should().ThrowAsync<ChatGptRunnerException>()).Which.Code.Should().Be("invalid_device_code");
		shown.Should().BeFalse();
	}

	[Fact]
	public async Task OperatorLease_ExcludesOtherOperatorsButAllowsRunnerAndReleasesOnDispose()
	{
		var runner = Create("console.log(JSON.stringify({type:'ready'}));");
		using (ChatGptOperatorLease.Acquire(Settings()))
		{
			Action second = () => ChatGptOperatorLease.Acquire(Settings());
			second.Should().Throw<ChatGptRunnerException>().Which.Code.Should().Be("operator_busy");
			(await runner.GetStatusAsync(CancellationToken.None)).CredentialsPresent.Should().BeTrue();
		}
		using var next = ChatGptOperatorLease.Acquire(Settings());
	}

	[Fact]
	public async Task AdmissionRejectsOversizedInputBeforeStartingAChild()
	{
		var runner = Create("throw new Error('must not start');");
		var request = Request() with { Prompt = new string('界', 100000) };
		Func<Task> action = () => runner.InferAsync(request, CancellationToken.None);
		(await action.Should().ThrowAsync<ChatGptRunnerException>()).Which.Code.Should().Be("input_limit");
		Directory.Exists(Path.Combine(directory, "home")).Should().BeFalse();
	}

	[Fact]
	public async Task InferAsync_LockedCredentialHomeIsKnownNotDispatched()
	{
		var runner = Create("throw new Error('must not start');");
		Directory.CreateDirectory(Settings().HomeDirectory);
		using FileStream held = new(Path.Combine(Settings().HomeDirectory, ".runner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		Func<Task> action = () => runner.InferAsync(Request(), CancellationToken.None);
		var exception = (await action.Should().ThrowAsync<ChatGptRunnerException>()).Which;
		exception.Code.Should().Be("busy");
		exception.RequestMayHaveStarted.Should().BeFalse();
		(await runner.GetStatusAsync(CancellationToken.None)).Busy.Should().BeTrue();
	}

	[Fact]
	public async Task InferAsync_InvalidHomeIsSetupFailureWithoutDispatch()
	{
		var runner = Create("throw new Error('must not start');");
		File.WriteAllText(Settings().HomeDirectory, "not a directory");
		Func<Task> action = () => runner.InferAsync(Request(), CancellationToken.None);
		var exception = (await action.Should().ThrowAsync<ChatGptRunnerException>()).Which;
		exception.Code.Should().Be("runner_setup_failed");
		exception.RequestMayHaveStarted.Should().BeFalse();
	}

	[Fact]
	public async Task InferAsync_ProtocolFailureAfterDispatchRemainsUncertain()
	{
		var runner = Create("for await (const chunk of process.stdin) {} console.log('invalid');");
		Func<Task> action = () => runner.InferAsync(Request(), CancellationToken.None);
		(await action.Should().ThrowAsync<ChatGptRunnerException>()).Which.RequestMayHaveStarted.Should().BeTrue();
	}

	private PiNormalizationRunner Create(string script)
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "fake.mjs"), script);
		return new(Options.Create(Settings()));
	}

	private PiNormalizationRunnerOptions Settings() => new()
	{
		HomeDirectory = Path.Combine(directory, "home"),
		BridgePath = Path.Combine(directory, "fake.mjs"),
		NodeExecutable = "node",
		TimeoutSeconds = 15,
	};

	private static ChatGptInferenceRequest Request() => new("test-model", "instructions", "untrusted item text", JsonSerializer.SerializeToElement(new { type = "object" }));

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}
}
