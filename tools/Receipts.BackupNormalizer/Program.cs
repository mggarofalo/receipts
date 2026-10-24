using System.Text.Json;
using Application.Normalization;
using Infrastructure.Services;

// Output files are private artifacts. stdout contains status only, including on malformed receipt data.
try
{
	if (args.Length < 1) { throw new ArgumentException(); }
	OfflinePortableBackupInspection service = new();
	switch (args[0])
	{
		case "apply" or "verify" when args.Length == 7 && args[1] == "--source" && args[3] == "--manifest" && args[5] == "--output":
			{
				PortableBackupNormalizationService normalizer = new();
				OfflineBackupNormalizationResult result = args[0] == "apply"
					? await normalizer.ApplyAsync(args[2], args[4], args[6])
					: await normalizer.VerifyAsync(args[2], args[4], args[6]);
				Console.WriteLine(result.Unchanged ? "Existing normalized artifact and lineage verified; unchanged." : "Normalized artifact published with private manifest and lineage sidecars.");
				break;
			}
		case "propose" when args.Length == 9 && args[1] == "--source" && args[3] == "--output" && args[5] == "--proposer" && args[7] == "--tool-revision":
			{
				string sourcePath = Path.GetFullPath(args[2]);
				string outputPath = Path.GetFullPath(args[4]);
				EnsureSeparate(sourcePath, outputPath);
				OfflineBackupInspection inspection = await service.InspectAsync(sourcePath);
				OfflineNormalizationManifest proposal = new(1, inspection.SourceSha256, inspection.FormatVersion,
					inspection.FactualBaselineSha256, args[8], args[6], DateTimeOffset.UtcNow, [], [], [], [], null);
				OfflineNormalizationManifestPolicy.Validate(proposal, false, DateTimeOffset.UtcNow);
				await using FileStream output = new(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
				await JsonSerializer.SerializeAsync(output, proposal, OfflineNormalizationManifestPolicy.JsonOptions);
				Console.WriteLine("Unreviewed manifest scaffold written. No decisions or review authority generated.");
				break;
			}
		case "inspect" when args.Length == 5 && args[1] == "--source" && args[3] == "--output":
			{
				string sourcePath = Path.GetFullPath(args[2]);
				string outputPath = Path.GetFullPath(args[4]);
				EnsureSeparate(sourcePath, outputPath);
				OfflineBackupInspection inspection = await service.InspectAsync(sourcePath);
				await using FileStream output = new(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
				await JsonSerializer.SerializeAsync(output, inspection, OfflineNormalizationManifestPolicy.JsonOptions);
				Console.WriteLine("Inspection complete; private report written.");
				break;
			}
		case "verify-baseline" when args.Length == 5 && args[1] == "--baseline" && args[3] == "--candidate":
			{
				OfflineBackupInspection baseline = JsonSerializer.Deserialize<OfflineBackupInspection>(await File.ReadAllTextAsync(args[2]), OfflineNormalizationManifestPolicy.JsonOptions) ?? throw new InvalidDataException();
				OfflineBackupInspection candidate = await service.InspectAsync(args[4]);
				service.VerifyBaseline(baseline, candidate);
				Console.WriteLine("Factual and financial baseline verified.");
				break;
			}
		case "validate-manifest" when args.Length == 5 && args[1] == "--source" && args[3] == "--manifest":
			{
				OfflineNormalizationManifest manifest = OfflineNormalizationManifestPolicy.Read(await File.ReadAllTextAsync(args[4]), true, DateTimeOffset.UtcNow);
				service.ValidateManifest(manifest, await service.InspectAsync(args[2]), true, DateTimeOffset.UtcNow);
				Console.WriteLine("Reviewed manifest matches the source artifact. No changes applied.");
				break;
			}
		default: throw new ArgumentException();
	}
	return 0;
}
catch (ArgumentException)
{
	Console.Error.WriteLine("Usage: apply|verify --source A.db --manifest reviewed.json --output B.db | inspect --source A.db --output private-report.json | propose --source A.db --output proposal.json --proposer identity --tool-revision revision | verify-baseline --baseline private-report.json --candidate B.db | validate-manifest --source A.db --manifest reviewed.json");
	return 2;
}
catch (Exception)
{
	// SQLite/JSON/parse exception messages can include source values. Keep private facts out of logs.
	Console.Error.WriteLine("Backup inspection or validation failed. Check private inputs, schema, integrity and review requirements.");
	return 1;
}

static void EnsureSeparate(string source, string output)
{
	if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase) || File.Exists(output) || Directory.Exists(output)
		|| new[] { "-wal", "-shm", "-journal" }.Any(suffix => string.Equals(source + suffix, output, StringComparison.OrdinalIgnoreCase)))
	{ throw new InvalidDataException("Output must be a new separate private file."); }
}
