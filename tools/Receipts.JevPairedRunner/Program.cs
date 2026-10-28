using System.Text.Json;
using Receipts.JevCandidateParity;
using Receipts.JevPairedRunner;

return await OfflineExchangeCommand.RunAsync(args);

namespace Receipts.JevPairedRunner
{
	/// <summary>Private file exchange only. Never opens a socket or reads credentials.</summary>
	public static class OfflineExchangeCommand
	{
		public static async Task<int> RunAsync(string[] args)
		{
			if (args.Length != 4 || args[0] is not ("prepare" or "replay"))
			{
				Console.Error.WriteLine("Usage: prepare|replay <absolute-input.json> <absolute-replies.json> <absolute-output.json>");
				return 2;
			}
			try
			{
				RequirePlainAbsolutePath(args[1], allowMissingLeaf: false);
				RequirePlainAbsolutePath(args[2], allowMissingLeaf: false);
				RequirePlainAbsolutePath(args[3], allowMissingLeaf: true);
				for (DirectoryInfo? parent = new(Path.GetDirectoryName(args[3])!); parent is not null; parent = parent.Parent)
				{
					if (File.Exists(Path.Combine(parent.FullName, ".git")) || Directory.Exists(Path.Combine(parent.FullName, ".git")))
					{
						throw new ArgumentException();
					}
				}
				PairedInput input = Read<PairedInput>(args[1]);
				ReplayReplies replies = Read<ReplayReplies>(args[2]);
				PairedReplayPlan plan = PairedReplay.Prepare(input);
				object result;
				if (args[0] == "prepare")
				{
					// An empty shortlist requests initial payloads. Otherwise the actual
					// reducer validates every supplied shortlist response before verification.
					if (replies.Shortlist.Count != 0 && ExhaustiveShortlist.Prepare(input.Purchase,
						input.TaxonomyRevision, input.Taxonomy, plan.Model).Aggregate(replies.Shortlist).Failure is not null)
					{
						throw new ArgumentException();
					}
					var verification = replies.Shortlist.Count == 0 ? null : plan.PrepareVerification(replies.Shortlist);
					result = new
					{
						format = "paired-offline-exchange-v1",
						plan.PlanSha256,
						plan.Model,
						plan.ProductionPayload,
						shortlistPayloads = plan.ShortlistBatches.Select(batch => batch.Payload),
						verificationPayload = verification?.Payload,
						verificationSha256 = verification?.PlanSha256,
						maximumNetworkCalls = 0,
						inputTokenLimit = (long?)null,
						outputTokenLimit = (long?)null
					};
				}
				else
				{
					result = await plan.ReplayAsync(replies);
				}
				// Caller owns private output directory/ACLs. Never overwrite an artifact.
				using FileStream output = new(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None);
				await JsonSerializer.SerializeAsync(output, result, CandidateParity.JsonOptions);
				output.Flush(flushToDisk: true);
				return 0;
			}
			catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
				or JsonException or InvalidOperationException or NullReferenceException)
			{
				// Never print exception text, paths, payloads, response bodies or secrets.
				Console.Error.WriteLine("Offline exchange failed. Inspect private inputs and output location.");
				return 2;
			}
		}

		private static T Read<T>(string path)
		{
			using FileStream input = File.OpenRead(path);
			if (input.Length > 8 * 1024 * 1024)
			{
				throw new ArgumentException();
			}
			byte[] bytes = new byte[checked((int)input.Length)];
			input.ReadExactly(bytes);
			return JsonSerializer.Deserialize<T>(bytes, CandidateParity.JsonOptions) ?? throw new JsonException();
		}

		private static void RequirePlainAbsolutePath(string path, bool allowMissingLeaf)
		{
			if (!Path.IsPathFullyQualified(path) || path.Split(['\\', '/']).Contains(".."))
			{
				throw new ArgumentException();
			}
			string full = Path.GetFullPath(path);
			string current = Path.GetPathRoot(full)!;
			string[] parts = full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
			// Inspect from the volume root down, rejecting a reparse point before
			// accessing descendants. Private path ancestry must remain stable.
			for (int index = -1; index < parts.Length; index++)
			{
				if (index >= 0)
				{
					current = Path.Combine(current, parts[index]);
				}
				FileAttributes attributes;
				try
				{
					attributes = File.GetAttributes(current);
				}
				catch (FileNotFoundException) when (allowMissingLeaf && current == full)
				{
					return;
				}
				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					throw new ArgumentException();
				}
			}
		}
	}
}
