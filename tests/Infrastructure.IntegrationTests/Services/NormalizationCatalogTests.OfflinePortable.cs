using System.Security.Cryptography;
using System.Text.Json;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task OfflinePortable_ConsolidationInvalidatesTrashedMappedPurchaseWithAuditBeforeUntrash()
	{
		await ResetAsync();
		var survivor = await CanonicalAsync("Beef franks 8 count");
		var loser = await CanonicalAsync("BF FRANK");
		var trashedIds = await ItemsAsync(Source, 1);
		Guid trashedId = trashedIds.Single();
		var catalog = Service();
		var preview = await catalog.PreviewAsync(Actor, Request(loser), CancellationToken.None);
		await catalog.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		var beforeTrash = (await ReadAsync(trashedIds)).Single();
		var trashedFacts = await FactsAsync(trashedIds);
		beforeTrash.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		beforeTrash.NormalizationOrigin.Should().Be("mapped");
		beforeTrash.NormalizationMappingId.Should().NotBeNull();
		var deletedAt = DateTimeOffset.UtcNow;
		await using (var db = fixture.CreateDbContext())
		{
			(await db.ReceiptItems.SingleAsync(x => x.Id == trashedId)).DeletedAt = deletedAt;
			await db.SaveChangesAsync();
		}
		// Soft-delete already invalidates review state in PostgreSQL; stale authority still needs clearing.
		await using (var db = fixture.CreateDbContext())
		{
			var afterTrash = await db.ReceiptItems.IgnoreQueryFilters().SingleAsync(x => x.Id == trashedId);
			afterTrash.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
			afterTrash.NormalizationReviewReason.Should().Be("unavailable");
			afterTrash.NormalizationMappingId.Should().Be(beforeTrash.NormalizationMappingId);
			afterTrash.NormalizationReviewDecisionId.Should().Be(beforeTrash.NormalizationReviewDecisionId);
			afterTrash.ReviewedSourceHash.Should().Be(beforeTrash.ReviewedSourceHash);
		}
		var liveIds = await ItemsAsync(Source with { Description = "Legacy live beef", ItemCode = "999" }, 1, "legacy_unknown", loser.Id);
		Guid[] all = [trashedId, .. liveIds];
		var facts = trashedFacts.Concat(await FactsAsync(liveIds)).ToArray();
		Guid[] auditIds;
		await using (var db = fixture.CreateDbContext()) { auditIds = await db.AuditLogs.Select(x => x.Id).ToArrayAsync(); }
		string source = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		string directory = Path.Combine(Path.GetTempPath(), "offline-trash-pg-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			byte[] original = await File.ReadAllBytesAsync(source);
			var baseline = await new OfflinePortableBackupInspection().InspectAsync(source);
			baseline.Observations.Should().NotContain(x => x.EntityId == trashedId, "trash is absent from portable purchase facts");
			var merge = new OfflineConsolidationDecision(Guid.NewGuid(), loser.Id, survivor.Id);
			var name = new OfflineCanonicalDecision(Guid.NewGuid(), survivor.Id, survivor.CanonicalName, null);
			var now = DateTimeOffset.UtcNow.AddSeconds(-1);
			var manifest = new OfflineNormalizationManifest(1, baseline.SourceSha256, baseline.FormatVersion, baseline.FactualBaselineSha256,
				"trash-regression", "synthetic-proposer", now.AddMinutes(-1), [name], [], [merge], [],
				new("synthetic-reviewer", now, [new(name.DecisionId, "Reviewed surviving beef identity"), new(merge.DecisionId, "Same beef product")]));
			string manifestPath = Path.Combine(directory, "reviewed.json"), output = Path.Combine(directory, "B.db");
			await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, OfflineNormalizationManifestPolicy.JsonOptions));
			var offline = new PortableBackupNormalizationService();
			await offline.ApplyAsync(source, manifestPath, output);
			await using (var stream = File.OpenRead(output))
			{
				await new BackupImportService(new Factory(fixture), NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, CancellationToken.None);
			}
			await using (var db = fixture.CreateDbContext())
			{
				var trashed = await db.ReceiptItems.IgnoreQueryFilters().SingleAsync(x => x.Id == trashedId);
				trashed.DeletedAt.Should().BeCloseTo(deletedAt, TimeSpan.FromMilliseconds(1));
				AssertNoAuthority(trashed);
				trashed.NormalizationAssignmentId.Should().NotBeNull();
				trashed.NormalizationAssignmentId.Should().NotBe(beforeTrash.NormalizationAssignmentId.GetValueOrDefault());
				(await db.ReceiptItems.SingleAsync(x => x.Id == liveIds.Single())).NormalizedDescriptionId.Should().Be(survivor.Id);
				(await db.NormalizedDescriptions.AnyAsync(x => x.Id == loser.Id)).Should().BeFalse();
				var invalidationAudits = await db.AuditLogs.Where(x => !auditIds.Contains(x.Id) && x.EntityType == "ReceiptItem" && x.EntityId == trashedId.ToString()).ToListAsync();
				invalidationAudits.Should().ContainSingle("the trash relink and proof invalidation must be one audited business change");
				var changes = invalidationAudits.Single().GetChanges();
				changes.Should().Contain(x => x.FieldName == "NormalizedDescriptionId" && x.OldValue == loser.Id.ToString() && x.NewValue == survivor.Id.ToString());
				changes.Should().Contain(x => x.FieldName == "NormalizationReviewReason" && x.OldValue == "unavailable" && x.NewValue == "legacy_unknown");
				changes.Should().Contain(x => x.FieldName == "NormalizationMappingId" && x.OldValue == beforeTrash.NormalizationMappingId.ToString() && x.NewValue == null);
				changes.Should().Contain(x => x.FieldName == "NormalizationReviewDecisionId" && x.OldValue == beforeTrash.NormalizationReviewDecisionId.ToString() && x.NewValue == null);
				changes.Should().Contain(x => x.FieldName == "ReviewedSourceHash" && x.OldValue == beforeTrash.ReviewedSourceHash && x.NewValue == null);
				changes.Should().Contain(x => x.FieldName == "NormalizationOrigin" && x.OldValue == "mapped" && x.NewValue == "legacy_unknown");
				trashed.DeletedAt = null;
				await db.SaveChangesAsync();
			}
			AssertNoAuthority((await ReadAsync(trashedIds)).Single());
			(await FactsAsync(all)).Should().BeEquivalentTo(facts);
			(await File.ReadAllBytesAsync(source)).Should().Equal(original);

			void AssertNoAuthority(Infrastructure.Entities.Core.ReceiptItemEntity item)
			{
				item.NormalizedDescriptionId.Should().Be(survivor.Id);
				item.NormalizationOrigin.Should().Be("legacy_unknown");
				item.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
				item.NormalizationReviewReason.Should().Be("legacy_unknown");
				item.NormalizationMappingId.Should().BeNull();
				item.NormalizationReviewDecisionId.Should().BeNull();
				item.ReviewedTargetId.Should().BeNull();
				item.ReviewedSourceHash.Should().BeNull();
				item.ReviewMappingRevision.Should().BeNull();
				item.ReviewedBy.Should().BeNull();
				item.ReviewedAt.Should().BeNull();
			}
		}
		finally { File.Delete(source); Directory.Delete(directory, recursive: true); }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task OfflinePortable_ActualExportDirectTransformationExactArtifactRestoresToFrozenSourceAndFreshDatabaseRepeatedly(bool reuseRetiredName)
	{
		await ResetAsync();
		var beef = await CanonicalAsync("BF FRANK");
		var fragment = await CanonicalAsync("bf frnk");
		var turkey = await CanonicalAsync("Turkey franks 8 count");
		var beefIds = await ItemsAsync(Source, 2);
		var turkeyIds = await ItemsAsync(Source with { Description = "TURKEY FRANK", ItemCode = "456" }, 1);
		var ambiguous = await ItemsAsync(Source with { Description = "HOT DOG", ItemCode = null }, 1);
		var legacy = await ItemsAsync(Source with { Description = "Legacy unknown", ItemCode = "789" }, 1, "legacy_unknown", fragment.Id);
		var all = beefIds.Concat(turkeyIds).Concat(ambiguous).Concat(legacy).ToArray();
		var originalFacts = await FactsAsync(all);
		string source = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		string directory = Path.Combine(Path.GetTempPath(), "offline-portable-pg-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			byte[] original = await File.ReadAllBytesAsync(source);
			var baseline = await new OfflinePortableBackupInspection().InspectAsync(source);
			baseline.FormatVersion.Should().Be(9);
			// Product identities and do-not-merge expectations are arranged above independently.
			// Observation data binds those choices to the exact exported source.
			var assignments = beefIds.Select(id => Choice(id, beef.Id, true))
				.Concat(turkeyIds.Select(id => Choice(id, turkey.Id, false)))
				.Concat(ambiguous.Select(id => Choice(id, null, false))).ToArray();
			OfflineAssignmentDecision Choice(Guid id, Guid? target, bool remember)
			{
				var observation = baseline.Observations.Single(o => o.EntityId == id);
				return new(Guid.NewGuid(), "item", id, observation.Source, observation.SourceHash, target, target is null, remember, target is null ? "Cannot distinguish beef from turkey" : null);
			}
			OfflineCanonicalDecision[] names = [new(Guid.NewGuid(), beef.Id, reuseRetiredName ? fragment.CanonicalName : "Beef franks 8 count", "Beef franks"), new(Guid.NewGuid(), turkey.Id, "Turkey franks 8 count", null)];
			var consolidation = new OfflineConsolidationDecision(Guid.NewGuid(), fragment.Id, beef.Id);
			var now = DateTimeOffset.UtcNow.AddSeconds(-1);
			var manifest = new OfflineNormalizationManifest(1, baseline.SourceSha256, baseline.FormatVersion, baseline.FactualBaselineSha256, "synthetic-pg-revision", "synthetic-proposer", now.AddMinutes(-1), names, assignments, [consolidation],
				[new("item", legacy.Single(), "Legacy provenance remains unverified")], new("synthetic-reviewer", now,
					names.Select(n => new OfflineReviewedDecision(n.DecisionId, "Reviewed product name"))
						.Concat(assignments.Select(a => new OfflineReviewedDecision(a.DecisionId, "Reviewed independent corpus identity")))
						.Append(new(consolidation.DecisionId, "Same beef identity; turkey stays distinct")).ToArray()));
			string manifestPath = Path.Combine(directory, "reviewed.json"), output = Path.Combine(directory, "B.db");
			await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, OfflineNormalizationManifestPolicy.JsonOptions));
			var offline = new PortableBackupNormalizationService();
			var result = await offline.ApplyAsync(source, manifestPath, output);
			byte[] exactB = await File.ReadAllBytesAsync(output);
			(await File.ReadAllBytesAsync(source)).Should().Equal(original);
			result.SourceSha256.Should().Be(Convert.ToHexString(SHA256.HashData(original)));
			(await offline.VerifyAsync(source, manifestPath, output)).OutputSha256.Should().Be(Convert.ToHexString(SHA256.HashData(exactB)));
			await RestoreAndAssertAsync(fixture);
			(await FactsAsync(all)).Should().BeEquivalentTo(originalFacts);
			var fresh = new PostgresFixture();
			try { await fresh.InitializeAsync(); await RestoreAndAssertAsync(fresh); }
			finally { await fresh.DisposeAsync(); }
			(await File.ReadAllBytesAsync(output)).Should().Equal(exactB);
			(await File.ReadAllBytesAsync(source)).Should().Equal(original);

			async Task RestoreAndAssertAsync(PostgresFixture target)
			{
				for (int repeat = 0; repeat < 2; repeat++)
				{
					// Import the exact offline artifact, never an export from an intermediary app DB.
					await using var stream = File.OpenRead(output);
					await new BackupImportService(new Factory(target), NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, CancellationToken.None);
					await using var read = target.CreateDbContext();
					var rows = await read.ReceiptItems.IgnoreAutoIncludes().Where(x => all.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync();
					rows.Should().HaveCount(all.Length);
					rows.Where(x => beefIds.Contains(x.Id)).Should().OnlyContain(x => x.NormalizedDescriptionId == beef.Id && x.NormalizationReviewState == NormalizationReviewState.Verified && x.NormalizationOrigin == "mapped");
					rows.Single(x => x.Id == turkeyIds.Single()).NormalizedDescriptionId.Should().Be(turkey.Id);
					var excluded = rows.Single(x => x.Id == ambiguous.Single());
					excluded.NormalizedDescriptionId.Should().BeNull(); excluded.NormalizationReviewState.Should().Be(NormalizationReviewState.Excluded); excluded.NormalizationExclusionReason.Should().Be("Cannot distinguish beef from turkey");
					var untouched = rows.Single(x => x.Id == legacy.Single());
					untouched.NormalizedDescriptionId.Should().Be(beef.Id); untouched.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
					var mapping = await read.NormalizationSourceMappings.SingleAsync();
					mapping.TargetId.Should().Be(beef.Id); mapping.Description.Should().Be(Source.Description); mapping.Location.Should().Be(Source.Location);
					(await read.NormalizedDescriptions.SingleAsync(x => x.Id == beef.Id)).CanonicalName.Should().Be(reuseRetiredName ? fragment.CanonicalName : "Beef franks 8 count");
					(await read.NormalizedDescriptions.SingleAsync(x => x.Id == turkey.Id)).CanonicalName.Should().Be("Turkey franks 8 count");
					(await read.NormalizedDescriptions.AnyAsync(x => x.Id == fragment.Id)).Should().BeFalse("the reviewed losing identity must be retired in the unchanged source as well as in a fresh database");
					rows.Select(x => new { x.Id, x.ReceiptId, x.Description, x.ReceiptItemCode, x.Quantity, x.UnitPrice, x.UnitPriceCurrency, x.TotalAmount, x.TotalAmountCurrency, x.Category, x.Subcategory }).Cast<object>().Should().BeEquivalentTo(originalFacts);
				}
				// Retirement authority belongs to this reviewed artifact; a cleaned installation does not replay it in later exports.
				string reexport = await new BackupService(new Factory(target), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
				try
				{
					await using var archived = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = reexport, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
					await archived.OpenAsync();
					await using var query = archived.CreateCommand();
					query.CommandText = "SELECT COUNT(*) FROM normalization_catalog WHERE kind='consolidation'";
					Convert.ToInt64(await query.ExecuteScalarAsync()).Should().Be(0);
				}
				finally { File.Delete(reexport); }
			}
		}
		finally { File.Delete(source); Directory.Delete(directory, recursive: true); }
	}
}
