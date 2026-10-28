using System.Text.Json;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	[Fact]
	public async Task Effects_CandidateSqliteExportSealsAndStrictlyReadsEvidence()
	{
		var (state, evidence) = EffectState();
		evidence.Outcomes.Add(new()
		{
			ManifestId = evidence.Items[0].ManifestId,
			ItemId = evidence.Items[0].ItemId,
			Phase = "apply",
			Disposition = "skipped",
			Reason = "source changed",
			ItemRevision = Guid.NewGuid(),
			ReceiptRevision = Guid.NewGuid(),
			RecordedAt = evidence.Manifests[0].Manifest.CapturedAt
		});
		var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
		await using var db = new ApplicationDbContext(options);
		db.OrganizationEffectManifests.AddRange(evidence.Manifests.Select(m => m.Manifest));
		db.OrganizationEffectItems.AddRange(evidence.Items);
		db.OrganizationEffectOutcomes.AddRange(evidence.Outcomes);
		await db.SaveChangesAsync();
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await PortableTaxonomyArchive.ExportEffectsCandidateAsync(db, sqlite, state, CancellationToken.None);
		var actual = await PortableTaxonomyArchive.ReadEffectsCandidateAsync(sqlite, state, CancellationToken.None);
		JsonSerializer.Serialize(actual).Should().Be(JsonSerializer.Serialize(evidence));
		await using var command = sqlite.CreateCommand();
		command.CommandText = "SELECT payload FROM organization_effect_archive WHERE kind='manifest'";
		((string)(await command.ExecuteScalarAsync())!).Should().NotContain("WriteTransaction");
		command.CommandText = "DELETE FROM organization_effect_archive WHERE kind='item'";
		await command.ExecuteNonQueryAsync();
		Func<Task> read = () => PortableTaxonomyArchive.ReadEffectsCandidateAsync(sqlite, state, CancellationToken.None);
		await read.Should().ThrowAsync<InvalidDataException>().WithMessage("*sealed membership*");
		PortableBackupFormat.CurrentVersion.Should().Be(13);
		PortableBackupFormat.RequiredTablesFor(13).Should().NotContain("organization_effect_archive");
	}

	[Theory]
	[InlineData("kind")]
	[InlineData("id")]
	public async Task Effects_CandidateSqliteReaderBoundsKeysIncludingEmbeddedNullSuffix(string field)
	{
		var (state, evidence) = EffectState();
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await using var command = sqlite.CreateCommand();
		command.CommandText = "CREATE TABLE organization_effect_archive(kind TEXT,id TEXT,payload TEXT)";
		await command.ExecuteNonQueryAsync();
		var row = PortableTaxonomyArchive.WriteEffectEvidence(evidence)[0];
		// Concatenate inside SQLite: parameter binding itself can truncate strings at NUL.
		command.CommandText = "INSERT INTO organization_effect_archive VALUES("
			+ "$kind || CASE WHEN $field='kind' THEN char(0) || $suffix ELSE '' END,"
			+ "$id || CASE WHEN $field='id' THEN char(0) || $suffix ELSE '' END,$payload)";
		command.Parameters.AddWithValue("$kind", row.Kind);
		command.Parameters.AddWithValue("$id", row.Id);
		command.Parameters.AddWithValue("$field", field);
		command.Parameters.AddWithValue("$suffix", new string('x', 1024 * 1024));
		command.Parameters.AddWithValue("$payload", row.Payload);
		await command.ExecuteNonQueryAsync();
		Func<Task> read = () => PortableTaxonomyArchive.ReadEffectsCandidateAsync(sqlite, state, CancellationToken.None);
		await read.Should().ThrowAsync<InvalidDataException>().WithMessage("Invalid or oversized effect archive row.");
	}

	[Fact]
	public async Task Effects_CandidateExportRollsBackTableAndRowsWhenAnInsertFails()
	{
		var (state, evidence) = EffectState();
		var node = JsonSerializer.SerializeToNode(evidence.Items[0])!;
		node["RawDescription"] = new string('x', 50_000);
		var item = node.Deserialize<Infrastructure.Entities.Taxonomy.OrganizationEffectItemEntity>()!;
		var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
		await using var db = new ApplicationDbContext(options);
		db.OrganizationEffectManifests.Add(evidence.Manifests[0].Manifest);
		db.OrganizationEffectItems.Add(item);
		await db.SaveChangesAsync();
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await using var command = sqlite.CreateCommand();
		// Space for the schema and table pages, but insufficient space for the large item.
		command.CommandText = "PRAGMA page_size=4096; PRAGMA max_page_count=3";
		await command.ExecuteNonQueryAsync();
		Func<Task> export = () => PortableTaxonomyArchive.ExportEffectsCandidateAsync(db, sqlite, state, CancellationToken.None);
		var error = await export.Should().ThrowAsync<SqliteException>();
		error.Which.SqliteErrorCode.Should().Be(13); // SQLITE_FULL
		command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='organization_effect_archive'";
		Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(0);
	}

	[Theory]
	[InlineData("duplicate")]
	[InlineData("unknown")]
	[InlineData("oversized")]
	public async Task Effects_CandidateSqliteReaderRejectsUntrustedRows(string mutation)
	{
		var (state, evidence) = EffectState();
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await using var command = sqlite.CreateCommand();
		// Deliberately omit a primary key: strict reading cannot trust a producer's schema constraints.
		command.CommandText = "CREATE TABLE organization_effect_archive(kind TEXT,id TEXT,payload TEXT)";
		await command.ExecuteNonQueryAsync();
		var row = PortableTaxonomyArchive.WriteEffectEvidence(evidence)[0];
		command.CommandText = "INSERT INTO organization_effect_archive VALUES($kind,$id,$payload)";
		command.Parameters.AddWithValue("$kind", mutation == "unknown" ? "reservation" : row.Kind);
		command.Parameters.AddWithValue("$id", row.Id);
		command.Parameters.AddWithValue("$payload", mutation == "oversized" ? new string('x', PortableTaxonomyArchive.MaximumEffectPayloadBytes + 1) : row.Payload);
		await command.ExecuteNonQueryAsync();
		if (mutation == "duplicate") { await command.ExecuteNonQueryAsync(); }
		Func<Task> read = () => PortableTaxonomyArchive.ReadEffectsCandidateAsync(sqlite, state, CancellationToken.None);
		await read.Should().ThrowAsync<InvalidDataException>();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Effects_RejectsOversizedDecisionBeforeJsonParsing(bool multibyte)
	{
		string json = multibyte ? new string('é', PortableTaxonomyArchive.MaximumEffectDecisionBytes / 2 + 1)
			: new string('x', PortableTaxonomyArchive.MaximumEffectDecisionBytes + 1);
		FluentActions.Invoking(() => PortableTaxonomyArchive.CanonicalEffectDecision(json))
			.Should().Throw<InvalidDataException>().WithMessage("*size limit*");
	}

	[Fact]
	public void Effects_BoundsNumericWorkBeforeBigIntegerParsingWithoutRounding()
	{
		string exponent = new('9', PortableTaxonomyArchive.MaximumEffectNumberCharacters);
		FluentActions.Invoking(() => PortableTaxonomyArchive.CanonicalEffectDecision("{\"n\":1e" + exponent + "}"))
			.Should().Throw<InvalidDataException>().WithMessage("*number exceeds size limit*");
		PortableTaxonomyArchive.CanonicalEffectDecision("{\"n\":1e" + new string('9', 100) + "}")
			.Should().Be("{\"n\":1e" + new string('9', 100) + "}");
	}
}
