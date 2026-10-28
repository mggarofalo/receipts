using System.Text.Json;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	[Theory]
	[InlineData("none")]
	[InlineData("version")]
	[InlineData("table")]
	[InlineData("effects-table")]
	[InlineData("columns")]
	[InlineData("history")]
	[InlineData("membership")]
	[InlineData("read-authority")]
	[InlineData("export-authority")]
	public async Task Effects_CandidateBackupComposesOrdinaryHistoryAndSealedEffects(string mutation)
	{
		var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
		await using (var db = new ApplicationDbContext(options))
		{
			await db.Database.EnsureCreatedAsync();
			var (_, evidence) = EffectState();
			var revision = new TaxonomyRevisionEntity
			{
				Id = Guid.NewGuid(),
				PreviousRevisionId = Infrastructure.Configurations.TaxonomyConfiguration.GenesisId,
				Sequence = 1,
				Actor = "actor",
				Reason = "create",
				OperationId = Guid.NewGuid(),
				RecordedAt = evidence.Manifests[0].Manifest.CapturedAt
			};
			// The frozen item points at a real non-genesis taxonomy snapshot.
			var manifestJson = JsonSerializer.SerializeToNode(evidence.Manifests[0].Manifest)!;
			manifestJson["TaxonomyRevisionId"] = revision.Id;
			var manifest = manifestJson.Deserialize<OrganizationEffectManifestEntity>()!;
			var itemJson = JsonSerializer.SerializeToNode(evidence.Items[0])!;
			itemJson["TaxonomyRevisionId"] = revision.Id;
			if (mutation == "export-authority") { itemJson["ApplyOperationId"] = revision.OperationId; }
			var item = itemJson.Deserialize<OrganizationEffectItemEntity>()!;
			db.TaxonomyIdentities.Add(new() { Id = evidence.Items[0].TargetCategoryId });
			db.TaxonomyRevisions.Add(revision);
			db.TaxonomyOperations.Add(new()
			{
				Actor = revision.Actor,
				OperationId = revision.OperationId,
				RevisionId = revision.Id,
				Reason = revision.Reason,
				RecordedAt = revision.RecordedAt,
				RequestHash = new string('A', 64)
			});
			db.TaxonomyNodes.Add(new() { RevisionId = revision.Id, CategoryId = evidence.Items[0].TargetCategoryId, Name = "Target" });
			(await db.TaxonomyHeads.SingleAsync()).RevisionId = revision.Id;
			db.OrganizationEffectManifests.Add(manifest);
			db.OrganizationEffectItems.Add(item);
			await db.SaveChangesAsync();
		}
		var service = new BackupService(new Factory(options), NullLogger<BackupService>.Instance);
		if (mutation == "export-authority")
		{
			Func<Task> export = () => service.ExportV14CandidateAsync();
			await export.Should().ThrowAsync<InvalidDataException>().WithMessage("*collides with ordinary*");
			return;
		}
		string path = await service.ExportV14CandidateAsync();
		try
		{
			await using var sqlite = new SqliteConnection($"Data Source={path};Pooling=False");
			await sqlite.OpenAsync();
			await using var command = sqlite.CreateCommand();
			command.CommandText = mutation switch
			{
				"version" => "UPDATE backup_metadata SET value='13' WHERE key='export_version'",
				"table" => "DROP TABLE historical_operation_archive",
				"effects-table" => "DROP TABLE organization_effect_archive",
				"columns" => "ALTER TABLE organization_effect_archive ADD COLUMN surprise TEXT",
				"history" => "DELETE FROM taxonomy_archive WHERE kind='head'",
				"membership" => "DELETE FROM organization_effect_archive WHERE kind='item'",
				_ => "SELECT 1"
			};
			await command.ExecuteNonQueryAsync();
			if (mutation == "read-authority")
			{
				var archive = await PortableTaxonomyArchive.ReadV14CandidateAsync(sqlite, CancellationToken.None);
				var json = JsonSerializer.SerializeToNode(archive.Effects.Items[0])!;
				json["ApplyOperationId"] = archive.History.Operations[0].OperationId;
				var item = json.Deserialize<OrganizationEffectItemEntity>()!;
				var effects = archive.Effects with
				{
					Items = [item],
					Manifests = [PortableTaxonomyArchive.SealEffectManifest(archive.Effects.Manifests[0].Manifest, [item])]
				};
				foreach (var row in PortableTaxonomyArchive.WriteEffectEvidence(effects))
				{
					command.CommandText = "UPDATE organization_effect_archive SET payload=$payload WHERE kind=$kind AND id=$id";
					command.Parameters.Clear();
					command.Parameters.AddWithValue("$payload", row.Payload);
					command.Parameters.AddWithValue("$kind", row.Kind);
					command.Parameters.AddWithValue("$id", row.Id);
					await command.ExecuteNonQueryAsync();
				}
			}
			if (mutation == "none")
			{
				var archive = await PortableTaxonomyArchive.ReadV14CandidateAsync(sqlite, CancellationToken.None);
				archive.Effects.Manifests.Should().ContainSingle();
				archive.Effects.Items.Should().ContainSingle();
				archive.History.Revisions.Should().HaveCount(2);
				string currentPath = await service.ExportToSqliteAsync();
				try
				{
					await using var current = new SqliteConnection($"Data Source={currentPath};Pooling=False");
					await current.OpenAsync();
					await using var metadata = current.CreateCommand();
					metadata.CommandText = "SELECT value FROM backup_metadata WHERE key='export_version'";
					(await metadata.ExecuteScalarAsync()).Should().Be("13");
					metadata.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='organization_effect_archive'";
					Convert.ToInt64(await metadata.ExecuteScalarAsync()).Should().Be(0);
				}
				finally { File.Delete(currentPath); }
			}
			else
			{
				Func<Task> read = () => PortableTaxonomyArchive.ReadV14CandidateAsync(sqlite, CancellationToken.None);
				await read.Should().ThrowAsync<InvalidDataException>();
			}
			PortableBackupFormat.CurrentVersion.Should().Be(13);
			FluentActions.Invoking(() => PortableBackupFormat.RequiredTablesFor(14)).Should().Throw<InvalidOperationException>();
		}
		finally { File.Delete(path); }
	}
}
