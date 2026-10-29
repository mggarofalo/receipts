using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Receipts.JevProductionParity;

public sealed record LiveApproval(string FreezeSha256, DateTimeOffset ExpiresUtc,
		bool Authorized, bool PricingAndContextVerified, long PriorCarryNanodollars,
		long AuthorizedBudgetNanodollars);

/// <summary>One durable intent per send. An incomplete intent permanently blocks automatic resume.</summary>
public static class LiveEvaluation
{
	public static async Task<OfflineReplay> RunAsync(IReadOnlyList<OfflineCase> cases, string journalPath,
			string identity, Func<byte[], CancellationToken, Task<ReplayExchange>> send,
			Action authorize, CancellationToken ct = default)
	{
		OfflinePairedEvaluation.ValidateCases(cases);
		// Never replace this file: the open handle is also the exclusive process lock.
		using FileStream journal = new(journalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		List<JsonElement> rows = [];
		if (journal.Length > 64 * 1024 * 1024)
		{
			throw new InvalidOperationException("Journal size limit.");
		}

		using (StreamReader reader = new(journal, Encoding.UTF8, leaveOpen: true))
		{
			string text = reader.ReadToEnd();
			if (text.Length > 0 && !text.EndsWith('\n'))
			{
				throw new InvalidOperationException("Incomplete journal.");
			}

			foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				rows.Add(JsonSerializer.Deserialize<JsonElement>(line));
			}
		}
		void Append(object row)
		{
			byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(row, ProductionParity.JsonOptions);
			journal.Seek(0, SeekOrigin.End);
			journal.Write(bytes); journal.WriteByte((byte)'\n'); journal.Flush(flushToDisk: true);
		}
		if (rows.Count == 0)
		{
			Append(new { kind = "header", identity });
		}
		else if (rows[0].GetProperty("kind").GetString() != "header" || rows[0].GetProperty("identity").GetString() != identity)
		{
			throw new InvalidOperationException("Journal identity mismatch.");
		}

		List<ReplayExchange> exchanges = [];
		for (int i = 1; i < rows.Count; i += 2)
		{
			JsonElement intent = rows[i];
			if (intent.GetProperty("kind").GetString() != "intent" || intent.GetProperty("index").GetInt32() != exchanges.Count || i + 1 >= rows.Count)
			{
				throw new InvalidOperationException("Ambiguous intent; no resend.");
			}

			JsonElement response = rows[i + 1];
			string hash = intent.GetProperty("requestSha256").GetString()!;
			if (response.GetProperty("kind").GetString() != "response" || response.GetProperty("index").GetInt32() != exchanges.Count ||
					response.GetProperty("requestSha256").GetString() != hash)
			{
				throw new InvalidOperationException("Unbound response.");
			}

			exchanges.Add(new(response.GetProperty("body").GetString()!, response.GetProperty("statusCode").GetInt32(), hash));
		}
		while (true)
		{
			ct.ThrowIfCancellationRequested();
			foreach (ReplayExchange saved in exchanges.Where(e => e.StatusCode is >= 200 and < 300))
			{
				// Billed usage stays in the private response. Never release reservations.
				using JsonDocument body = JsonDocument.Parse(saved.Body);
				if (body.RootElement.TryGetProperty("usage", out JsonElement usage) &&
						usage.TryGetProperty("input_tokens", out JsonElement tokens) &&
						tokens.TryGetInt64(out long count) && count > 65_536)
				{
					throw new InvalidOperationException("Provider usage exceeded reservation assumptions.");
				}
			}
			OfflineReplay replay = await OfflinePairedEvaluation.ReplayAsync(cases, exchanges, ct);
			// Only an exchange missing at the exact tail permits another send.
			if (!replay.Stopped || replay.Attempts.Count != exchanges.Count + 1 || replay.Attempts[^1].Status != "unknown")
			{
				return replay;
			}

			OfflineAttempt next = replay.Attempts[^1];
			byte[] payload = Encoding.UTF8.GetBytes(next.RequestJson);
			if (OfflinePairedEvaluation.Hash(payload) != next.RequestSha256 || exchanges.Count >= 48 ||
					OfflinePairedEvaluation.PriorCarryNanodollars + (exchanges.Count + 1) * OfflinePairedEvaluation.PerAttemptNanodollars > 4_882_120_576)
			{
				throw new InvalidOperationException("Request or reservation mismatch.");
			}
			// Reject changed earlier requests before dispatching a suffix.
			for (int i = 0; i < exchanges.Count; i++)
			{
				if (replay.Attempts[i].RequestSha256 != exchanges[i].RequestSha256)
				{
					throw new InvalidOperationException("Request drift.");
				}
			}

			authorize(); ct.ThrowIfCancellationRequested();
			Append(new
			{
				kind = "intent",
				index = exchanges.Count,
				next.RequestSha256,
				reservationNanodollars = OfflinePairedEvaluation.PerAttemptNanodollars
			});
			// Any exception after the durable intent leaves it outstanding. Never retry.
			ReplayExchange exchange = await send(payload, ct);
			Append(new
			{
				kind = "response",
				index = exchanges.Count,
				next.RequestSha256,
				exchange.Body,
				exchange.StatusCode
			});
			exchanges.Add(exchange with { RequestSha256 = next.RequestSha256 });
		}
	}
}

public static class LiveCommand
{
	public static void ValidateApproval(LiveApproval? approval, string freezeHash, DateTimeOffset now)
	{
		// Authorization is a cumulative spending ceiling, never an account balance assertion.
		long maximumNewReservation = checked(OfflinePairedEvaluation.MaximumAttempts * OfflinePairedEvaluation.PerAttemptNanodollars);
		if (approval is null || !approval.Authorized || !approval.PricingAndContextVerified ||
			approval.FreezeSha256 != freezeHash || approval.ExpiresUtc <= now ||
			approval.PriorCarryNanodollars != OfflinePairedEvaluation.PriorCarryNanodollars ||
			approval.AuthorizedBudgetNanodollars != OfflinePairedEvaluation.CeilingNanodollars ||
			checked(approval.PriorCarryNanodollars + maximumNewReservation) > approval.AuthorizedBudgetNanodollars)
		{
			throw new InvalidOperationException("Authorization stale or insufficient.");
		}
	}

	// Persist only a one-way fingerprint, never the credential. Rotation requires investigation,
	// even when the replacement key might belong to the same provider account.
	public static string BindCredential(string identity, string key) => identity + ":" +
		OfflinePairedEvaluation.Hash(Encoding.UTF8.GetBytes("receipts-live-credential-v1\0" + key));

	public static async Task<ReplayExchange> SendAsync(HttpClient client, byte[] payload, string key, CancellationToken ct)
	{
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(TimeSpan.FromSeconds(10));
		using HttpRequestMessage request = new(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
		request.Content = new ByteArrayContent(payload);
		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
		await response.Content.LoadIntoBufferAsync(1_048_576, deadline.Token);
		return new(await response.Content.ReadAsStringAsync(deadline.Token), (int)response.StatusCode);
	}

	public static async Task<int> RunAsync(string[] args)
	{
		try
		{
			if (args is not ["live-run", var packet, var repository, var approvalPath, var journalPath, var outputPath, "--send-authorized"])
			{
				throw new ArgumentException("Invalid arguments.");
			}
			// Evidence outputs must live outside the checkout; never overwrite artifacts.
			string repo = Path.GetFullPath(repository).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			foreach (string path in new[] { packet, approvalPath, journalPath, outputPath })
			{
				if (Path.GetFullPath(path).StartsWith(repo, StringComparison.OrdinalIgnoreCase))
				{
					throw new ArgumentException("Private path required.");
				}
			}

			if (File.Exists(outputPath))
			{
				throw new IOException("Output exists.");
			}

			if (!string.Equals(Path.GetFullPath(journalPath), Path.GetFullPath(Path.Combine(packet, "live.journal.jsonl")), StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException("Use the single packet journal.");
			}

			OfflineCommand.ValidateFreeze(packet, repository);
			string freezeHash = OfflinePairedEvaluation.Hash(File.ReadAllBytes(Path.Combine(packet, "freeze.private.json")));
			OfflineCase[] cases = JsonSerializer.Deserialize<OfflineCase[]>(File.ReadAllText(Path.Combine(packet, "cases.private.json")), ProductionParity.JsonOptions)!;
			OfflinePairedEvaluation.ValidateCases(cases, requireFullPacket: true);
			string key = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY") ?? throw new InvalidOperationException("Missing key.");
			if (string.IsNullOrWhiteSpace(key))
			{
				throw new InvalidOperationException("Missing key.");
			}

			string approvalHash = OfflinePairedEvaluation.Hash(File.ReadAllBytes(approvalPath));
			void Authorize()
			{
				byte[] bytes = File.ReadAllBytes(approvalPath);
				LiveApproval approval = JsonSerializer.Deserialize<LiveApproval>(bytes, ProductionParity.JsonOptions)!;
				if (OfflinePairedEvaluation.Hash(bytes) != approvalHash || Environment.GetEnvironmentVariable("TYPESAFE_API_KEY") != key)
				{
					throw new InvalidOperationException("Authorization stale or insufficient.");
				}
				ValidateApproval(approval, freezeHash, DateTimeOffset.UtcNow);
			}
			Authorize();
			string identity = freezeHash + ":" + approvalHash + ":" + OfflinePairedEvaluation.Hash(JsonSerializer.SerializeToUtf8Bytes(cases, ProductionParity.JsonOptions)) + ":" +
					OfflinePairedEvaluation.Hash(File.ReadAllBytes(typeof(LiveCommand).Assembly.Location)) + ":" +
					OfflinePairedEvaluation.Hash(File.ReadAllBytes(typeof(Infrastructure.Jev.JevSuggestionProvider).Assembly.Location)) + ":" +
					OfflinePairedEvaluation.Hash(File.ReadAllBytes(typeof(Application.Classification.CategorySuggestionPolicy).Assembly.Location));
			using SocketsHttpHandler handler = new() { AllowAutoRedirect = false, UseCookies = false };
			using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
			OfflineReplay result = await LiveEvaluation.RunAsync(cases, journalPath, BindCredential(identity, key),
				(payload, ct) => SendAsync(client, payload, key, ct), Authorize);
			using FileStream output = new(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
			await JsonSerializer.SerializeAsync(output, result, ProductionParity.JsonOptions);
			output.Flush(flushToDisk: true);
			Console.WriteLine(result.Stopped ? "Evaluation stopped. Inspect private evidence." : "Evaluation completed. Private evidence saved.");
			return result.Stopped ? 3 : 0;
		}
		catch (Exception)
		{
			// Do not echo exceptions: HTTP failures can contain payloads or credential material.
			Console.Error.WriteLine("Evaluation stopped. No automatic retries. Preserve and inspect the private journal.");
			return 2;
		}
	}
}
