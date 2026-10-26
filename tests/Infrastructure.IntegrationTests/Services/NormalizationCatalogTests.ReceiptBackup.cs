using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData("SourceHash")]
	[InlineData("TargetId")]
	public async Task ReceiptBackup_EvidenceForAnotherSourceOrIdentityRollsBackAllImportedFacts(string changedField)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Reviewed product");
		var other = await CanonicalAsync("Different product");
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision))], [], CancellationToken.None);
		var itemId = saved.Items.Single().Id;
		var original = (await ReadAsync([itemId])).Single();
		var facts = await FactsAsync([itemId]);
		var audits = await AuditCountAsync([itemId]);
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		try
		{
			await using (var archive = new SqliteConnection($"Data Source={path};Pooling=False"))
			{
				await archive.OpenAsync();
				await using var command = archive.CreateCommand();
				command.CommandText = "UPDATE normalization_catalog SET payload=json_set(payload,$path,$value) WHERE kind='review_evidence'; UPDATE receipt_items SET quantity=99;";
				command.Parameters.AddWithValue("$path", "$." + changedField);
				command.Parameters.AddWithValue("$value", changedField == "TargetId" ? other.Id.ToString() : NormalizationCatalogPolicy.Hash(Source with { Location = "Different store" }));
				await command.ExecuteNonQueryAsync();
			}
			await using var stream = File.OpenRead(path);
			Func<Task> restore = () => new BackupImportService(new Factory(fixture), NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, CancellationToken.None);

			await restore.Should().ThrowAsync<InvalidOperationException>();

			(await FactsAsync([itemId])).Should().BeEquivalentTo(facts);
			var retained = (await ReadAsync([itemId])).Single();
			retained.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
			retained.NormalizationReviewDecisionId.Should().Be(original.NormalizationReviewDecisionId);
			(await AuditCountAsync([itemId])).Should().Be(audits);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task ReceiptBackup_VersionSevenMappingsDoNotBlessImportedHistoryButCanRecognizeNewPurchases()
	{
		await VersionSevenMappingsCoreAsync();
	}

	private async Task VersionSevenMappingsCoreAsync()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Reviewed reusable product");
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		var originalId = saved.Items.Single().Id;
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		try
		{
			await using (var legacy = new SqliteConnection($"Data Source={path};Pooling=False"))
			{
				await legacy.OpenAsync();
				await using var command = legacy.CreateCommand();
				command.CommandText = "UPDATE backup_metadata SET value='7' WHERE key='export_version'; DELETE FROM normalization_catalog WHERE kind IN ('review_evidence','write_request'); UPDATE normalization_catalog SET payload=json_remove(payload,'$.ExclusionReason') WHERE kind='mapping';";
				await command.ExecuteNonQueryAsync();
			}
			await StartFreshDatabaseAsync();
			await using (var stream = File.OpenRead(path))
			{
				await new BackupImportService(new Factory(fixture), NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, CancellationToken.None);
			}
			var history = (await ReadAsync([originalId])).Single();
			history.NormalizedDescriptionId.Should().Be(target.Id);
			history.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
			history.NormalizationReviewDecisionId.Should().BeNull();

			var future = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null)], [], CancellationToken.None);

			(await ReadAsync([future.Items.Single().Id])).Single().NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
			(await ReadAsync([originalId])).Single().NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
			await using var db = fixture.CreateDbContext();
			(await db.NormalizationReviewWork.SingleAsync()).ItemId.Should().Be(originalId);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task ReceiptBackup_RoundtripPreservesExplicitPurchaseEvidenceWithoutUpgradingUnknownRows()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Explicitly reviewed product");
		var reviewed = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision));
		var excluded = EntryItem(new(ReceiptNormalizationIntentKind.Exclude, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), Reason: "Non-product line"));
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [reviewed, excluded, EntryItem(null)], [], CancellationToken.None);
		var ids = saved.Items.Select(x => x.Id).ToArray();
		var original = await ReadAsync(ids);
		var facts = await FactsAsync(ids);
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		try
		{
			await StartFreshDatabaseAsync();
			await using (var stream = File.OpenRead(path))
			{
				await new BackupImportService(new Factory(fixture), NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, CancellationToken.None);
			}

			(await FactsAsync(ids)).Should().BeEquivalentTo(facts);
			var restored = await ReadAsync(ids);
			foreach (var before in original)
			{
				var after = restored.Single(x => x.Id == before.Id);
				after.NormalizationReviewState.Should().Be(before.NormalizationReviewState);
				after.NormalizationReviewDecisionId.Should().Be(before.NormalizationReviewDecisionId);
				after.ReviewedBy.Should().Be(before.ReviewedBy);
				after.ReviewedAt.Should().Be(before.ReviewedAt);
				after.ReviewedSourceHash.Should().Be(before.ReviewedSourceHash);
			}
			await using var db = fixture.CreateDbContext();
			(await db.NormalizationReviewWork.SingleAsync()).ItemId.Should().Be(original.Single(x => x.NormalizationReviewState == NormalizationReviewState.NeedsReview).Id);
			(await db.NormalizedDescriptions.CountAsync()).Should().Be(1);
		}
		finally { File.Delete(path); }
	}
}
