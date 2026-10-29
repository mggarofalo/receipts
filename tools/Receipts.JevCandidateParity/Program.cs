using System.Text.Json;
using Receipts.JevCandidateParity;

return CandidateParityCommand.Run(args, Console.Out, Console.Error);

namespace Receipts.JevCandidateParity
{
	public static class CandidateParityCommand
	{
		public const int MaximumFixtureBytes = 8 * 1024 * 1024;

		public static int Run(string[] args, TextWriter output, TextWriter error)
		{
			if (args.Length != 1)
			{
				error.WriteLine("Usage: Receipts.JevCandidateParity <sanitized-fixture.json>");
				return 2;
			}

			try
			{
				using FileStream stream = File.OpenRead(args[0]);
				if (stream.Length > MaximumFixtureBytes)
				{
					throw new ArgumentException("Oversized fixture.");
				}
				// Read a bounded snapshot even if another process subsequently grows the file.
				byte[] bytes = new byte[checked((int)stream.Length)];
				stream.ReadExactly(bytes);
				CandidateParityFixture fixture = JsonSerializer.Deserialize<CandidateParityFixture>(bytes,
					CandidateParity.JsonOptions) ?? throw new JsonException("Fixture must be an object.");
				CandidateParityRecord record = CandidateParity.Evaluate(fixture);
				output.WriteLine(JsonSerializer.Serialize(record, CandidateParity.JsonOptions));
				return 0;
			}
			catch (Exception exception) when (exception is JsonException or ArgumentException or IOException
				or UnauthorizedAccessException)
			{
				// Fixture content and exception text may contain private data.
				error.WriteLine("Invalid or unreadable fixture. Check the documented schema and candidate input limits.");
				return 2;
			}
		}
	}
}
