using System.Net;
using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

/// <summary>The shared journalled loop and live transport, driven by a synthetic round-4 packet. Nothing reaches a provider.</summary>
public sealed class JournalledRunTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "jev-journal-" + Guid.NewGuid().ToString("N"));
	private string Journal => Path.Combine(directory, "journal.jsonl");

	public JournalledRunTests() => Directory.CreateDirectory(directory);
	public void Dispose() => Directory.Delete(directory, recursive: true);

	private static TwoStageProtocol Protocol(int cases = 2) => new(SyntheticPacket.Create(cases));

	[Fact]
	public async Task AmbiguousSendIsNeverRetried()
	{
		int calls = 0;
		Task<ReplayExchange> Send(byte[] _, CancellationToken ct) { calls++; throw new HttpRequestException("synthetic failure"); }

		await Assert.ThrowsAsync<HttpRequestException>(() => JournalledRun.RunAsync(Protocol(), Journal, "pin", Send, () => { }));
		InvalidOperationException resume = await Assert.ThrowsAsync<InvalidOperationException>(
			() => JournalledRun.RunAsync(Protocol(), Journal, "pin", Send, () => { }));

		Assert.Equal("Ambiguous intent; no resend.", resume.Message);
		Assert.Equal(1, calls);
		Assert.Contains("reservationNanodollars", File.ReadAllText(Journal));
	}

	[Theory]
	[InlineData(200, "{}")]
	[InlineData(429, "")]
	[InlineData(402, "{\"error\":\"insufficient credit\"}")]
	public async Task BadResponseStopsTheSuffixAndResume(int status, string body)
	{
		int calls = 0;
		Task<ReplayExchange> Send(byte[] _, CancellationToken ct) { calls++; return Task.FromResult(new ReplayExchange(body, status)); }

		foreach (int _ in new[] { 1, 2 })
		{
			TwoStageReplay result = await JournalledRun.RunAsync(Protocol(), Journal, "pin", Send, () => { });
			Assert.True(result.Stopped);
			Assert.Equal(["failed", "unattempted"], result.Outcomes.Select(o => o.Status));
			Assert.Single(result.Attempts);
		}

		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task RevocationAndIdentityDriftCannotSend()
	{
		Task<ReplayExchange> Send(byte[] _, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Must not send");

		await Assert.ThrowsAsync<InvalidOperationException>(() => JournalledRun.RunAsync(Protocol(), Journal, "pin", Send,
			() => throw new InvalidOperationException("revoked")));
		Assert.DoesNotContain("intent", File.ReadAllText(Journal));
		await Assert.ThrowsAsync<InvalidOperationException>(() => JournalledRun.RunAsync(Protocol(), Journal, "changed", Send, () => { }));
	}

	[Fact]
	public async Task RestartWithDifferentCredentialCannotUseSameJournal()
	{
		const string firstKey = "synthetic-credential-one";
		const string secondKey = "synthetic-credential-two";
		int calls = 0;
		Task<ReplayExchange> Send(byte[] payload, CancellationToken ct) { calls++; return Task.FromResult(SyntheticAnswers.For(payload)); }

		await JournalledRun.RunAsync(Protocol(1), Journal, LiveCommand.BindCredential("same-approval", firstKey), Send, () => { });
		await JournalledRun.RunAsync(Protocol(1), Journal, LiveCommand.BindCredential("same-approval", firstKey), Send, () => { });
		await Assert.ThrowsAsync<InvalidOperationException>(() => JournalledRun.RunAsync(Protocol(1), Journal,
			LiveCommand.BindCredential("same-approval", secondKey), Send, () => { }));

		Assert.Equal(2, calls);
		string journal = File.ReadAllText(Journal);
		Assert.DoesNotContain(firstKey, journal);
		Assert.DoesNotContain(secondKey, journal);
	}

	[Fact]
	public async Task StalledResponseBodyHitsDeadlineAndLeavesIntentWithoutRetry()
	{
		using StalledBodyHandler handler = new();
		using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
		Task<ReplayExchange> Send(byte[] payload, CancellationToken ct) => LiveCommand.SendAsync(client, payload, "synthetic-credential", ct);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
			await JournalledRun.RunAsync(Protocol(), Journal, "pin", Send, () => { }).WaitAsync(TimeSpan.FromSeconds(20)));
		Assert.True(handler.Content.BodyReadStarted);
		Assert.True(handler.Content.BodyReadCancelled);
		await Assert.ThrowsAsync<InvalidOperationException>(() => JournalledRun.RunAsync(Protocol(), Journal, "pin", Send, () => { }));

		Assert.Equal(1, handler.Calls);
		string journal = File.ReadAllText(Journal);
		Assert.Contains("reservationNanodollars", journal);
		Assert.DoesNotContain("\"kind\":\"response\"", journal);
	}

	[Fact]
	public async Task SendAsyncClosesTheConnectionAfterEachRequest()
	{
		using CapturingHandler handler = new();
		using HttpClient client = new(handler);

		ReplayExchange exchange = await LiveCommand.SendAsync(client, "{}"u8.ToArray(), "synthetic-credential", CancellationToken.None);

		Assert.Equal(1, handler.Calls);
		Assert.True(handler.ConnectionClose);
		Assert.Equal("Bearer synthetic-credential", handler.Authorization);
		Assert.Equal("{\"ok\":true}", exchange.Body);
	}

	[Fact]
	public void JournalStateParsesHeaderExchangesAndOutstandingIntent()
	{
		const string header = """{"kind":"header","identity":"pin"}""";
		const string intent = """{"kind":"intent","index":0,"requestSha256":"aa"}""";
		const string response = """{"kind":"response","index":0,"requestSha256":"aa","body":"{}","statusCode":402}""";

		LiveJournalState empty = LiveJournalState.Parse("");
		Assert.Null(empty.Identity);
		Assert.Empty(empty.Exchanges);
		Assert.False(empty.OutstandingIntent);
		LiveJournalState complete = LiveJournalState.Parse(header + "\n" + intent + "\n" + response + "\n");
		Assert.Equal("pin", complete.Identity);
		Assert.False(complete.OutstandingIntent);
		Assert.Equal(new ReplayExchange("{}", 402, "aa"), Assert.Single(complete.Exchanges));
		LiveJournalState outstanding = LiveJournalState.Parse(header + "\n" + intent + "\n");
		Assert.True(outstanding.OutstandingIntent);
		Assert.Empty(outstanding.Exchanges);
	}

	[Theory]
	[InlineData("""{"kind":"header","identity":"pin"}""")]
	[InlineData("""{"kind":"intent","index":0,"requestSha256":"aa"}""" + "\n")]
	[InlineData("""{"kind":"header","identity":"pin"}""" + "\n" + """{"kind":"intent","index":1,"requestSha256":"aa"}""" + "\n")]
	[InlineData("""{"kind":"header","identity":"pin"}""" + "\n" + """{"kind":"intent","index":0,"requestSha256":"aa"}""" + "\n" +
		"""{"kind":"response","index":0,"requestSha256":"bb","body":"{}","statusCode":200}""" + "\n")]
	public void IncompleteOrUnboundJournalIsRejected(string text) =>
		Assert.Throws<InvalidOperationException>(() => LiveJournalState.Parse(text));

	private sealed class CapturingHandler : HttpMessageHandler
	{
		public int Calls { get; private set; }
		public bool? ConnectionClose { get; private set; }
		public string? Authorization { get; private set; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			// SendAsync disposes its request, so capture the headers here rather than the message.
			Calls++;
			ConnectionClose = request.Headers.ConnectionClose;
			Authorization = request.Headers.Authorization?.ToString();
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
		}
	}

	private sealed class StalledBodyHandler : HttpMessageHandler
	{
		public int Calls { get; private set; }
		public StalledContent Content { get; } = new();
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Calls++;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Content });
		}
	}

	private sealed class StalledContent : HttpContent
	{
		public bool BodyReadStarted { get; private set; }
		public bool BodyReadCancelled { get; private set; }
		protected override bool TryComputeLength(out long length) { length = 0; return false; }
		protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
			SerializeToStreamAsync(stream, context, CancellationToken.None);
		protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
		{
			BodyReadStarted = true;
			try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
			catch (OperationCanceledException) { BodyReadCancelled = true; throw; }
		}
	}
}
