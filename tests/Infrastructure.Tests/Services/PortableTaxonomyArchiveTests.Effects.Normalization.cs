using System.Security.Cryptography;
using System.Text.Json;
using Application.Normalization;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	private static async Task VerifyCandidateNormalizationAsync(string source)
	{
		// Store a valid literal replacement character, rather than a JSON escape. A
		// provider decoder must not make later invalid FF bytes indistinguishable.
		await using (var db = new SqliteConnection($"Data Source={source};Pooling=False"))
		{
			await db.OpenAsync();
			await using var cmd = db.CreateCommand();
			cmd.CommandText = "UPDATE organization_effect_archive SET payload=replace(payload,'source changed','source changed ' || char(65533)) WHERE kind='outcome'";
			await cmd.ExecuteNonQueryAsync();
		}
		var inspection = new OfflinePortableBackupInspection(true);
		var normalizer = new PortableBackupNormalizationService(true);
		var baseline = await inspection.InspectAsync(source);
		byte[] original = await File.ReadAllBytesAsync(source);
		string output = source + ".normalized", manifestPath = source + ".review.json";
		var now = DateTimeOffset.UtcNow.AddMinutes(-1);
		Guid decision = Guid.NewGuid(), canonical = Guid.NewGuid();
		var manifest = new OfflineNormalizationManifest(1, baseline.SourceSha256, 14,
			baseline.FactualBaselineSha256, "synthetic-candidate", "proposer", now, [new(decision, canonical, "Reviewed product", null)], [], [], [],
			new("reviewer", now, [new(decision, "Reviewed canonical name")]));
		await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, OfflineNormalizationManifestPolicy.JsonOptions));
		async Task<string[]> Rows(string path)
		{
			await using var db = new SqliteConnection($"Data Source={path};Pooling=False");
			await db.OpenAsync();
			await using var cmd = db.CreateCommand();
			cmd.CommandText = "SELECT kind,id,hex(CAST(payload AS BLOB)) FROM organization_effect_archive ORDER BY kind,id";
			await using var reader = await cmd.ExecuteReaderAsync();
			List<string> rows = [];
			while (await reader.ReadAsync()) { rows.Add(reader.GetString(0) + ":" + reader.GetString(1) + ":" + reader.GetString(2)); }
			return rows.ToArray();
		}
		try
		{
			Func<Task> publicInspect = () => new OfflinePortableBackupInspection().InspectAsync(source);
			await publicInspect.Should().ThrowAsync<InvalidDataException>();
			Func<Task> publicApply = () => new PortableBackupNormalizationService().ApplyAsync(source, manifestPath, output);
			await publicApply.Should().ThrowAsync<InvalidDataException>();
			var before = await Rows(source);
			before.Should().HaveCount(3); // Manifest, sealed item and terminal skipped outcome.
			var first = await normalizer.ApplyAsync(source, manifestPath, output);
			first.Unchanged.Should().BeFalse();
			(await Rows(output)).Should().Equal(before);
			(await File.ReadAllBytesAsync(source)).Should().Equal(original);
			var after = await inspection.InspectAsync(output);
			after.FormatVersion.Should().Be(14);
			inspection.VerifyBaseline(baseline, after);
			var bytes = await File.ReadAllBytesAsync(output);
			(await normalizer.ApplyAsync(source, manifestPath, output)).Unchanged.Should().BeTrue();
			(await normalizer.VerifyAsync(source, manifestPath, output)).OutputSha256.Should().Be(first.OutputSha256);
			(await File.ReadAllBytesAsync(output)).Should().Equal(bytes);

			await using (var db = new SqliteConnection($"Data Source={output};Pooling=False"))
			{
				await db.OpenAsync();
				await using var cmd = db.CreateCommand();
				cmd.CommandText = "UPDATE organization_effect_archive SET payload=payload || ' ' WHERE kind='outcome'";
				await cmd.ExecuteNonQueryAsync();
			}
			// Valid equivalent JSON and a matching whole-file checksum must not conceal a history edit.
			var lineage = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(output + ".lineage.json"))!;
			lineage["OutputSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(output)));
			await File.WriteAllTextAsync(output + ".lineage.json", JsonSerializer.Serialize(lineage));
			Func<Task> verify = () => normalizer.VerifyAsync(source, manifestPath, output);
			await verify.Should().ThrowAsync<InvalidDataException>().WithMessage("*baseline mismatch*");

			await File.WriteAllBytesAsync(output, bytes);
			await using (var db = new SqliteConnection($"Data Source={output};Pooling=False"))
			{
				await db.OpenAsync();
				await using var cmd = db.CreateCommand();
				cmd.CommandText = "UPDATE organization_effect_archive SET payload=replace(payload,char(65533),CAST(X'FF' AS TEXT)) WHERE kind='outcome'";
				await cmd.ExecuteNonQueryAsync();
				cmd.CommandText = "SELECT hex(CAST(payload AS BLOB)) FROM organization_effect_archive WHERE kind='outcome'";
				((string)(await cmd.ExecuteScalarAsync())!).Should().Contain("FF").And.NotContain("EFBFBD");
			}
			lineage["OutputSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(output)));
			await File.WriteAllTextAsync(output + ".lineage.json", JsonSerializer.Serialize(lineage));
			await verify.Should().ThrowAsync<InvalidDataException>().WithMessage("*UTF-8*");
			Func<Task> inspect = () => inspection.InspectAsync(output);
			await inspect.Should().ThrowAsync<InvalidDataException>().WithMessage("*UTF-8*");

		}
		finally
		{
			foreach (string path in new[] { output, output + ".manifest.json", output + ".lineage.json", manifestPath }) { File.Delete(path); }
		}
	}
}
