using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class PortableEffectImportCandidateTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }

	private sealed class FailingFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new FailingContext(fixture.CreateOptions());
	}

	private sealed class FailingContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
	{
		public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
		{
			int saved = await base.SaveChangesAsync(cancellationToken);
			if (OrganizationEffectItems.Local.Any())
			{
				// This fault must occur after ordinary business and immutable history restoration.
				(await Receipts.CountAsync(cancellationToken)).Should().Be(1);
				(await Classifications.CountAsync(cancellationToken)).Should().Be(1);
				throw new InvalidOperationException("injected after effect item insertion");
			}
			return saved;
		}
	}

	[Theory]
	[InlineData(10)]
	[InlineData(11)]
	[InlineData(12)]
	[InlineData(13)]
	public async Task CandidateImport_RoundTripsRepeatsRetainsOlderEvidenceAndRollsBackFences(int olderVersion)
	{
		Guid receiptId = Guid.NewGuid(), itemId = Guid.NewGuid();
		await using (var db = fixture.CreateDbContext())
		{
			db.Categories.Add(new() { Id = Guid.NewGuid(), Name = "Food", IsActive = true });
			db.Receipts.Add(new() { Id = receiptId, Location = "Original", Date = new(2026, 1, 1) });
			db.ReceiptItems.Add(new()
			{
				Id = itemId,
				ReceiptId = receiptId,
				Description = "Milk",
				Category = "Food",
				Quantity = 1,
				UnitPrice = 2,
				TotalAmount = 2
			});
			await db.SaveChangesAsync();
		}
		var migration = new LegacyTaxonomyMigrationService(new Factory(fixture));
		var plan = await migration.PreflightAsync(new("candidate-import", Guid.NewGuid(), "test",
			new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)));
		await migration.ApplyAsync(LegacyMigrationFreeze.Source(plan));
		var exporter = new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance);
		string olderPath = await exporter.ExportToSqliteAsync();
		string? candidatePath = null;
		try
		{
			PortableTaxonomyArchive.State history;
			await using (SqliteConnection sqlite = new($"Data Source={olderPath};Pooling=False"))
			{
				await sqlite.OpenAsync();
				history = await PortableTaxonomyArchive.ReadAsync(sqlite, default);
			}
			await using (SqliteConnection sqlite = new($"Data Source={olderPath};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using var version = sqlite.CreateCommand();
				version.CommandText = "UPDATE backup_metadata SET value=$version WHERE key='export_version'";
				version.Parameters.AddWithValue("$version", olderVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
				await version.ExecuteNonQueryAsync();
			}
			var assignment = history.Assignments.Single();
			var item = new OrganizationEffectItemEntity
			{
				ManifestId = Guid.NewGuid(),
				ItemId = itemId,
				ReceiptId = receiptId,
				Store = "Original",
				RawDescription = "Milk",
				ItemRevision = Guid.NewGuid(),
				ReceiptRevision = Guid.NewGuid(),
				TaxonomyRevisionId = assignment.TaxonomyRevisionId,
				TargetCategoryId = assignment.CategoryId!.Value,
				ExpectedAssignmentId = assignment.Id,
				ApplyOperationId = Guid.NewGuid(),
				UndoOperationId = Guid.NewGuid(),
				DecisionEvidenceJson = "{\"n\":100.0}"
			};
			var manifest = new OrganizationEffectManifestEntity
			{
				Id = item.ManifestId,
				Actor = "test",
				RequestHash = new string('A', 64),
				PolicyVersion = "candidate",
				TaxonomyRevisionId = item.TaxonomyRevisionId,
				CapturedAt = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)
			};
			PortableTaxonomyArchive.EffectEvidence evidence = new(
				[PortableTaxonomyArchive.SealEffectManifest(manifest, [item])], [item], [new()
				{
					ManifestId = item.ManifestId, ItemId = itemId, Phase = "apply", Disposition = "skipped",
					Reason = "candidate", RecordedAt = manifest.CapturedAt
				}]);
			await using (var db = fixture.CreateDbContext())
			{
				await using var tx = await db.Database.BeginTransactionAsync();
				await PortableTaxonomyArchive.RestoreEffectsCandidateAsync(db, history, history, evidence, default);
				await tx.CommitAsync();
			}
			candidatePath = await exporter.ExportV14CandidateAsync();
			await ReplaceDatabaseAsync();
			var importer = new BackupImportService(new Factory(fixture), NullLogger<BackupImportService>.Instance);
			async Task Import(string path, bool candidate = true)
			{
				await using var input = File.OpenRead(path);
				if (candidate) { await importer.ImportV14CandidateAsync(input); }
				else { await importer.ImportFromSqliteAsync(input, default); }
			}
			Guid initialGeneration;
			int initialNormalizationConnections;
			await using (var db = fixture.CreateDbContext())
			{
				initialGeneration = (await db.CategoryAssistanceSettings.SingleAsync()).RestoreGeneration;
				initialNormalizationConnections = await db.NormalizationAiConnection.CountAsync();
			}
			var failingImporter = new BackupImportService(new FailingFactory(fixture), NullLogger<BackupImportService>.Instance);
			await using (var input = File.OpenRead(candidatePath))
			{
				Func<Task> failAfterDependencies = () => failingImporter.ImportV14CandidateAsync(input);
				await failAfterDependencies.Should().ThrowAsync<InvalidOperationException>()
					.WithMessage("injected after effect item insertion");
			}
			await using (var db = fixture.CreateDbContext())
			{
				(await db.Receipts.CountAsync()).Should().Be(0);
				(await db.Classifications.CountAsync()).Should().Be(0);
				(await db.OrganizationEffectManifests.CountAsync()).Should().Be(0);
				(await db.OrganizationEffectItems.CountAsync()).Should().Be(0);
				(await db.OrganizationEffectOperations.CountAsync()).Should().Be(0);
				(await db.OrganizationEffectOutcomes.CountAsync()).Should().Be(0);
				(await db.CategoryAssistanceSettings.SingleAsync()).RestoreGeneration.Should().Be(initialGeneration);
				(await db.NormalizationAiConnection.CountAsync()).Should().Be(initialNormalizationConnections);
			}
			await Import(candidatePath);
			Guid generation;
			Guid normalizationGeneration;
			await using (var db = fixture.CreateDbContext())
			{
				generation = (await db.CategoryAssistanceSettings.SingleAsync()).RestoreGeneration;
				normalizationGeneration = (await db.NormalizationAiConnection.SingleAsync()).Generation;
				(await db.OrganizationEffectItems.CountAsync()).Should().Be(1);
				(await db.OrganizationEffectOutcomes.SingleAsync()).Disposition.Should().Be("skipped");
				(await db.OrganizationEffectOperations.CountAsync()).Should().Be(2);
			}
			await Import(candidatePath);
			await using (var db = fixture.CreateDbContext())
			{
				(await db.OrganizationEffectManifests.CountAsync()).Should().Be(1);
				(await db.OrganizationEffectItems.CountAsync()).Should().Be(1);
				(await db.OrganizationEffectOutcomes.SingleAsync()).Disposition.Should().Be("skipped");
				(await db.OrganizationEffectOperations.CountAsync()).Should().Be(2);
				var settings = await db.CategoryAssistanceSettings.SingleAsync();
				settings.RestoreGeneration.Should().NotBe(generation);
				generation = settings.RestoreGeneration;
				(await db.NormalizationAiConnection.SingleAsync()).Generation.Should().NotBe(normalizationGeneration);
				normalizationGeneration = (await db.NormalizationAiConnection.SingleAsync()).Generation;
			}
			// A structurally valid incoming manifest cannot rewrite destination intent.
			await using (SqliteConnection sqlite = new($"Data Source={candidatePath};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using var command = sqlite.CreateCommand();
				command.CommandText = "UPDATE organization_effect_archive SET payload=replace(payload,$old,$new) WHERE kind='manifest'; UPDATE receipts SET location='must rollback'";
				command.Parameters.AddWithValue("$old", new string('A', 64));
				command.Parameters.AddWithValue("$new", new string('B', 64));
				await command.ExecuteNonQueryAsync();
			}
			Func<Task> conflict = () => Import(candidatePath);
			await conflict.Should().ThrowAsync<InvalidOperationException>()
				.WithMessage("Restore conflicts with immutable effect manifest or membership.");
			await using (var db = fixture.CreateDbContext())
			{
				(await db.Receipts.SingleAsync()).Location.Should().Be("Original");
				(await db.CategoryAssistanceSettings.SingleAsync()).RestoreGeneration.Should().Be(generation);
				(await db.NormalizationAiConnection.SingleAsync()).Generation.Should().Be(normalizationGeneration);
				(await db.OrganizationEffectManifests.SingleAsync()).RequestHash.Should().Be(new string('A', 64));
			}
			await using (SqliteConnection sqlite = new($"Data Source={candidatePath};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using var command = sqlite.CreateCommand();
				command.CommandText = "UPDATE organization_effect_archive SET payload=replace(payload,$old,$new) WHERE kind='manifest'";
				command.Parameters.AddWithValue("$old", new string('B', 64));
				command.Parameters.AddWithValue("$new", new string('A', 64));
				await command.ExecuteNonQueryAsync();
			}
			// Fail during business import, after the restore generations have been saved.
			await using (SqliteConnection sqlite = new($"Data Source={candidatePath};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using var command = sqlite.CreateCommand();
				command.CommandText = "UPDATE receipts SET location='must rollback'; UPDATE receipt_items SET quantity='invalid'";
				await command.ExecuteNonQueryAsync();
			}
			Func<Task> invalid = () => Import(candidatePath);
			await invalid.Should().ThrowAsync<FormatException>();
			await using (var db = fixture.CreateDbContext())
			{
				(await db.Receipts.SingleAsync()).Location.Should().Be("Original");
				(await db.CategoryAssistanceSettings.SingleAsync()).RestoreGeneration.Should().Be(generation);
				(await db.NormalizationAiConnection.SingleAsync()).Generation.Should().Be(normalizationGeneration);
				(await db.OrganizationEffectManifests.CountAsync()).Should().Be(1);
			}
			// Missing sealed membership fails before touching local state.
			await using (SqliteConnection sqlite = new($"Data Source={candidatePath};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using var command = sqlite.CreateCommand();
				command.CommandText = "DELETE FROM organization_effect_archive WHERE kind='item'";
				await command.ExecuteNonQueryAsync();
			}
			Func<Task> truncated = () => Import(candidatePath);
			await truncated.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Invalid portable taxonomy archive*");
			await using (var db = fixture.CreateDbContext())
			{
				(await db.CategoryAssistanceSettings.SingleAsync()).RestoreGeneration.Should().Be(generation);
				(await db.NormalizationAiConnection.SingleAsync()).Generation.Should().Be(normalizationGeneration);
				(await db.OrganizationEffectItems.CountAsync()).Should().Be(1);
			}
			await Import(olderPath);
			await using (var db = fixture.CreateDbContext())
			{
				(await db.OrganizationEffectManifests.SingleAsync()).RequestHash.Should().Be(new string('A', 64));
				(await db.OrganizationEffectItems.SingleAsync()).ApplyOperationId.Should().Be(item.ApplyOperationId);
				(await db.OrganizationEffectOperations.CountAsync()).Should().Be(2);
				(await db.OrganizationEffectOutcomes.SingleAsync()).Disposition.Should().Be("skipped");
				(await db.CategoryAssistanceSettings.SingleAsync()).RestoreGeneration.Should().NotBe(generation);
			}
			Func<Task> publicImport = () => Import(candidatePath, false);
			await publicImport.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Unsupported backup*");
			PortableBackupFormat.CurrentVersion.Should().Be(13);
		}
		finally
		{
			File.Delete(olderPath);
			if (candidatePath != null) { File.Delete(candidatePath); }
		}
	}
}
