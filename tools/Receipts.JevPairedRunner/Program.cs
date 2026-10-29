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
			if (args.Length != 4 || args[0] is not ("prepare" or "replay" or "validate" or "baseline-result" or "prepare-batched" or "replay-batched" or "validate-batched" or "prepare-purchase-experiment" or "validate-purchase-experiment" or "prepare-selected-path" or "validate-selected-path"))
			{
				Console.Error.WriteLine("Usage: <offline-command> <absolute-input.json> <absolute-replies.json> <absolute-output.json>");
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
				if (args[0] is "prepare-purchase-experiment" or "validate-purchase-experiment")
				{
					PurchaseExperimentInput experiment = ReadExperiment<PurchaseExperimentInput>(args[1]) ?? throw new JsonException();
					PurchaseExperimentReply? reply = ReadExperiment<PurchaseExperimentReply>(args[2]);
					object experimentResult = PurchaseExperimentExchange.Execute(args[0], experiment, reply);
					using FileStream experimentOutput = new(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None);
					await JsonSerializer.SerializeAsync(experimentOutput, experimentResult, CandidateParity.JsonOptions);
					experimentOutput.Flush(flushToDisk: true);
					return 0;
				}
				if (args[0] is "prepare-selected-path" or "validate-selected-path")
				{
					PairedInput selectedInput = ReadExperiment<PairedInput>(args[1]) ?? throw new JsonException();
					SelectedPathReply selectedReply = ReadExperiment<SelectedPathReply>(args[2]) ?? throw new JsonException();
					object selectedResult = await SelectedPathExchange.ExecuteAsync(args[0], selectedInput, selectedReply);
					using FileStream selectedOutput = new(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None);
					await JsonSerializer.SerializeAsync(selectedOutput, selectedResult, CandidateParity.JsonOptions);
					selectedOutput.Flush(flushToDisk: true);
					return 0;
				}
				PairedInput input = Read<PairedInput>(args[1]);
				if (args[0].EndsWith("-batched", StringComparison.Ordinal))
				{
					BatchedReplayReplies batchedReplies = Read<BatchedReplayReplies>(args[2]);
					BatchedPairedReplayPlan batched = new(input);
					object batchResult;
					if (args[0] == "validate-batched")
					{
						await batched.ValidateAsync(batchedReplies);
						batchResult = new { validated = true, maximumNetworkCalls = 0 };
					}
					else if (args[0] == "prepare-batched")
					{
						var verification = batchedReplies.Shortlist.Count == 0 ? null :
							batched.PrepareVerification(batchedReplies.Shortlist);
						batchResult = new
						{
							format = BatchedPairedReplayPlan.Version,
							batched.PlanSha256,
							batched.ProductionPayload,
							shortlistPayloads = batched.ShortlistBatches.Select(batch => batch.Payload),
							verificationBatches = verification?.Batches,
							verificationSha256 = verification?.PlanSha256,
							maximumNetworkCalls = 0,
							retries = 0
						};
					}
					else
					{
						batchResult = await batched.ReplayAsync(batchedReplies);
					}
					using FileStream batchOutput = new(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None);
					await JsonSerializer.SerializeAsync(batchOutput, batchResult, CandidateParity.JsonOptions);
					batchOutput.Flush(flushToDisk: true);
					return 0;
				}
				ReplayReplies replies = Read<ReplayReplies>(args[2]);
				PairedReplayPlan plan = PairedReplay.Prepare(input);
				object result;
				if (args[0] == "baseline-result")
				{
					if (replies.Shortlist is null || replies.Shortlist.Count != 0 || replies.Verification is not null || string.IsNullOrEmpty(replies.Production))
					{
						throw new ArgumentException();
					}
					var baseline = await plan.ParseProductionAsync(replies.Production);
					if (baseline.Failure is not null)
					{
						throw new ArgumentException();
					}
					result = new { production = baseline, maximumNetworkCalls = 0 };
				}
				else if (args[0] == "validate")
				{
					await plan.ValidateAsync(replies);
					result = new { validated = true, maximumNetworkCalls = 0 };
				}
				else if (args[0] == "prepare")
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

		private static T? ReadExperiment<T>(string path) where T : class
		{
			using FileStream input = File.OpenRead(path);
			if (input.Length > 8 * 1024 * 1024)
			{
				throw new ArgumentException();
			}
			byte[] bytes = new byte[checked((int)input.Length)];
			input.ReadExactly(bytes);
			using JsonDocument document = JsonDocument.Parse(bytes);
			RejectDuplicateMembers(document.RootElement);
			return JsonSerializer.Deserialize<T>(bytes, CandidateParity.JsonOptions);
		}

		internal static void RejectDuplicateMembers(JsonElement element)
		{
			if (element.ValueKind == JsonValueKind.Object)
			{
				HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
				foreach (JsonProperty property in element.EnumerateObject())
				{
					if (!names.Add(property.Name))
					{
						throw new JsonException();
					}
					RejectDuplicateMembers(property.Value);
				}
			}
			else if (element.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in element.EnumerateArray())
				{
					RejectDuplicateMembers(item);
				}
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
