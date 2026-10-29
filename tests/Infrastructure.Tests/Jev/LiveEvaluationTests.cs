using System.Net;
using System.Text.Json;
using Application.Classification;
using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

public class LiveEvaluationTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "jev-synthetic-" + Guid.NewGuid());
	private string Journal => Path.Combine(directory, "journal.jsonl");
	public LiveEvaluationTests() => Directory.CreateDirectory(directory);
	public void Dispose() => Directory.Delete(directory, recursive: true);
	private static OfflineCase Case(string id = "synthetic-1") => new(id,
			new("synthetic apple", "synthetic-v1", [new("food", "Food", "Edible goods", null)]));
	private static ReplayExchange Abstain() => new(JsonSerializer.Serialize(new
	{
		model = CategorySuggestionPolicy.Model,
		answers = new
		{
			fits = new { type = "noul", noul = 0.1 },
			category = new { type = "choice", choice = "food", confidence = 0.9, probabilities = new { food = 1.0 } }
		},
		usage = new { input_tokens = 100, output_tokens = 10 }
	}));

	[Fact]
	public void ApprovalUsesCumulativeAuthorizationWithoutClaimingRemainingCredit()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		// No balance evidence is supplied or fabricated. Prior carry is already allocated.
		const string json = """
		{"freezeSha256":"synthetic-freeze","expiresUtc":"2099-01-01T00:00:00Z","authorized":true,"pricingAndContextVerified":true,"priorCarryNanodollars":4750000000,"authorizedBudgetNanodollars":5000000000}
		""";
		LiveApproval approval = JsonSerializer.Deserialize<LiveApproval>(json, ProductionParity.JsonOptions)!;
		LiveCommand.ValidateApproval(approval, "synthetic-freeze", now);
		Assert.True(approval.AuthorizedBudgetNanodollars - approval.PriorCarryNanodollars < approval.PriorCarryNanodollars);
		Assert.Throws<InvalidOperationException>(() => LiveCommand.ValidateApproval(
			approval with { AuthorizedBudgetNanodollars = 132_120_576 }, "synthetic-freeze", now));
		Assert.Throws<InvalidOperationException>(() => LiveCommand.ValidateApproval(
			approval with { PriorCarryNanodollars = 0 }, "synthetic-freeze", now));
		Assert.Throws<InvalidOperationException>(() => LiveCommand.ValidateApproval(
			approval with { PricingAndContextVerified = false }, "synthetic-freeze", now));
		Assert.Throws<InvalidOperationException>(() => LiveCommand.ValidateApproval(
			approval with { Authorized = false }, "synthetic-freeze", now));
		Assert.Throws<InvalidOperationException>(() => LiveCommand.ValidateApproval(
			approval with { ExpiresUtc = now }, "synthetic-freeze", now));
		Assert.Throws<InvalidOperationException>(() => LiveCommand.ValidateApproval(approval, "changed-freeze", now));
	}

	[Fact]
	public async Task SuccessfulResumeReplaysWithoutDuplicateSends()
	{
		int calls = 0;
		async Task<ReplayExchange> Send(byte[] payload, CancellationToken ct)
		{
			Assert.NotEmpty(payload);
			// Intent must already exist and be exclusively locked before transport runs.
			Assert.Throws<IOException>(() => File.OpenRead(Journal));
			calls++; await Task.CompletedTask; return Abstain();
		}
		OfflineReplay first = await LiveEvaluation.RunAsync([Case()], Journal, "pin", Send, () => { });
		Assert.False(first.Stopped);
		OfflineReplay second = await LiveEvaluation.RunAsync([Case()], Journal, "pin", Send, () => { });
		Assert.False(second.Stopped);
		Assert.Equal(1, calls);
		Assert.Equal(OfflinePairedEvaluation.PerAttemptNanodollars, second.ReservationNanodollars);
		Assert.Contains("input_tokens", File.ReadAllText(Journal));
	}

	[Fact]
	public async Task AmbiguousSendIsNeverRetried()
	{
		int calls = 0;
		Task<ReplayExchange> Send(byte[] _, CancellationToken ct) { calls++; throw new HttpRequestException("synthetic failure"); }
		await Assert.ThrowsAsync<HttpRequestException>(() => LiveEvaluation.RunAsync([Case()], Journal, "pin", Send, () => { }));
		await Assert.ThrowsAsync<InvalidOperationException>(() => LiveEvaluation.RunAsync([Case()], Journal, "pin", Send, () => { }));
		Assert.Equal(1, calls);
		Assert.Contains("reservationNanodollars", File.ReadAllText(Journal));
	}

	[Theory]
	[InlineData(200, "{}")]
	[InlineData(429, "")]
	[InlineData(402, "{\"error\":\"insufficient credit\"}")]
	public async Task BadResponseStopsSuffixAndResume(int status, string body)
	{
		int calls = 0;
		Task<ReplayExchange> Send(byte[] _, CancellationToken ct) { calls++; return Task.FromResult(new ReplayExchange(body, status)); }
		OfflineCase[] cases = [Case(), Case("synthetic-2")];
		foreach (int _ in new[] { 1, 2 })
		{
			OfflineReplay result = await LiveEvaluation.RunAsync(cases, Journal, "pin", Send, () => { });
			Assert.True(result.Stopped);
			Assert.Equal("failed", result.Outcomes[0].Status);
			Assert.Equal("unattempted", result.Outcomes[1].Status);
			Assert.Equal(OfflinePairedEvaluation.PerAttemptNanodollars, result.ReservationNanodollars);
		}
		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task RevocationAndIdentityDriftCannotSend()
	{
		Task<ReplayExchange> Send(byte[] _, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Must not send");
		await Assert.ThrowsAsync<InvalidOperationException>(() => LiveEvaluation.RunAsync([Case()], Journal, "pin", Send,
				() => throw new InvalidOperationException("revoked")));
		Assert.DoesNotContain("intent", File.ReadAllText(Journal));
		await Assert.ThrowsAsync<InvalidOperationException>(() => LiveEvaluation.RunAsync([Case()], Journal, "changed", Send, () => { }));
	}

	[Fact]
	public async Task FullCohortUsesExactBoundBytesAndFrozenReservation()
	{
		OfflineCase[] cases = Enumerable.Range(0, 24).Select(i => Case("synthetic-" + i)).ToArray();
		int calls = 0;
		Task<ReplayExchange> Send(byte[] payload, CancellationToken ct)
		{
			Assert.Equal(Infrastructure.Jev.JevSuggestionQuestions.Serialize(cases[calls].Request), payload);
			calls++; return Task.FromResult(Abstain());
		}
		OfflineReplay result = await LiveEvaluation.RunAsync(cases, Journal, "pin", Send, () => { });
		Assert.Equal(24, result.CompletedCases);
		Assert.Equal(24, calls);
		Assert.Equal(4_750_000_000 + 24 * 2_752_512, result.CombinedReservationNanodollars);
	}

	[Fact]
	public async Task ConditionalSelectedCallsReachExactlyFortyEightAttempts()
	{
		OfflineCase[] cases = Enumerable.Range(0, 24).Select(i => Case("synthetic-" + i)).ToArray();
		int calls = 0;
		Task<ReplayExchange> Send(byte[] payload, CancellationToken ct)
		{
			using JsonDocument request = JsonDocument.Parse(payload);
			string[] keys = request.RootElement.GetProperty("questions").EnumerateObject().Select(p => p.Name).ToArray();
			Dictionary<string, object> answers = [];
			foreach (string key in keys)
			{
				answers[key] = key == "category"
						? new { type = "choice", choice = "food", confidence = 0.9, probabilities = new { food = 1.0 } }
						: (object)new { type = "noul", noul = 0.9 };
			}

			calls++;
			return Task.FromResult(new ReplayExchange(JsonSerializer.Serialize(new
			{
				model = CategorySuggestionPolicy.Model,
				answers,
				usage = new { input_tokens = 100, output_tokens = 10 }
			})));
		}
		OfflineReplay result = await LiveEvaluation.RunAsync(cases, Journal, "pin", Send, () => { });
		Assert.Equal(24, result.CompletedCases);
		Assert.Equal(48, calls);
		Assert.Equal(132_120_576, result.ReservationNanodollars);
		Assert.Equal(4_882_120_576, result.CombinedReservationNanodollars);
		await LiveEvaluation.RunAsync(cases, Journal, "pin", Send, () => { });
		Assert.Equal(48, calls);
	}

	[Fact]
	public async Task ExcessReportedUsageStopsBeforeAnySuffixSend()
	{
		int calls = 0;
		Task<ReplayExchange> Send(byte[] _, CancellationToken ct)
		{
			calls++;
			return Task.FromResult(Abstain() with { Body = Abstain().Body.Replace("100", "65537") });
		}
		await Assert.ThrowsAsync<InvalidOperationException>(() => LiveEvaluation.RunAsync([Case(), Case("synthetic-2")], Journal, "pin", Send, () => { }));
		await Assert.ThrowsAsync<InvalidOperationException>(() => LiveEvaluation.RunAsync([Case(), Case("synthetic-2")], Journal, "pin", Send, () => { }));
		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task RestartWithDifferentCredentialCannotUseSameJournal()
	{
		const string firstKey = "synthetic-credential-one";
		const string secondKey = "synthetic-credential-two";
		int calls = 0;
		Task<ReplayExchange> Send(byte[] _, CancellationToken ct) { calls++; return Task.FromResult(Abstain()); }
		await LiveEvaluation.RunAsync([Case()], Journal, LiveCommand.BindCredential("same-approval", firstKey), Send, () => { });
		await LiveEvaluation.RunAsync([Case()], Journal, LiveCommand.BindCredential("same-approval", firstKey), Send, () => { });
		await Assert.ThrowsAsync<InvalidOperationException>(() => LiveEvaluation.RunAsync([Case()], Journal,
			LiveCommand.BindCredential("same-approval", secondKey), Send, () => { }));
		Assert.Equal(1, calls);
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
			await LiveEvaluation.RunAsync([Case()], Journal, "pin", Send, () => { }).WaitAsync(TimeSpan.FromSeconds(20)));
		Assert.True(handler.Content.BodyReadStarted);
		Assert.True(handler.Content.BodyReadCancelled);
		await Assert.ThrowsAsync<InvalidOperationException>(() => LiveEvaluation.RunAsync([Case()], Journal, "pin", Send, () => { }));
		Assert.Equal(1, handler.Calls);
		string journal = File.ReadAllText(Journal);
		Assert.Contains("reservationNanodollars", journal);
		Assert.DoesNotContain("\"kind\":\"response\"", journal);
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
