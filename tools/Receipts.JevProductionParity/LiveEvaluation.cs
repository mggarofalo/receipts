using System.Text;
using System.Text.Json;

namespace Receipts.JevProductionParity;

/// <summary>Parsed journal: header identity, complete exchanges, and whether an unanswered intent remains.</summary>
public sealed record LiveJournalState(string? Identity, IReadOnlyList<ReplayExchange> Exchanges, bool OutstandingIntent)
{
	public const long MaximumBytes = 64 * 1024 * 1024;

	public static LiveJournalState Read(Stream journal)
	{
		if (journal.Length > MaximumBytes)
		{
			throw new InvalidOperationException("Journal size limit.");
		}

		using StreamReader reader = new(journal, Encoding.UTF8, leaveOpen: true);
		return Parse(reader.ReadToEnd());
	}

	public static LiveJournalState Parse(string text)
	{
		if (text.Length > 0 && !text.EndsWith('\n'))
		{
			throw new InvalidOperationException("Incomplete journal.");
		}

		List<JsonElement> rows = [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<JsonElement>(line))];
		if (rows.Count == 0)
		{
			return new(null, [], false);
		}

		if (rows[0].GetProperty("kind").GetString() != "header")
		{
			throw new InvalidOperationException("Journal identity mismatch.");
		}

		List<ReplayExchange> exchanges = [];
		for (int i = 1; i < rows.Count; i += 2)
		{
			JsonElement intent = rows[i];
			if (intent.GetProperty("kind").GetString() != "intent" || intent.GetProperty("index").GetInt32() != exchanges.Count)
			{
				throw new InvalidOperationException("Ambiguous intent; no resend.");
			}

			if (i + 1 >= rows.Count)
			{
				return new(rows[0].GetProperty("identity").GetString(), exchanges, true);
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
		return new(rows[0].GetProperty("identity").GetString(), exchanges, false);
	}
}

/// <summary>One durable intent per send. An incomplete intent permanently blocks automatic resume.</summary>
public static class LiveEvaluation
{
	public static async Task<OfflineReplay> RunAsync(IReadOnlyList<OfflineCase> cases, EvaluationPolicy policy, string journalPath,
			string identity, Func<byte[], CancellationToken, Task<ReplayExchange>> send,
			Action authorize, CancellationToken ct = default)
	{
		OfflinePairedEvaluation.ValidateCases(cases, policy);
		// Never replace this file: the open handle is also the exclusive process lock.
		using FileStream journal = new(journalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		LiveJournalState state = LiveJournalState.Read(journal);
		void Append(object row)
		{
			byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(row, ProductionParity.JsonOptions);
			journal.Seek(0, SeekOrigin.End);
			journal.Write(bytes); journal.WriteByte((byte)'\n'); journal.Flush(flushToDisk: true);
		}
		if (state.Identity is null)
		{
			Append(new { kind = "header", identity });
		}
		else if (state.Identity != identity)
		{
			throw new InvalidOperationException("Journal identity mismatch.");
		}

		if (state.OutstandingIntent)
		{
			throw new InvalidOperationException("Ambiguous intent; no resend.");
		}

		List<ReplayExchange> exchanges = [.. state.Exchanges];
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
			OfflineReplay replay = await OfflinePairedEvaluation.ReplayAsync(cases, exchanges, policy, ct);
			// Only an exchange missing at the exact tail permits another send.
			if (!replay.Stopped || replay.Attempts.Count != exchanges.Count + 1 || replay.Attempts[^1].Status != "unknown")
			{
				return replay;
			}

			OfflineAttempt next = replay.Attempts[^1];
			byte[] payload = Encoding.UTF8.GetBytes(next.RequestJson);
			if (OfflinePairedEvaluation.Hash(payload) != next.RequestSha256 || exchanges.Count >= policy.MaximumAttempts ||
					policy.PriorCarryNanodollars + ((exchanges.Count + 1) * EvaluationPolicy.PerAttemptNanodollars) > policy.CombinedMaximumReservationNanodollars)
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
				reservationNanodollars = EvaluationPolicy.PerAttemptNanodollars
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

