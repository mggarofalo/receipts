using System.Security.Cryptography;
using Application.Normalization;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.Tests.Services;

public sealed class OfflinePortableBackupInspectionTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "offline-inspection-tests-" + Guid.NewGuid().ToString("N"));
	private readonly OfflinePortableBackupInspection _service = new();
	private static readonly Guid Receipt = Guid.Parse("10000000-0000-0000-0000-000000000001");
	private static readonly Guid Item = Guid.Parse("20000000-0000-0000-0000-000000000001");
	private static readonly Guid OtherItem = Guid.Parse("20000000-0000-0000-0000-000000000002");
	private static readonly Guid Target = Guid.Parse("30000000-0000-0000-0000-000000000001");
	private static readonly Guid Template = Guid.Parse("40000000-0000-0000-0000-000000000001");

	public OfflinePortableBackupInspectionTests() => Directory.CreateDirectory(_directory);

	private async Task<string> CreateAsync(int version = 8)
	{
		string path = Path.Combine(_directory, Guid.NewGuid() + ".db");
		await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
		await connection.OpenAsync();
		// Use the exporter's DDL: no reduced or invented replacement schema.
		await BackupService.CreateSchemaAsync(connection, CancellationToken.None);
		await using var command = connection.CreateCommand();
		command.CommandText = $"""
            INSERT INTO backup_metadata VALUES('export_version','{version}');
            INSERT INTO backup_metadata VALUES('format','receipts-sqlite-backup');
            INSERT INTO normalized_descriptions(id,canonical_name,status,created_at) VALUES('{Target}','Legacy name','Active','2026-10-24');
            INSERT INTO receipts(id,location,date,tax_amount,tax_amount_currency) VALUES('{Receipt}',' Shop ','2026-10-24','0.1000000000000000000000000001','USD');
            INSERT INTO receipt_items(id,receipt_id,receipt_item_code,description,quantity,unit_price,unit_price_currency,total_amount,total_amount_currency,category,normalized_description_id)
                VALUES('{Item}','{Receipt}',' 42 ',' Milk ','0.5','0.2','USD','0.1000000000000000000000000001','USD','Food','{Target}');
            INSERT INTO receipt_items(id,receipt_id,receipt_item_code,description,quantity,unit_price,unit_price_currency,total_amount,total_amount_currency,category)
                VALUES('{OtherItem}','{Receipt}','42','Milk','-0.25','0.8','USD','-0.2','USD','Food');
            INSERT INTO item_templates(id,name,default_item_code,normalized_description_id) VALUES('{Template}',' Milk ',' 42 ','{Target}');
            CREATE TABLE extra_facts(id TEXT, payload TEXT, number INTEGER);
            INSERT INTO extra_facts VALUES('independent','original',7);
            """;
		await command.ExecuteNonQueryAsync();
		if (version == 8)
		{
			// Exact normalization_catalog DDL used by PortableCatalogArchive.ExportAsync.
			command.CommandText = "CREATE TABLE normalization_catalog (kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(kind,id))";
			await command.ExecuteNonQueryAsync();
		}
		return path;
	}

	private static async Task EditAsync(string path, string sql)
	{
		await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = false }.ToString());
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = sql;
		await command.ExecuteNonQueryAsync();
	}

	[Theory]
	[InlineData(6)]
	[InlineData(8)]
	public async Task InspectAsync_SupportedExport_PreservesBytesExactMoneyAndUnverifiedHistory(int version)
	{
		string path = await CreateAsync(version);
		byte[] before = await File.ReadAllBytesAsync(path);
		var report = await _service.InspectAsync(path);
		(await File.ReadAllBytesAsync(path)).Should().Equal(before);
		report.SourceSha256.Should().Be(Convert.ToHexString(SHA256.HashData(before)));
		report.FormatVersion.Should().Be(version);
		report.RowCounts["receipt_items"].Should().Be(2);
		report.ReviewStateCounts["NeedsReview"].Should().Be(2, "legacy active links convey no reviewed authority");
		report.CurrencyTotals.Should().Contain(new OfflineCurrencyTotal("receipt_items", "USD", -0.0999999999999999999999999999m));
		report.CurrencyTotals.Should().Contain(new OfflineCurrencyTotal("receipts", "USD", 0.1000000000000000000000000001m));
		report.ScopedSourceCount.Should().Be(1);
		var item = report.Observations.Single(o => o.EntityId == Item);
		item.Source.Should().Be(new NormalizationSourceKey("Shop", "Milk", "42"));
		item.SourceHash.Should().Be(NormalizationCatalogPolicy.Hash(new("Shop", "Milk", "42")));
		item.LinkedCanonicalId.Should().Be(Target);
		report.Observations.Single(o => o.EntityId == Template).Source.Location.Should().BeEmpty();
		Directory.GetFiles(_directory).Should().ContainSingle();
	}

	[Fact]
	public async Task VerifyBaseline_NormalizationOnlyAndV6ToV8Upgrade_Accepts()
	{
		string path = await CreateAsync(6);
		var before = await _service.InspectAsync(path);
		await EditAsync(path, $$"""
            UPDATE normalized_descriptions SET canonical_name='Reviewed milk',display_label='Milk';
            UPDATE receipt_items SET normalized_description_id=NULL,normalized_description_match_score='0.99';
            UPDATE item_templates SET normalized_description_id=NULL;
            UPDATE backup_metadata SET value='8' WHERE key='export_version';
            CREATE TABLE normalization_catalog (kind TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(kind,id));
            INSERT INTO normalization_catalog VALUES('review_evidence','{{Item}}','{"State":0}');
            """);
		var after = await _service.InspectAsync(path);
		after.SourceSha256.Should().NotBe(before.SourceSha256);
		Action verify = () => _service.VerifyBaseline(before, after);
		verify.Should().NotThrow();
	}

	[Theory]
	[InlineData("'b3JpZ2luYWw='", "X'6F726967696E616C'")]
	[InlineData("7", "7.0")]
	public async Task VerifyBaseline_FactualStorageClassChange_Rejects(string original, string replacement)
	{
		string baselinePath = await CreateAsync();
		// No column affinity: retain the exact SQLite storage classes supplied by each literal.
		await EditAsync(baselinePath, $"ALTER TABLE extra_facts ADD COLUMN storage_value; UPDATE extra_facts SET storage_value={original}");
		string candidatePath = Path.Combine(_directory, Guid.NewGuid() + ".db");
		File.Copy(baselinePath, candidatePath);
		await EditAsync(candidatePath, $"UPDATE extra_facts SET storage_value={replacement}");

		var baseline = await _service.InspectAsync(baselinePath);
		var candidate = await _service.InspectAsync(candidatePath);

		using (new FluentAssertions.Execution.AssertionScope())
		{
			candidate.FactualTableSha256["extra_facts"].Should().NotBe(baseline.FactualTableSha256["extra_facts"]);
			candidate.FactualBaselineSha256.Should().NotBe(baseline.FactualBaselineSha256);
			Action verify = () => _service.VerifyBaseline(baseline, candidate);
			verify.Should().Throw<InvalidDataException>();
		}
	}

	[Theory]
	[InlineData("UPDATE receipt_items SET description='Changed'", "receipt_items")]
	[InlineData("UPDATE receipt_items SET quantity='0.75'", "receipt_items")]
	[InlineData("UPDATE receipt_items SET total_amount='1.01'", "receipt_items")]
	[InlineData("UPDATE item_templates SET name='Changed'", "item_templates")]
	[InlineData("UPDATE receipts SET date='2026-10-23'", "receipts")]
	[InlineData("UPDATE extra_facts SET payload=NULL", "extra_facts")]
	[InlineData("UPDATE extra_facts SET number=8", "extra_facts")]
	[InlineData("ALTER TABLE extra_facts ADD COLUMN added TEXT", "extra_facts")]
	[InlineData("UPDATE receipt_items SET id='20000000-0000-0000-0000-000000000003' WHERE quantity='0.5'", "receipt_items")]
	public async Task VerifyBaseline_FactualTampering_Rejects(string mutation, string table)
	{
		string path = await CreateAsync();
		var before = await _service.InspectAsync(path);
		await EditAsync(path, mutation);
		var after = await _service.InspectAsync(path);
		after.FactualTableSha256[table].Should().NotBe(before.FactualTableSha256[table]);
		Action verify = () => _service.VerifyBaseline(before, after);
		verify.Should().Throw<InvalidDataException>();
	}

	[Theory]
	[InlineData("UPDATE receipt_items SET receipt_id='00000000-0000-0000-0000-000000000001'")]
	[InlineData("UPDATE receipt_items SET normalized_description_id='00000000-0000-0000-0000-000000000001'")]
	[InlineData("INSERT INTO normalization_catalog VALUES('review_evidence','00000000-0000-0000-0000-000000000001','{\"State\":1}')")]
	[InlineData("UPDATE backup_metadata SET value='7' WHERE key='export_version'")]
	[InlineData("DROP TABLE normalization_catalog")]
	public async Task InspectAsync_InvalidRelationshipsOrFormat_Rejects(string mutation)
	{
		string path = await CreateAsync();
		await EditAsync(path, mutation);
		Func<Task> inspect = async () => await _service.InspectAsync(path);
		await inspect.Should().ThrowAsync<InvalidDataException>();
	}

	[Theory]
	[InlineData("-wal")]
	[InlineData("-shm")]
	[InlineData("-journal")]
	public async Task InspectAsync_OpenBackupSidecar_RejectsWithoutChanges(string suffix)
	{
		string path = await CreateAsync();
		byte[] before = await File.ReadAllBytesAsync(path);
		await File.WriteAllTextAsync(path + suffix, "sidecar");
		Func<Task> inspect = async () => await _service.InspectAsync(path);
		await inspect.Should().ThrowAsync<InvalidDataException>();
		(await File.ReadAllBytesAsync(path)).Should().Equal(before);
	}

	[Fact]
	public async Task InspectAsync_TruncatedBackup_RejectsWithoutChanges()
	{
		string path = await CreateAsync();
		await File.WriteAllBytesAsync(path, (await File.ReadAllBytesAsync(path))[..100]);
		byte[] before = await File.ReadAllBytesAsync(path);
		Func<Task> inspect = async () => await _service.InspectAsync(path);
		await inspect.Should().ThrowAsync<SqliteException>();
		(await File.ReadAllBytesAsync(path)).Should().Equal(before);
	}

	[Fact]
	public async Task InspectAsync_UnknownCurrency_ReportsSeparateInventoryWithoutAssertingImportValidity()
	{
		// Inspection is a factual inventory. EUR is intentionally unsupported by the app;
		// accepting this inventory must never stand in for portable import validation.
		string path = await CreateAsync();
		await EditAsync(path, $"UPDATE receipt_items SET total_amount_currency='EUR' WHERE id='{OtherItem}'");
		var report = await _service.InspectAsync(path);
		report.CurrencyTotals.Should().Contain(new OfflineCurrencyTotal("receipt_items", "EUR", -0.2m));
		report.CurrencyTotals.Should().Contain(new OfflineCurrencyTotal("receipt_items", "USD", 0.1000000000000000000000000001m));
	}

	[Fact]
	public async Task VerifyBaseline_ValidButChangedReceiptRelationship_Rejects()
	{
		string path = await CreateAsync();
		await EditAsync(path, "INSERT INTO receipts(id,location,date,tax_amount,tax_amount_currency) VALUES('10000000-0000-0000-0000-000000000002','Shop','2026-10-24','0','USD')");
		var before = await _service.InspectAsync(path);
		await EditAsync(path, $"UPDATE receipt_items SET receipt_id='10000000-0000-0000-0000-000000000002' WHERE id='{Item}'");
		var after = await _service.InspectAsync(path);
		after.CurrencyTotals.Should().Equal(before.CurrencyTotals);
		Action verify = () => _service.VerifyBaseline(before, after);
		verify.Should().Throw<InvalidDataException>();
	}

	[Fact]
	public async Task ValidateManifest_AmbiguousScopeWithExplicitOneOffChoices_AcceptsWithoutRemembering()
	{
		var source = await _service.InspectAsync(await CreateAsync());
		var manifest = Manifest(source) with { Review = null };
		manifest = manifest with
		{
			Assignments = manifest.Assignments.Select((a, index) => index == 0
			? a with { RememberSource = false }
			: a with { CanonicalId = null, Exclude = true, ExclusionReason = "Cannot distinguish package", RememberSource = false }).ToArray()
		};
		Action validate = () => _service.ValidateManifest(manifest, source, false, Now);
		validate.Should().NotThrow();
		manifest.Assignments.Should().OnlyContain(a => !a.RememberSource);
	}
	private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
	private static OfflineNormalizationManifest Manifest(OfflineBackupInspection source)
	{
		var assignments = source.Observations.Where(o => o.EntityKind == "item").Select(o =>
			new OfflineAssignmentDecision(Guid.NewGuid(), "item", o.EntityId, o.Source, o.SourceHash, Target, false, true, null)).ToArray();
		var canonical = new OfflineCanonicalDecision(Guid.NewGuid(), Target, "Milk", null);
		return new(1, source.SourceSha256, source.FormatVersion, source.FactualBaselineSha256, "revision", "proposer", Now.AddHours(-1),
			[canonical], assignments, [], [], new("reviewer", Now, assignments.Select(a => new OfflineReviewedDecision(a.DecisionId, "Same scoped product"))
				.Append(new(canonical.DecisionId, "Readable name checked")).ToArray()));
	}

	[Fact]
	public async Task ValidateManifest_ReviewedWholeScopedSource_Accepts()
	{
		var source = await _service.InspectAsync(await CreateAsync());
		Action validate = () => _service.ValidateManifest(Manifest(source), source, true, Now);
		validate.Should().NotThrow();
	}

	[Theory]
	[InlineData("source-digest")]
	[InlineData("format")]
	[InlineData("baseline")]
	[InlineData("entity")]
	[InlineData("source-scope")]
	[InlineData("missing-name-approval")]
	[InlineData("incomplete-scope")]
	[InlineData("mixed-remember")]
	[InlineData("mixed-target")]
	public async Task ValidateManifest_MismatchedIdentityOrUnsafeScope_Rejects(string mutation)
	{
		var source = await _service.InspectAsync(await CreateAsync());
		var manifest = Manifest(source) with { Review = null };
		var assignment = manifest.Assignments[0];
		var other = manifest.Assignments[1];
		var wrongKey = new NormalizationSourceKey("Different shop", "Milk", "42");
		manifest = mutation switch
		{
			"source-digest" => manifest with { SourceSha256 = new string('A', 64) },
			"format" => manifest with { SourceFormatVersion = 6 },
			"baseline" => manifest with { FactualBaselineSha256 = new string('B', 64) },
			"entity" => manifest with { Assignments = [assignment with { EntityId = Guid.NewGuid() }, other] },
			"source-scope" => manifest with { Assignments = [assignment with { Source = wrongKey, SourceHash = NormalizationCatalogPolicy.Hash(wrongKey) }, other] },
			"missing-name-approval" => manifest with { CanonicalDecisions = [] },
			"incomplete-scope" => manifest with { Assignments = [assignment] },
			"mixed-remember" => manifest with { Assignments = [assignment, other with { RememberSource = false }] },
			_ => manifest with { Assignments = [assignment, other with { CanonicalId = null, Exclude = true, ExclusionReason = "Different size" }] }
		};
		Action validate = () => _service.ValidateManifest(manifest, source, false, Now);
		validate.Should().Throw<InvalidDataException>();
	}

	public void Dispose()
	{
		Directory.Delete(_directory, recursive: true);
	}
}
