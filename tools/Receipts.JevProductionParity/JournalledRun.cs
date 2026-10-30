using System.Text;
using System.Text.Json;

namespace Receipts.JevProductionParity;

/// <summary>One attempt as a protocol replay sees it: the exact request, its status, and what it reserves.</summary>
public sealed record JournalAttempt(string RequestSha256, string RequestJson, string Status,
	long ReservedInputTokens, long ReservationNanodollars);

/// <summary>A deterministic replay of saved exchanges. The tail attempt is "unknown" when its exchange is still missing.</summary>
public sealed record ProtocolReplay<TResult>(TResult Result, IReadOnlyList<JournalAttempt> Attempts, bool Stopped);

/// <summary>
/// The evaluation-specific part of a journalled run: which request comes next and what it reserves.
/// Budget limits come from a frozen packet; <see cref="JournalledRun"/> enforces them.
/// </summary>
public interface IJournalProtocol<TResult>
{
	int MaximumAttempts { get; }
	long PriorCarryNanodollars { get; }

	/// <summary>Carry plus every reservation plus headroom may never exceed this.</summary>
	long ReservationLimitNanodollars { get; }

	/// <summary>Kept back for one attempt whose usage exceeds its reservation; such usage stops the run.</summary>
	long HeadroomNanodollars { get; }

	Task<ProtocolReplay<TResult>> ReplayAsync(IReadOnlyList<ReplayExchange> exchanges, CancellationToken ct);
}

/// <summary>
/// One durable intent per send, written and flushed before transport. An incomplete intent permanently
/// blocks automatic resume; there are no retries. Shared by every evaluation protocol.
/// </summary>
public static class JournalledRun
{
	public static async Task<TResult> RunAsync<TResult>(IJournalProtocol<TResult> protocol, string journalPath,
		string identity, Func<byte[], CancellationToken, Task<ReplayExchange>> send, Action authorize,
		CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(protocol);
		// Never replace this file: the open handle is also the exclusive process lock.
		using FileStream journal = PrivateEvidence.Open(journalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
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
			ProtocolReplay<TResult> replay = await protocol.ReplayAsync(exchanges, ct);
			if (replay.Attempts.Count < exchanges.Count)
			{
				throw new InvalidOperationException("Request drift.");
			}

			for (int i = 0; i < exchanges.Count; i++)
			{
				// Every saved exchange must answer the request replay rebuilds, wherever it sits.
				if (replay.Attempts[i].RequestSha256 != exchanges[i].RequestSha256)
				{
					throw new InvalidOperationException("Request drift.");
				}

				// Billed usage stays in the private response. Never release reservations.
				if (UsageExceeds(exchanges[i], replay.Attempts[i].ReservedInputTokens))
				{
					throw new InvalidOperationException("Provider usage exceeded reservation assumptions.");
				}
			}

			// Only an exchange missing at the exact tail permits another send.
			if (!replay.Stopped || replay.Attempts.Count != exchanges.Count + 1 || replay.Attempts[^1].Status != "unknown")
			{
				return replay.Result;
			}

			JournalAttempt next = replay.Attempts[^1];
			byte[] payload = Encoding.UTF8.GetBytes(next.RequestJson);
			long reserved = checked(protocol.PriorCarryNanodollars + replay.Attempts.Sum(a => a.ReservationNanodollars) +
				protocol.HeadroomNanodollars);
			if (Hashing.Hash(payload) != next.RequestSha256 || exchanges.Count >= protocol.MaximumAttempts ||
					next.ReservationNanodollars <= 0 || reserved > protocol.ReservationLimitNanodollars)
			{
				throw new InvalidOperationException("Request or reservation mismatch.");
			}
			authorize(); ct.ThrowIfCancellationRequested();
			Append(new
			{
				kind = "intent",
				index = exchanges.Count,
				next.RequestSha256,
				reservationNanodollars = next.ReservationNanodollars
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

	/// <summary>True when a successful response reports more input tokens than the attempt reserved.</summary>
	public static bool UsageExceeds(ReplayExchange exchange, long reservedInputTokens)
	{
		ArgumentNullException.ThrowIfNull(exchange);
		if (exchange.StatusCode is < 200 or >= 300)
		{
			return false;
		}

		using JsonDocument body = JsonDocument.Parse(exchange.Body);
		return body.RootElement.ValueKind == JsonValueKind.Object &&
			body.RootElement.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object &&
			usage.TryGetProperty("input_tokens", out JsonElement tokens) &&
			tokens.TryGetInt64(out long count) && count > reservedInputTokens;
	}
}
