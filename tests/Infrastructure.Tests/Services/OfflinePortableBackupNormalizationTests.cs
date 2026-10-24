using System.Security.Cryptography;
using System.Text.Json;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.Tests.Services;

public sealed partial class OfflinePortableBackupNormalizationTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "offline-apply-tests-" + Guid.NewGuid().ToString("N"));
	private readonly PortableBackupNormalizationService service = new();
	private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
	private static readonly Guid Beef = Id(100), Fragment = Id(101), Turkey = Id(102), Receipt = Id(200), Template = Id(300);
	private static readonly DateTimeOffset ReviewedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
	public OfflinePortableBackupNormalizationTests() => Directory.CreateDirectory(directory);

	private async Task<string> CreateAsync(int version = 6)
	{
		string path = Path.Combine(directory, "A.db");
		await using var db = Open(path);
		await db.OpenAsync();
		await BackupService.CreateSchemaAsync(db, CancellationToken.None);
		await using var command = db.CreateCommand();
		command.CommandText = $"""
            INSERT INTO backup_metadata VALUES('export_version','{version}');
            INSERT INTO backup_metadata VALUES('format','receipts-sqlite-backup');
            INSERT INTO normalized_descriptions(id,canonical_name,status,created_at) VALUES('{Beef}','BEEF FRANK','Active','2026-10-24'),('{Fragment}','bf frnk','Active','2026-10-24'),('{Turkey}','Turkey franks 8 count','Active','2026-10-24');
            INSERT INTO receipts(id,location,date,tax_amount,tax_amount_currency) VALUES('{Receipt}',' Shop ','2026-10-24','0.1000000000000000000000000001','USD');
            INSERT INTO item_templates(id,name,default_item_code,default_unit_price,default_unit_price_currency,normalized_description_id) VALUES('{Template}','BF FRANK','42','2.3456789','USD','{Fragment}');
            """;
		await command.ExecuteNonQueryAsync();
		for (int i = 1; i <= 5; i++)
		{
			command.CommandText = $"""
                INSERT INTO receipt_items(id,receipt_id,receipt_item_code,description,quantity,unit_price,unit_price_currency,total_amount,total_amount_currency,category,normalized_description_id)
                VALUES('{Id(i)}','{Receipt}','{(i <= 2 ? "42" : i.ToString())}','{(i <= 2 ? " BF FRANK " : i == 3 ? "TURKEY FRANK" : i == 4 ? "HOT DOG" : "Legacy purchase")}','{(i == 2 ? "-0.25" : "0.5")}','0.2','USD','{(i == 2 ? "-0.05" : "0.1")}','USD','Food','{(i == 3 ? Turkey : Fragment)}');
                """;
			await command.ExecuteNonQueryAsync();
		}
		if (version >= 8)
		{
			command.CommandText = "CREATE TABLE normalization_catalog(kind TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(kind,id))";
			await command.ExecuteNonQueryAsync();
			for (int i = 1; i <= 5; i++)
			{
				Guid decision = Id(400 + i);
				await WriteEvidenceAsync(db, "assignment", Id(i), new PortableCatalogArchive.Assignment(Id(i), i == 3 ? "reviewed" : "legacy_unknown", null, decision));
				await WriteEvidenceAsync(db, "review_evidence", Id(i), i == 3
					? new PortableCatalogArchive.ReviewEvidence(Id(i), NormalizationReviewState.Verified, NormalizationCatalogPolicy.Hash(new("Shop", "TURKEY FRANK", "3")), Turkey, "purchase_review", null, decision, "existing-reviewer", ReviewedAt.AddDays(-1), null)
					: new PortableCatalogArchive.ReviewEvidence(Id(i), NormalizationReviewState.NeedsReview, null, null, null, null, null, null, null, null));
			}
			await WriteEvidenceAsync(db, "operation", Id(800), new NormalizationCatalogOperationEntity { Id = Id(800), State = "applied", Actor = "historical-actor" });
		}
		return path;
	}

	private static SqliteConnection Open(string path) => new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
	private static async Task WriteEvidenceAsync<T>(SqliteConnection db, string kind, Guid id, T value)
	{
		await using var command = db.CreateCommand();
		command.CommandText = "INSERT INTO normalization_catalog VALUES($kind,$id,$payload)";
		command.Parameters.AddWithValue("$kind", kind);
		command.Parameters.AddWithValue("$id", id.ToString());
		command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value));
		await command.ExecuteNonQueryAsync();
	}

	private async Task<OfflineNormalizationManifest> ManifestAsync(string source, bool decisions)
	{
		var baseline = await new OfflinePortableBackupInspection().InspectAsync(source);
		OfflineCanonicalDecision[] names = decisions ? [new(Id(501), Beef, "Beef franks 8 count", "Beef franks"), new(Id(502), Turkey, "Turkey franks 8 count", null)] : [];
		OfflineAssignmentDecision Assignment(int id, Guid? target, bool remember, bool exclude = false)
		{
			// The semantic choice is fixed above; inspection supplies only the source binding.
			var observation = baseline.Observations.Single(o => o.EntityId == Id(id));
			return new(Id(600 + id), observation.EntityKind, Id(id), observation.Source, observation.SourceHash, target, exclude, remember, exclude ? "Package cannot be distinguished" : null);
		}
		OfflineAssignmentDecision[] assignments = decisions ? [Assignment(1, Beef, true), Assignment(2, Beef, true), Assignment(3, Turkey, false), Assignment(4, null, false, true), Assignment(300, Beef, false)] : [];
		OfflineConsolidationDecision[] merges = decisions ? [new(Id(700), Fragment, Beef)] : [];
		return new(1, baseline.SourceSha256, baseline.FormatVersion, baseline.FactualBaselineSha256, "synthetic-revision", "synthetic-proposer", ReviewedAt.AddMinutes(-1), names, assignments, merges,
			[new("item", Id(5), "Legacy purchase remains unreviewed")], new("synthetic-reviewer", ReviewedAt,
			names.Select(n => new OfflineReviewedDecision(n.DecisionId, "Readable product name checked")).Concat(assignments.Select(a => new OfflineReviewedDecision(a.DecisionId, "Individual source and product checked"))).Concat(merges.Select(m => new OfflineReviewedDecision(m.DecisionId, "Same beef product, turkey remains distinct"))).ToArray()));
	}

	private async Task<string> SaveAsync(OfflineNormalizationManifest manifest)
	{
		string path = Path.Combine(directory, "reviewed.json");
		await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, OfflineNormalizationManifestPolicy.JsonOptions));
		return path;
	}
	private string Output => Path.Combine(directory, "B.db");
	private static async Task<string[][]> QueryAsync(string path, string sql)
	{
		await using var db = Open(path);
		await db.OpenAsync();
		await using var command = db.CreateCommand();
		command.CommandText = sql;
		await using var rows = await command.ExecuteReaderAsync();
		List<string[]> result = [];
		while (await rows.ReadAsync()) { result.Add(Enumerable.Range(0, rows.FieldCount).Select(i => rows.IsDBNull(i) ? "<null>" : Convert.ToString(rows.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!).ToArray()); }
		return result.ToArray();
	}
	private static async Task EditAsync(string path, string sql)
	{
		await using var db = Open(path); await db.OpenAsync();
		await using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task Apply_V6WithoutDecisions_PreservesLegacyLinksWithoutBlessingThem()
	{
		string source = await CreateAsync(); byte[] before = await File.ReadAllBytesAsync(source);
		string manifest = await SaveAsync(await ManifestAsync(source, false));
		var result = await service.ApplyAsync(source, manifest, Output);
		result.Unchanged.Should().BeFalse();
		result.ReviewStateCounts["NeedsReview"].Should().Be(5);
		(await QueryAsync(Output, "SELECT normalized_description_id FROM receipt_items ORDER BY id")).Select(r => r[0]).Should().Equal(Fragment.ToString(), Fragment.ToString(), Turkey.ToString(), Fragment.ToString(), Fragment.ToString());
		var evidence = await QueryAsync(Output, "SELECT payload FROM normalization_catalog WHERE kind='assignment'");
		evidence.Should().HaveCount(5).And.OnlyContain(row => row[0].Contains("legacy_unknown"));
		(await File.ReadAllBytesAsync(source)).Should().Equal(before);
		(await QueryAsync(Output, "SELECT value FROM backup_metadata WHERE key='export_version'")).Single()[0].Should().Be("9");
	}

	[Theory]
	[InlineData(6)]
	[InlineData(8)]
	[InlineData(9)]
	public async Task Apply_ReviewedDecisions_ConsolidatesOnlyBeefPreservesFactsAndIsByteIdempotent(int version)
	{
		string source = await CreateAsync(version); byte[] before = await File.ReadAllBytesAsync(source);
		const string factsSql = "SELECT id,receipt_id,receipt_item_code,description,quantity,unit_price,unit_price_currency,total_amount,total_amount_currency,category,subcategory FROM receipt_items ORDER BY id";
		var facts = await QueryAsync(source, factsSql);
		string manifest = await SaveAsync(await ManifestAsync(source, true));
		var first = await service.ApplyAsync(source, manifest, Output);
		first.ReviewStateCounts["Verified"].Should().Be(3);
		first.ReviewStateCounts["Excluded"].Should().Be(1);
		first.ReviewStateCounts["NeedsReview"].Should().Be(1);
		(await QueryAsync(Output, "SELECT id,canonical_name FROM normalized_descriptions ORDER BY id")).Should().BeEquivalentTo(new[] { new[] { Beef.ToString(), "Beef franks 8 count" }, new[] { Turkey.ToString(), "Turkey franks 8 count" } });
		(await QueryAsync(Output, "SELECT normalized_description_id FROM receipt_items ORDER BY id")).Select(r => r[0]).Should().Equal(Beef.ToString(), Beef.ToString(), Turkey.ToString(), "<null>", Beef.ToString());
		(await QueryAsync(Output, "SELECT normalized_description_id FROM item_templates")).Single()[0].Should().Be(Beef.ToString());
		var mappings = (await QueryAsync(Output, "SELECT payload FROM normalization_catalog WHERE kind='mapping'")).Select(r => JsonSerializer.Deserialize<NormalizationSourceMappingEntity>(r[0])!).ToArray();
		mappings.Should().ContainSingle(); mappings[0].TargetId.Should().Be(Beef); mappings[0].Location.Should().Be("Shop"); mappings[0].Description.Should().Be("BF FRANK"); mappings[0].ItemCode.Should().Be("42");
		var reviews = (await QueryAsync(Output, "SELECT payload FROM normalization_catalog WHERE kind='review_evidence'")).Select(r => JsonSerializer.Deserialize<PortableCatalogArchive.ReviewEvidence>(r[0])!).ToDictionary(r => r.Id);
		reviews[Id(3)].Reason.Should().Be("purchase_review"); reviews[Id(4)].Reason.Should().Be("purchase_exclusion"); reviews[Id(5)].State.Should().Be(NormalizationReviewState.NeedsReview);
		reviews[Id(1)].ReviewedBy.Should().Be($"offline-agent:synthetic-reviewer;manifest-sha256:{first.ManifestSha256}");
		(await QueryAsync(Output, factsSql)).Should().BeEquivalentTo(facts, options => options.WithStrictOrdering());
		(await QueryAsync(Output, "SELECT tax_amount,tax_amount_currency FROM receipts")).Single().Should().Equal("0.1000000000000000000000000001", "USD");
		(await QueryAsync(Output, "PRAGMA integrity_check")).Single()[0].Should().Be("ok");
		(await QueryAsync(Output, "PRAGMA foreign_key_check")).Should().BeEmpty();
		byte[] output = await File.ReadAllBytesAsync(Output);
		var second = await service.ApplyAsync(source, manifest, Output);
		second.Unchanged.Should().BeTrue(); second.OutputSha256.Should().Be(first.OutputSha256);
		(await File.ReadAllBytesAsync(Output)).Should().Equal(output);
		(await service.VerifyAsync(source, manifest, Output)).OutputSha256.Should().Be(Convert.ToHexString(SHA256.HashData(output)));
		(await File.ReadAllBytesAsync(Output + ".manifest.json")).Should().Equal(await File.ReadAllBytesAsync(manifest));
		(await File.ReadAllBytesAsync(source)).Should().Equal(before);
	}

	[Fact]
	public async Task Apply_V8WithoutDecisions_PreservesExistingReviewedEvidenceExactly()
	{
		string source = await CreateAsync(8);
		string manifest = await SaveAsync(await ManifestAsync(source, false));
		var before = await QueryAsync(source, "SELECT kind,id,payload FROM normalization_catalog ORDER BY kind,id");
		await service.ApplyAsync(source, manifest, Output);
		(await QueryAsync(Output, "SELECT kind,id,payload FROM normalization_catalog ORDER BY kind,id")).Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
		(await service.VerifyAsync(source, manifest, Output)).ReviewStateCounts["Verified"].Should().Be(1);
	}

	[Theory]
	[InlineData("source-fingerprint")]
	[InlineData("missing-review")]
	[InlineData("unknown-kind")]
	public async Task Apply_CorruptedV8Evidence_RejectsBeforeCopying(string corruption)
	{
		string source = await CreateAsync(8);
		string sql = corruption switch
		{
			"source-fingerprint" => $"UPDATE normalization_catalog SET payload=json_set(payload,'$.SourceHash','{new string('A', 64)}') WHERE kind='review_evidence' AND id='{Id(3)}'",
			"missing-review" => $"DELETE FROM normalization_catalog WHERE kind='review_evidence' AND id='{Id(3)}'",
			_ => "UPDATE normalization_catalog SET kind='unsupported' WHERE kind='operation'"
		};
		await EditAsync(source, sql);
		string manifest = await SaveAsync(await ManifestAsync(source, false));
		byte[] before = await File.ReadAllBytesAsync(source);
		Func<Task> apply = () => service.ApplyAsync(source, manifest, Output);
		await apply.Should().ThrowAsync<InvalidDataException>();
		(await File.ReadAllBytesAsync(source)).Should().Equal(before);
		Directory.GetFiles(directory, "B.db*").Should().BeEmpty();
	}

	[Fact]
	public async Task Verify_CorruptedReviewWithUpdatedOutputChecksum_StillRejectsAuthorityMismatch()
	{
		string source = await CreateAsync(); string manifest = await SaveAsync(await ManifestAsync(source, true));
		await service.ApplyAsync(source, manifest, Output);
		await EditAsync(Output, $"UPDATE normalization_catalog SET payload=json_set(payload,'$.SourceHash','{new string('A', 64)}') WHERE kind='review_evidence' AND id='{Id(1)}'");
		var lineage = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(Output + ".lineage.json"))!;
		lineage["OutputSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Output)));
		await File.WriteAllTextAsync(Output + ".lineage.json", JsonSerializer.Serialize(lineage));
		Func<Task> verify = () => service.VerifyAsync(source, manifest, Output);
		await verify.Should().ThrowAsync<InvalidDataException>().WithMessage("*review evidence*");
	}

	[Fact]
	public async Task Verify_ValidButWrongIdentityWithUpdatedChecksum_RejectsDisagreementWithReviewedManifest()
	{
		string source = await CreateAsync(); string manifest = await SaveAsync(await ManifestAsync(source, true));
		await service.ApplyAsync(source, manifest, Output);
		// Both the item and structurally valid one-off evidence are changed to the wrong product.
		// A checksum/evidence-only verifier would accept this, despite the reviewed turkey choice.
		await EditAsync(Output, $"UPDATE receipt_items SET normalized_description_id='{Beef}' WHERE id='{Id(3)}'; UPDATE normalization_catalog SET payload=json_set(payload,'$.TargetId','{Beef}') WHERE kind='review_evidence' AND id='{Id(3)}'");
		var lineage = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(Output + ".lineage.json"))!;
		lineage["OutputSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Output)));
		await File.WriteAllTextAsync(Output + ".lineage.json", JsonSerializer.Serialize(lineage));
		Func<Task> verify = () => service.VerifyAsync(source, manifest, Output);
		await verify.Should().ThrowAsync<InvalidDataException>("verify must enforce the reviewed turkey identity, independently of consistent file checksums and evidence");
	}

	[Fact]
	public async Task Apply_ReviewedNameCollidesWithUnmergedProduct_RejectsInsteadOfPublishingUnrestorableArchive()
	{
		string source = await CreateAsync(); var manifest = await ManifestAsync(source, true);
		manifest = manifest with { CanonicalDecisions = manifest.CanonicalDecisions.Select(d => d.CanonicalId == Beef ? d with { CanonicalName = "Turkey franks 8 count" } : d).ToArray() };
		string path = await SaveAsync(manifest);
		Func<Task> apply = () => service.ApplyAsync(source, path, Output);
		await apply.Should().ThrowAsync<InvalidDataException>("the app database requires distinct canonical names and these products were deliberately not merged");
		Directory.GetFiles(directory, "B.db*").Should().BeEmpty();
	}

	[Fact]
	public async Task Apply_ConsolidatesPreviouslyMappedIdentity_RebindsReviewedEvidenceWithoutBlessingLegacyNeighbors()
	{
		string source = await CreateAsync(8);
		var scope = new NormalizationSourceKey("Shop", "BF FRANK", "42");
		var mapping = new NormalizationSourceMappingEntity
		{
			Id = Id(900),
			Revision = Id(901),
			Location = scope.Location,
			Description = scope.Description,
			ItemCode = scope.ItemCode,
			SourceHash = NormalizationCatalogPolicy.Hash(scope),
			TargetId = Fragment,
			ApprovedBy = "original-reviewer",
			ApprovedAt = ReviewedAt.AddDays(-1)
		};
		await EditAsync(source, $"DELETE FROM normalization_catalog WHERE id='{Id(1)}'");
		await using (var db = Open(source))
		{
			await db.OpenAsync();
			await WriteEvidenceAsync(db, "mapping", mapping.Id, mapping);
			await WriteEvidenceAsync(db, "assignment", Id(1), new PortableCatalogArchive.Assignment(Id(1), "mapped", mapping.Id, Id(902)));
			await WriteEvidenceAsync(db, "review_evidence", Id(1), new PortableCatalogArchive.ReviewEvidence(Id(1), NormalizationReviewState.Verified, mapping.SourceHash, Fragment, "approved_mapping", null, Id(902), "original-reviewer", ReviewedAt.AddDays(-1), mapping.Revision));
		}
		var manifest = await ManifestAsync(source, false);
		var name = new OfflineCanonicalDecision(Id(910), Beef, "Beef franks 8 count", null);
		var merge = new OfflineConsolidationDecision(Id(911), Fragment, Beef);
		manifest = manifest with
		{
			CanonicalDecisions = [name],
			Consolidations = [merge],
			Review = new("synthetic-reviewer", ReviewedAt,
			[new(name.DecisionId, "Reviewed readable beef name"), new(merge.DecisionId, "Established identical beef product")])
		};
		string manifestPath = await SaveAsync(manifest);
		await service.ApplyAsync(source, manifestPath, Output);
		var savedMapping = JsonSerializer.Deserialize<NormalizationSourceMappingEntity>((await QueryAsync(Output, "SELECT payload FROM normalization_catalog WHERE kind='mapping'")).Single()[0])!;
		savedMapping.Id.Should().Be(mapping.Id); savedMapping.TargetId.Should().Be(Beef); savedMapping.Revision.Should().NotBe(mapping.Revision);
		var review = JsonSerializer.Deserialize<PortableCatalogArchive.ReviewEvidence>((await QueryAsync(Output, $"SELECT payload FROM normalization_catalog WHERE kind='review_evidence' AND id='{Id(1)}'")).Single()[0])!;
		review.State.Should().Be(NormalizationReviewState.Verified); review.TargetId.Should().Be(Beef); review.MappingRevision.Should().Be(savedMapping.Revision); review.DecisionId.Should().NotBe(Id(902));
		var neighbor = JsonSerializer.Deserialize<PortableCatalogArchive.ReviewEvidence>((await QueryAsync(Output, $"SELECT payload FROM normalization_catalog WHERE kind='review_evidence' AND id='{Id(2)}'")).Single()[0])!;
		neighbor.State.Should().Be(NormalizationReviewState.NeedsReview);
		(await service.VerifyAsync(source, manifestPath, Output)).ReviewStateCounts["Verified"].Should().Be(2);
	}

	[Theory]
	[InlineData("source")]
	[InlineData("output")]
	[InlineData("manifest")]
	[InlineData("lineage")]
	[InlineData("missing-sidecar")]
	public async Task Verify_TamperingOrPartialArtifact_RejectsWithoutChangingSource(string corruption)
	{
		string source = await CreateAsync(); string manifest = await SaveAsync(await ManifestAsync(source, true));
		await service.ApplyAsync(source, manifest, Output);
		switch (corruption)
		{
			case "source": await EditAsync(source, "UPDATE receipt_items SET quantity='9'"); break;
			case "output": await EditAsync(Output, "UPDATE receipt_items SET total_amount='9'"); break;
			case "manifest": await File.AppendAllTextAsync(Output + ".manifest.json", " "); break;
			case "lineage": await File.WriteAllTextAsync(Output + ".lineage.json", "{}"); break;
			default: File.Delete(Output + ".manifest.json"); break;
		}
		byte[] before = await File.ReadAllBytesAsync(source);
		Func<Task> verify = () => service.VerifyAsync(source, manifest, Output);
		await verify.Should().ThrowAsync<Exception>();
		(await File.ReadAllBytesAsync(source)).Should().Equal(before);
		Directory.GetFiles(directory, "*.tmp*").Should().BeEmpty();
	}

	[Theory]
	[InlineData("UPDATE receipts SET tax_amount_currency='CAD'")]
	[InlineData("UPDATE receipt_items SET unit_price_currency='0'")]
	[InlineData("UPDATE receipt_items SET total_amount_currency='999'")]
	[InlineData("UPDATE item_templates SET default_unit_price_currency='EUR'")]
	[InlineData("ALTER TABLE receipts ADD COLUMN unexpected TEXT")]
	public async Task Apply_UnsupportedCurrencyOrSchema_RejectsBeforePublishing(string mutation)
	{
		string source = await CreateAsync(); await EditAsync(source, mutation);
		string manifest = await SaveAsync(await ManifestAsync(source, true)); byte[] before = await File.ReadAllBytesAsync(source);
		Func<Task> apply = () => service.ApplyAsync(source, manifest, Output);
		await apply.Should().ThrowAsync<InvalidDataException>();
		File.Exists(Output).Should().BeFalse(); Directory.GetFiles(directory, "B.db*").Should().BeEmpty();
		(await File.ReadAllBytesAsync(source)).Should().Equal(before);
	}

	[Theory]
	[InlineData("unreviewed")]
	[InlineData("incomplete-review")]
	[InlineData("source-hash")]
	[InlineData("source-sidecar")]
	[InlineData("output-sidecar")]
	[InlineData("clobber")]
	[InlineData("alias")]
	[InlineData("cancel")]
	public async Task Apply_InvalidInputOrCancellation_DoesNotClobberOrLeaveTemporaryArtifacts(string failure)
	{
		string source = await CreateAsync(); var reviewed = await ManifestAsync(source, true);
		reviewed = failure switch { "unreviewed" => reviewed with { Review = null }, "incomplete-review" => reviewed with { Review = reviewed.Review! with { Decisions = [] } }, "source-hash" => reviewed with { SourceSha256 = new string('A', 64) }, _ => reviewed };
		string manifest = await SaveAsync(reviewed); byte[] before = await File.ReadAllBytesAsync(source);
		string output = failure == "alias" ? Path.Combine(directory, ".", "A.db") : Output;
		if (failure == "source-sidecar") { await File.WriteAllTextAsync(source + "-wal", "occupied"); }
		if (failure == "output-sidecar") { await File.WriteAllTextAsync(Output + ".lineage.json", "occupied"); }
		if (failure == "clobber") { await File.WriteAllTextAsync(Output, "occupied"); }
		using var cancellation = new CancellationTokenSource(); if (failure == "cancel") { cancellation.Cancel(); }
		Func<Task> apply = () => service.ApplyAsync(source, manifest, output, cancellation.Token);
		await apply.Should().ThrowAsync<Exception>();
		(await File.ReadAllBytesAsync(source)).Should().Equal(before);
		Directory.GetFiles(directory, "*.tmp*").Should().BeEmpty();
		if (failure == "clobber") { (await File.ReadAllTextAsync(Output)).Should().Be("occupied"); }
		else { File.Exists(Output).Should().BeFalse(); }
		if (failure == "output-sidecar") { (await File.ReadAllTextAsync(Output + ".lineage.json")).Should().Be("occupied"); }
		else { File.Exists(Output + ".lineage.json").Should().BeFalse(); }
		File.Exists(Output + ".manifest.json").Should().BeFalse();
	}

	public void Dispose() => Directory.Delete(directory, recursive: true);
}
