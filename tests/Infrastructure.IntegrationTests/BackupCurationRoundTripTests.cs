using Application.Models;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using FluentAssertions.Execution;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.IntegrationTests.Services;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class BackupCurationRoundTripTests(PostgresFixture source) : IClassFixture<PostgresFixture>
{
	[Fact]
	public async Task ExportAndFreshPostgresRestore_PreservesCurationAndAcceptedDuplicateDecision()
	{
		Guid activeId = Guid.NewGuid(), pendingId = Guid.NewGuid(), rejectedId = Guid.NewGuid(), historicalId = Guid.NewGuid();
		Guid receiptA = Guid.NewGuid(), receiptB = Guid.NewGuid(), curatedItemId = Guid.NewGuid(), measuredItemId = Guid.NewGuid(), templateId = Guid.NewGuid();
		Guid unlinkedItemId = Guid.NewGuid(), unlinkedTemplateId = Guid.NewGuid();
		DateTimeOffset createdAt = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
		await using (ApplicationDbContext seed = source.CreateDbContext())
		{
			seed.NormalizedDescriptions.AddRange(
				new NormalizedDescriptionEntity { Id = pendingId, CanonicalName = "Pending raw name", Status = NormalizedDescriptionStatus.PendingReview, CreatedAt = createdAt, NearestNeighbourId = activeId, NearestNeighbourSimilarity = 0.78 },
				new NormalizedDescriptionEntity { Id = activeId, CanonicalName = "Raw canonical text", DisplayLabel = "Chosen display label", Status = NormalizedDescriptionStatus.Active, CreatedAt = createdAt },
				new NormalizedDescriptionEntity { Id = rejectedId, CanonicalName = "Rejected raw text", Status = NormalizedDescriptionStatus.Rejected, CreatedAt = createdAt },
				new NormalizedDescriptionEntity { Id = historicalId, CanonicalName = "Historical neighbour removed", Status = NormalizedDescriptionStatus.PendingReview, CreatedAt = createdAt, NearestNeighbourSimilarity = 0.86 });
			seed.Receipts.AddRange(
				new ReceiptEntity { Id = receiptA, Location = "Separate purchases", Date = new(2024, 1, 1) },
				new ReceiptEntity { Id = receiptB, Location = "Separate purchases", Date = new(2024, 1, 1) });
			seed.ReceiptItems.AddRange(
				new ReceiptItemEntity { Id = curatedItemId, ReceiptId = receiptA, Description = "Template entry text", Quantity = 1, UnitPrice = 3, TotalAmount = 3, Category = "Food", NormalizedDescriptionId = activeId },
				new ReceiptItemEntity { Id = measuredItemId, ReceiptId = receiptB, Description = "Measured receipt text", Quantity = 1, UnitPrice = 3, TotalAmount = 3, Category = "Food", NormalizedDescriptionId = pendingId, NormalizedDescriptionMatchScore = 0.78 });
			seed.ItemTemplates.Add(new ItemTemplateEntity { Id = templateId, Name = "Template display differs from canonical", NormalizedDescriptionId = activeId });
			seed.ItemTemplates.Add(new ItemTemplateEntity { Id = unlinkedTemplateId, Name = "Intentionally unlinked template" });
			seed.ReceiptItems.Add(new ReceiptItemEntity { Id = unlinkedItemId, ReceiptId = receiptA, Description = "Intentionally unlinked item", Quantity = 1, UnitPrice = 1, TotalAmount = 1, Category = "Food" });
			await seed.SaveChangesAsync();
		}
		await ReviewedPurchaseFixture.ConfirmActiveAsync(source.CreateOptions(), curatedItemId);
		DuplicateWorkflowFixture sourceReports = new(new Factory(source.CreateOptions()));
		(await sourceReports.GetDuplicatesAsync("dateAndLocation", "exact", 0, false, CancellationToken.None)).Groups.Should().ContainSingle();
		(await sourceReports.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None)).Should().Be(1);
		(await sourceReports.GetDuplicatesAsync("dateAndLocation", "exact", 0, false, CancellationToken.None)).Groups.Should().BeEmpty();
		AcceptedDuplicatePairEntity expectedPair;
		await using (ApplicationDbContext read = source.CreateDbContext())
		{
			expectedPair = await read.AcceptedDuplicatePairs.AsNoTracking().SingleAsync();
		}

		// Version 10 preserves acceptance and the trashed endpoint for recovery.
		Guid excludedReceipt = Guid.NewGuid();
		await using (ApplicationDbContext seed = source.CreateDbContext())
		{
			seed.Receipts.Add(new() { Id = excludedReceipt, Location = "Excluded trash", Date = new(2024, 1, 2) });
			await seed.SaveChangesAsync();
		}
		await sourceReports.AcceptDuplicateGroupAsync([receiptA, excludedReceipt], CancellationToken.None);
		await using (ApplicationDbContext trash = source.CreateDbContext())
		{
			trash.Receipts.Remove(await trash.Receipts.SingleAsync(row => row.Id == excludedReceipt));
			await trash.SaveChangesAsync();
		}
		await using (ApplicationDbContext read = source.CreateDbContext())
		{
			(await read.AcceptedDuplicatePairs.CountAsync()).Should().Be(2, "source acceptance remains intact when a receipt goes to trash");
		}

		ExportPathLogger logger = new();
		BackupService exporter = new(new Factory(source.CreateOptions()), logger);
		try
		{
			string path = await exporter.ExportToSqliteAsync();
			await using (SqliteConnection sqlite = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "PRAGMA foreign_key_check";
				await using SqliteDataReader violations = await command.ExecuteReaderAsync();
				(await violations.ReadAsync()).Should().BeFalse();
			}

			PostgresFixture target = new();
			try
			{
				await target.InitializeAsync();
				BackupImportService importer = new(new Factory(target.CreateOptions()), NullLogger<BackupImportService>.Instance);
				await using (FileStream stream = File.OpenRead(path))
				{
					BackupImportResult result = await importer.ImportFromSqliteAsync(stream, CancellationToken.None);
					result.AcceptedDuplicatePairsCreated.Should().Be(2);
				}
				await using ApplicationDbContext restored = target.CreateDbContext();
				ReceiptItemEntity curated = await restored.ReceiptItems.IgnoreAutoIncludes().SingleAsync(row => row.Id == curatedItemId);
				ReceiptItemEntity measured = await restored.ReceiptItems.IgnoreAutoIncludes().SingleAsync(row => row.Id == measuredItemId);
				ItemTemplateEntity template = await restored.ItemTemplates.SingleAsync(row => row.Id == templateId);
				NormalizedDescriptionEntity active = await restored.NormalizedDescriptions.SingleAsync(row => row.Id == activeId);
				NormalizedDescriptionEntity pending = await restored.NormalizedDescriptions.SingleAsync(row => row.Id == pendingId);
				NormalizedDescriptionEntity rejected = await restored.NormalizedDescriptions.SingleAsync(row => row.Id == rejectedId);
				NormalizedDescriptionEntity historical = await restored.NormalizedDescriptions.SingleAsync(row => row.Id == historicalId);
				List<AcceptedDuplicatePairEntity> pairs = await restored.AcceptedDuplicatePairs.ToListAsync();
				DuplicateWorkflowFixture targetReports = new(new Factory(target.CreateOptions()));
				var visible = await targetReports.GetDuplicatesAsync("dateAndLocation", "exact", 0, false, CancellationToken.None);
				var includingAccepted = await targetReports.GetDuplicatesAsync("dateAndLocation", "exact", 0, true, CancellationToken.None);
				var spending = await new ReportService(new Factory(target.CreateOptions())).GetSpendingByNormalizedDescriptionAsync(null, null, "totalAmount", "desc", 1, 50, CancellationToken.None);

				using AssertionScope assertions = new();
				curated.NormalizedDescriptionId.Should().Be(activeId);
				curated.NormalizedDescriptionMatchScore.Should().BeNull("a declaration has no fabricated cosine score");
				measured.NormalizedDescriptionId.Should().Be(pendingId);
				measured.NormalizedDescriptionMatchScore.Should().Be(0.78);
				template.NormalizedDescriptionId.Should().Be(activeId);
				active.CanonicalName.Should().Be("Raw canonical text");
				active.DisplayLabel.Should().Be("Chosen display label");
				spending.Items.Should().ContainSingle(row => row.CanonicalName == "Chosen display label" && row.TotalAmount == 3 && row.ItemCount == 1 && row.Status == NormalizedDescriptionStatus.Active);
				spending.Items.Should().ContainSingle(row => row.CanonicalName == "(Needs review)" && row.TotalAmount == 4 && row.ItemCount == 2 && row.Status == null && row.NormalizedDescriptionId == null && row.NormalizationReviewState == "needsReview");
				spending.TotalCount.Should().Be(2);
				curated.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
				measured.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
				spending.GrandTotal.Should().Be(7, "restored grouping and human labels must preserve all spending, including the unlinked bucket");
				active.CreatedAt.Should().Be(createdAt);
				active.Embedding.Should().BeNull("embedding vectors remain deliberately excluded");
				pending.Status.Should().Be(NormalizedDescriptionStatus.PendingReview);
				pending.NearestNeighbourId.Should().Be(activeId);
				pending.NearestNeighbourSimilarity.Should().Be(0.78);
				rejected.Status.Should().Be(NormalizedDescriptionStatus.Rejected);
				historical.NearestNeighbourId.Should().BeNull();
				historical.NearestNeighbourSimilarity.Should().Be(0.86, "deleting a neighbour deliberately retains historical similarity evidence");
				(await restored.Receipts.IgnoreQueryFilters().SingleAsync(row => row.Id == excludedReceipt)).DeletedAt.Should().NotBeNull();
				pairs.Should().HaveCount(2, "acceptance involving trash is portable recovery state");
				pairs = pairs.Where(pair => pair.Id == expectedPair.Id).ToList();
				if (pairs.Count == 1)
				{
					pairs[0].Id.Should().Be(expectedPair.Id);
					pairs[0].ReceiptIdA.Should().Be(expectedPair.ReceiptIdA);
					pairs[0].ReceiptIdB.Should().Be(expectedPair.ReceiptIdB);
					pairs[0].AcceptedAt.Should().Be(expectedPair.AcceptedAt);
				}
				visible.Groups.Should().BeEmpty("restoring a reviewed separate-purchase decision must not recreate a warning");
				includingAccepted.Groups.Should().ContainSingle();
				includingAccepted.Groups.Should().OnlyContain(group => group.IsAccepted);

				// Replaying an unchanged v12 archive is idempotent.
				for (int pass = 0; pass < 2; pass++)
				{
					await using FileStream stream = File.OpenRead(path);
					BackupImportResult result = await importer.ImportFromSqliteAsync(stream, CancellationToken.None);
					result.AcceptedDuplicatePairsCreated.Should().Be(0);
					result.AcceptedDuplicatePairsUpdated.Should().Be(2);
				}

				// A different destination source revision must reject the whole v12 restore.
				await using (ApplicationDbContext change = target.CreateDbContext())
				{
					ReceiptItemEntity previouslyUnlinked = await change.ReceiptItems.SingleAsync(row => row.Id == unlinkedItemId);
					previouslyUnlinked.NormalizedDescriptionId = activeId;
					previouslyUnlinked.NormalizedDescriptionMatchScore = 0.91;
					(await change.ReceiptItems.SingleAsync(row => row.Id == curatedItemId)).NormalizedDescriptionMatchScore = 0.99;
					(await change.ReceiptItems.SingleAsync(row => row.Id == measuredItemId)).NormalizedDescriptionId = activeId;
					(await change.ItemTemplates.SingleAsync(row => row.Id == unlinkedTemplateId)).NormalizedDescriptionId = activeId;
					(await change.ItemTemplates.SingleAsync(row => row.Id == templateId)).NormalizedDescriptionId = null;
					NormalizedDescriptionEntity changedRejected = await change.NormalizedDescriptions.SingleAsync(row => row.Id == rejectedId);
					changedRejected.DisplayLabel = "Target-only label";
					changedRejected.NearestNeighbourId = activeId;
					changedRejected.NearestNeighbourSimilarity = 0.95;
					(await change.NormalizedDescriptions.SingleAsync(row => row.Id == activeId)).DisplayLabel = "Target override";
					await change.SaveChangesAsync();
				}
				await using (FileStream stream = File.OpenRead(path))
				{
					Func<Task> import = async () => await importer.ImportFromSqliteAsync(stream, CancellationToken.None);
					(await import.Should().ThrowAsync<InvalidOperationException>())
							.WithMessage("*Restore conflicts with destination source revision*");
				}
				await using ApplicationDbContext upserted = target.CreateDbContext();
				ReceiptItemEntity explicitlyUnlinked = await upserted.ReceiptItems.SingleAsync(row => row.Id == unlinkedItemId);
				explicitlyUnlinked.NormalizedDescriptionId.Should().Be(activeId);
				explicitlyUnlinked.NormalizedDescriptionMatchScore.Should().Be(0.91);
				(await upserted.ReceiptItems.SingleAsync(row => row.Id == curatedItemId)).NormalizedDescriptionMatchScore.Should().Be(0.99);
				(await upserted.ReceiptItems.SingleAsync(row => row.Id == measuredItemId)).NormalizedDescriptionId.Should().Be(activeId);
				(await upserted.ItemTemplates.SingleAsync(row => row.Id == templateId)).NormalizedDescriptionId.Should().BeNull();
				(await upserted.ItemTemplates.SingleAsync(row => row.Id == unlinkedTemplateId)).NormalizedDescriptionId.Should().Be(activeId);
				(await upserted.NormalizedDescriptions.SingleAsync(row => row.Id == activeId)).DisplayLabel.Should().Be("Target override");
				NormalizedDescriptionEntity restoredRejected = await upserted.NormalizedDescriptions.SingleAsync(row => row.Id == rejectedId);
				restoredRejected.DisplayLabel.Should().Be("Target-only label");
				restoredRejected.NearestNeighbourId.Should().Be(activeId);
				restoredRejected.NearestNeighbourSimilarity.Should().Be(0.95);
				(await upserted.AcceptedDuplicatePairs.CountAsync()).Should().Be(2);
				(await upserted.AcceptedDuplicatePairs.SingleAsync(pair => pair.Id == expectedPair.Id)).Id.Should().Be(expectedPair.Id);
			}
			finally
			{
				await target.DisposeAsync();
			}
		}
		finally
		{
			if (logger.Path is { } path)
			{
				foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
				{
					File.Delete(path + suffix);
				}
			}
		}
	}

	private sealed class ExportPathLogger : ILogger<BackupService>
	{
		public string? Path { get; private set; }
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => true;
		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (state is IEnumerable<KeyValuePair<string, object?>> values)
			{
				Path ??= values.FirstOrDefault(row => row.Key == "Path").Value as string;
			}
		}
	}

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
	}
}
