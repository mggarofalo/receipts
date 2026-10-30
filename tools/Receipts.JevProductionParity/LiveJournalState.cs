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
