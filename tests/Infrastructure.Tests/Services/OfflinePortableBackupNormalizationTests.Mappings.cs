using System.Text.Json;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

public sealed partial class OfflinePortableBackupNormalizationTests
{
	[Fact]
	public async Task V11Normalization_PreservesEveryOriginalApprovalRevocationRenewalFieldAndHead()
	{
		string source = await CreateAsync(11, 11);
		var state = PortableTaxonomyArchiveTests.MappingState();
		var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
		await using (var db = new ApplicationDbContext(options))
		{
			await db.Database.EnsureCreatedAsync();
			await PortableTaxonomyArchive.RestoreAsync(db, state, [], CancellationToken.None);
			await using var sqlite = Open(source);
			await sqlite.OpenAsync();
			await PortableTaxonomyArchive.ExportAsync(db, sqlite, CancellationToken.None);
			// ExportAsync writes the current v12 archives. This fixture represents a
			// genuine v11 file, which predates historical operations and source revisions.
			await using var removeV12Tables = sqlite.CreateCommand();
			removeV12Tables.CommandText = "DROP TABLE historical_operation_archive; DROP TABLE source_revision_archive";
			await removeV12Tables.ExecuteNonQueryAsync();
			// This fixture's domain rows were inserted directly into SQLite. Mirror
			// their active deletion evidence rather than the empty in-memory domain.
			await using var clear = sqlite.CreateCommand();
			clear.CommandText = "DELETE FROM portable_deletion_state";
			await clear.ExecuteNonQueryAsync();
			foreach (string table in new[] { "receipts", "receipt_items", "item_templates" })
			{
				foreach (var row in await QueryAsync(source, $"SELECT id FROM {table}"))
				{
					await using var insert = sqlite.CreateCommand();
					insert.CommandText = "INSERT INTO portable_deletion_state VALUES($kind,$id,$payload)";
					insert.Parameters.AddWithValue("$kind", table);
					insert.Parameters.AddWithValue("$id", row[0]);
					insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new PortableTaxonomyArchive.DeletionState(table, Guid.Parse(row[0]), null, null, null, null)));
					await insert.ExecuteNonQueryAsync();
				}
			}
		}
		byte[] original = await File.ReadAllBytesAsync(source);
		const string mappingSql = "SELECT kind,id,payload FROM category_mapping_archive ORDER BY kind,id";
		var evidence = await QueryAsync(source, mappingSql);
		evidence.Should().HaveCount(4);
		string manifest = await SaveAsync(await ManifestAsync(source, true));
		await service.ApplyAsync(source, manifest, Output);
		(await QueryAsync(Output, mappingSql)).Should().BeEquivalentTo(evidence, options => options.WithStrictOrdering());
		(await new OfflinePortableBackupInspection().InspectAsync(Output)).FormatVersion.Should().Be(11);
		await service.VerifyAsync(source, manifest, Output);
		(await service.ApplyAsync(source, manifest, Output)).Unchanged.Should().BeTrue();
		(await File.ReadAllBytesAsync(source)).Should().Equal(original);
		await using var output = Open(Output);
		await output.OpenAsync();
		var restored = await PortableTaxonomyArchive.ReadAsync(output, CancellationToken.None, 11);
		restored.MappingRevisions.Should().BeEquivalentTo(state.MappingRevisions);
		restored.MappingHeads.Should().BeEquivalentTo(state.MappingHeads);
	}
}
