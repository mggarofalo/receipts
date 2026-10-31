using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Receipts.JevProductionParity;

public sealed record LiveApproval(string FreezeSha256, DateTimeOffset ExpiresUtc,
		bool Authorized, bool PricingAndContextVerified, long PriorCarryNanodollars,
		long AuthorizedBudgetNanodollars);

public static class LiveCommand
{
	private const string SendFlag = "--send-authorized";
	private const string DryRunFlag = "--dry-run";
	private const string UserSecretsFlag = "--api-user-secrets";

	/// <summary>
	/// Budget-only form: the approval must restate the frozen carry and ceiling, and the carry plus the
	/// packet's maximum new reservation (including any headroom) must fit within it.
	/// </summary>
	public static string? ApprovalProblem(LiveApproval? approval, string freezeHash, long priorCarryNanodollars,
		long ceilingNanodollars, long maximumReservationNanodollars, DateTimeOffset now) =>
		// Authorization is a cumulative spending ceiling, never an account balance assertion.
		approval is null ? "missing"
			: !approval.Authorized ? "not-authorized"
			: !approval.PricingAndContextVerified ? "pricing-unverified"
			: approval.FreezeSha256 != freezeHash ? "freeze-mismatch"
			: approval.ExpiresUtc <= now ? "expired"
			: approval.PriorCarryNanodollars != priorCarryNanodollars ||
				approval.AuthorizedBudgetNanodollars != ceilingNanodollars ||
				checked(approval.PriorCarryNanodollars + maximumReservationNanodollars) > approval.AuthorizedBudgetNanodollars
				? "budget-mismatch"
			: null;

	// Persist only a one-way fingerprint, never the credential. Rotation requires investigation,
	// even when the replacement key might belong to the same provider account.
	public static string BindCredential(string identity, string key) => identity + ":" +
		Hashing.Hash(Encoding.UTF8.GetBytes("receipts-live-credential-v1\0" + key));

	public static async Task<ReplayExchange> SendAsync(HttpClient client, byte[] payload, string key, CancellationToken ct)
	{
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(TimeSpan.FromSeconds(10));
		using HttpRequestMessage request = new(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
		// A fresh connection per request: .NET may transparently resend a request whose reused
		// pooled connection closed before any response bytes, which would be an unjournaled send.
		request.Headers.ConnectionClose = true;
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
		request.Content = new ByteArrayContent(payload);
		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
		await response.Content.LoadIntoBufferAsync(1_048_576, deadline.Token);
		return new(await response.Content.ReadAsStringAsync(deadline.Token), (int)response.StatusCode);
	}

	public static Task<int> RunAsync(string[] args) =>
		RunAsync(args, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows());

	public static async Task<int> RunAsync(string[] args, Func<string, string?> environment, bool isWindows)
	{
		try
		{
			if (args is not ["live-run", var packet, var repository, var approvalPath, var journalPath, var outputPath, .. var flags] ||
				flags.Length is < 1 or > 2 || flags.Distinct(StringComparer.Ordinal).Count() != flags.Length ||
				flags.Any(f => f is not (SendFlag or DryRunFlag or UserSecretsFlag)) ||
				flags.Contains(SendFlag) == flags.Contains(DryRunFlag))
			{
				throw new ArgumentException("Invalid arguments.");
			}

			bool dryRun = flags.Contains(DryRunFlag);
			bool apiUserSecrets = flags.Contains(UserSecretsFlag);
			// Evidence outputs must live outside the checkout; never overwrite artifacts.
			PrivateEvidence.RequireOutside(repository, packet, approvalPath, journalPath, outputPath);

			if (File.Exists(outputPath))
			{
				throw new IOException("Output exists.");
			}

			if (!string.Equals(Path.GetFullPath(journalPath), Path.GetFullPath(Path.Combine(packet, "live.journal.jsonl")), StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException("Use the single packet journal.");
			}

			// Round 4 is the only live protocol; earlier rounds were bound to a retired production policy.
			if (OfflineCommand.PacketKindOf(packet) != TwoStagePolicy.PacketKind)
			{
				throw new ArgumentException("Unsupported packet.");
			}

			return await RunTwoStageAsync(packet, repository, approvalPath, journalPath, outputPath, dryRun, apiUserSecrets,
				environment, isWindows);
		}
		catch (Exception)
		{
			// Do not echo exceptions: HTTP failures can contain payloads or credential material.
			Console.Error.WriteLine("Evaluation stopped. No automatic retries. Preserve and inspect the private journal.");
			return 2;
		}
	}

	/// <summary>Round 4: the same credential, approval, journal and transport safeguards around the two-stage replay.</summary>
	private static async Task<int> RunTwoStageAsync(string packet, string repository, string approvalPath, string journalPath,
		string outputPath, bool dryRun, bool apiUserSecrets, Func<string, string?> environment, bool isWindows)
	{
		TwoStagePacket twoStage = TwoStagePacket.Load(packet);
		string freezeHash = Hashing.Hash(File.ReadAllBytes(Path.Combine(packet, "freeze.private.json")));
		string? key = ApiCredential.Resolve(repository, apiUserSecrets, environment, isWindows);
		if (dryRun)
		{
			await TwoStageCommand.WriteDryRunAsync(outputPath, twoStage, freezeHash, approvalPath, journalPath, key);
			Console.WriteLine("Dry run report created. No provider request was sent.");
			return 0;
		}

		if (key is null)
		{
			throw new InvalidOperationException("Missing key.");
		}

		string approvalHash = Hashing.Hash(File.ReadAllBytes(approvalPath));
		Action authorize = Authorizer(approvalPath, approvalHash, repository, apiUserSecrets, environment, isWindows, key,
			approval => TwoStageCommand.ApprovalProblem(approval, twoStage, freezeHash, DateTimeOffset.UtcNow));
		authorize();
		using SocketsHttpHandler handler = new() { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.Zero };
		using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
		TwoStageReplay result = await JournalledRun.RunAsync(new TwoStageProtocol(twoStage), journalPath,
			BindCredential(TwoStageCommand.Identity(freezeHash, approvalHash), key), (payload, ct) => SendAsync(client, payload, key, ct), authorize);
		using FileStream output = PrivateEvidence.CreateNew(outputPath);
		await JsonSerializer.SerializeAsync(output, result, ProductionParity.JsonOptions);
		output.Flush(flushToDisk: true);
		Console.WriteLine(result.Stopped ? "Evaluation stopped. Inspect private evidence." : "Evaluation completed. Private evidence saved.");
		return result.Stopped ? 3 : 0;
	}

	/// <summary>
	/// Re-checks before every send that the approval bytes and the credential are unchanged and the approval still
	/// authorizes this packet. Expiry, revocation, credential change or any approval edit blocks dispatch.
	/// </summary>
	private static Action Authorizer(string approvalPath, string approvalHash, string repository, bool apiUserSecrets,
		Func<string, string?> environment, bool isWindows, string key, Func<LiveApproval, string?> problem) => () =>
	{
		byte[] bytes = File.ReadAllBytes(approvalPath);
		LiveApproval approval = JsonSerializer.Deserialize<LiveApproval>(bytes, ProductionParity.JsonOptions)!;
		if (Hashing.Hash(bytes) != approvalHash ||
			ApiCredential.Resolve(repository, apiUserSecrets, environment, isWindows) != key || problem(approval) is not null)
		{
			throw new InvalidOperationException("Authorization stale or insufficient.");
		}
	};

	/// <summary>Approval file hash and a non-sensitive status; a missing or unreadable file is reported, not fatal.</summary>
	internal static (string? Hash, string Status) ReadApprovalStatus(string approvalPath, Func<LiveApproval, string?> problem)
	{
		if (!File.Exists(approvalPath))
		{
			return (null, "missing");
		}

		byte[] bytes = File.ReadAllBytes(approvalPath);
		LiveApproval? approval;
		try { approval = JsonSerializer.Deserialize<LiveApproval>(bytes, ProductionParity.JsonOptions); }
		catch (JsonException) { approval = null; }
		return (Hashing.Hash(bytes), approval is null ? "unreadable" : problem(approval) ?? "valid");
	}

	/// <summary>Reads the journal without creating, locking for write, or modifying it.</summary>
	internal static (string Status, int CompletedExchanges, bool? IdentityMatches) ReadJournalStatus(string journalPath, string? expectedIdentity)
	{
		if (!File.Exists(journalPath))
		{
			return ("absent", 0, null);
		}

		try
		{
			using FileStream journal = new(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
			LiveJournalState state = LiveJournalState.Read(journal);
			string status = state.Identity is null ? "empty" : state.OutstandingIntent ? "outstanding-intent" : "resumable";
			bool? matches = state.Identity is not null && expectedIdentity is not null ? state.Identity == expectedIdentity : null;
			return (status, state.Exchanges.Count, matches);
		}
		catch (IOException) { return ("locked-or-unreadable", 0, null); }
		catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
		{
			return ("invalid", 0, null);
		}
	}
}
