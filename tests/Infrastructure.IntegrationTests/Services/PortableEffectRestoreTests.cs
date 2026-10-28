using FluentAssertions;
using Infrastructure.Configurations;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class PortableEffectRestoreTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	[Fact]
	public async Task CandidateRestore_RoundTripsResealsReconstructsReservationsAndOrdersOutcomes()
	{
		var (history, evidence) = await SeedAsync();
		long originalStamp;
		await using (ApplicationDbContext source = fixture.CreateDbContext())
		{
			await using var transaction = await source.Database.BeginTransactionAsync();
			await PortableTaxonomyArchive.RestoreEffectsCandidateAsync(source, history, history, evidence, default);
			await transaction.CommitAsync();
			originalStamp = await source.OrganizationEffectManifests.AsNoTracking()
				.Select(m => EF.Property<long>(m, "WriteTransaction")).SingleAsync();
			await using SqliteConnection archive = new("Data Source=:memory:");
			await archive.OpenAsync();
			await PortableTaxonomyArchive.ExportEffectsCandidateAsync(source, archive, history, default);
			evidence = await PortableTaxonomyArchive.ReadEffectsCandidateAsync(archive, history, default);
		}
		await ReplaceDatabaseAsync();
		await SeedAsync(history, evidence);
		await using (ApplicationDbContext destination = fixture.CreateDbContext())
		{
			await using var transaction = await destination.Database.BeginTransactionAsync();
			await PortableTaxonomyArchive.RestoreEffectsCandidateAsync(destination, history, history,
				evidence with { Outcomes = [.. evidence.Outcomes.OrderByDescending(o => o.Phase)] }, default);
			await transaction.CommitAsync();
		}
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await verify.OrganizationEffectManifests.Select(m => EF.Property<long>(m, "WriteTransaction")).SingleAsync())
			.Should().NotBe(originalStamp);
		var storedManifest = await verify.OrganizationEffectManifests.AsNoTracking().SingleAsync();
		var storedItem = await verify.OrganizationEffectItems.AsNoTracking().SingleAsync();
		PortableTaxonomyArchive.SealEffectManifest(storedManifest, [storedItem]).Should().BeEquivalentTo(evidence.Manifests[0]);
		var reservations = await verify.OrganizationEffectOperations.AsNoTracking().ToListAsync();
		reservations.Select(r => (r.OperationId, r.ManifestId, r.ItemId, r.Phase)).Should().BeEquivalentTo(
			new[] { (storedItem.ApplyOperationId, storedItem.ManifestId, storedItem.ItemId, "apply"),
				(storedItem.UndoOperationId, storedItem.ManifestId, storedItem.ItemId, "undo") });
		(await verify.OrganizationEffectOutcomes.Select(o => o.AssignmentId).ToListAsync())
			.Should().BeEquivalentTo(evidence.Outcomes.Select(o => o.AssignmentId));
		await using (var retry = await verify.Database.BeginTransactionAsync())
		{
			await PortableTaxonomyArchive.RestoreEffectsCandidateAsync(verify, history, history, evidence, default);
			await PortableTaxonomyArchive.RestoreEffectsCandidateAsync(verify, history, history, null, default);
			await retry.CommitAsync();
		}
		(await verify.OrganizationEffectOutcomes.CountAsync()).Should().Be(2);
		// The destination transaction has closed: its restored manifest is sealed.
		verify.OrganizationEffectItems.Add(new()
		{
			ManifestId = storedItem.ManifestId,
			ItemId = Guid.NewGuid(),
			ReceiptId = storedItem.ReceiptId,
			TaxonomyRevisionId = storedItem.TaxonomyRevisionId,
			TargetCategoryId = storedItem.TargetCategoryId,
			ApplyOperationId = Guid.NewGuid(),
			UndoOperationId = Guid.NewGuid()
		});
		Func<Task> extend = () => verify.SaveChangesAsync();
		(await extend.Should().ThrowAsync<DbUpdateException>()).WithInnerException<PostgresException>();
		PortableBackupFormat.CurrentVersion.Should().Be(13);
	}

	[Fact]
	public async Task CandidateRestore_RollsBackPartialManifestEvenIfCallerCommitsAfterFailure()
	{
		var (history, evidence) = await SeedAsync();
		await using FailingContext db = new(fixture.CreateOptions());
		await using var transaction = await db.Database.BeginTransactionAsync();
		Func<Task> restore = () => PortableTaxonomyArchive.RestoreEffectsCandidateAsync(db, history, history, evidence, default);
		await restore.Should().ThrowAsync<InvalidOperationException>().WithMessage("injected after item insertion");
		db.ChangeTracker.HasChanges().Should().BeFalse();
		await transaction.CommitAsync();
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await verify.OrganizationEffectManifests.CountAsync()).Should().Be(0);
		(await verify.OrganizationEffectItems.CountAsync()).Should().Be(0);
		(await verify.OrganizationEffectOperations.CountAsync()).Should().Be(0);
		(await verify.OrganizationEffectOutcomes.CountAsync()).Should().Be(0);
		(await verify.Classifications.CountAsync()).Should().Be(2);
	}

	[Fact]
	public async Task CandidateRestore_RejectsInvalidLineageBeforeWritingAndRequiresOuterTransaction()
	{
		var (history, evidence) = await SeedAsync();
		await using ApplicationDbContext db = fixture.CreateDbContext();
		Func<Task> withoutTransaction = () => PortableTaxonomyArchive.RestoreEffectsCandidateAsync(db, history, history, evidence, default);
		await withoutTransaction.Should().ThrowAsync<InvalidDataException>().WithMessage("*restore transaction*");
		await using var transaction = await db.Database.BeginTransactionAsync();
		var invalid = evidence with { Outcomes = [evidence.Outcomes.Single(o => o.Phase == "undo")] };
		Func<Task> restore = () => PortableTaxonomyArchive.RestoreEffectsCandidateAsync(db, history, history, invalid, default);
		await restore.Should().ThrowAsync<InvalidDataException>();
		await transaction.CommitAsync();
		(await db.OrganizationEffectManifests.CountAsync()).Should().Be(0);
	}

	private async Task<(PortableTaxonomyArchive.State History, PortableTaxonomyArchive.EffectEvidence Evidence)> SeedAsync(
		PortableTaxonomyArchive.State? history = null, PortableTaxonomyArchive.EffectEvidence? evidence = null)
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		if (history == null)
		{
			Guid category = Guid.NewGuid(), revision = Guid.NewGuid(), manifest = Guid.NewGuid(), itemId = Guid.NewGuid();
			DateTimeOffset captured = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
			OrganizationEffectItemEntity item = new()
			{
				ManifestId = manifest,
				ItemId = itemId,
				ReceiptId = Guid.NewGuid(),
				Store = "Store",
				RawDescription = "Item",
				ItemRevision = Guid.NewGuid(),
				ReceiptRevision = Guid.NewGuid(),
				TaxonomyRevisionId = revision,
				TargetCategoryId = category,
				ApplyOperationId = Guid.NewGuid(),
				UndoOperationId = Guid.NewGuid(),
				DecisionEvidenceJson = "{\"n\":1e2}"
			};
			OrganizationEffectManifestEntity header = new()
			{
				Id = manifest,
				Actor = "test",
				RequestHash = new string('A', 64),
				PolicyVersion = "candidate",
				TaxonomyRevisionId = revision,
				CapturedAt = captured
			};
			PurchaseClassificationEntity apply = new()
			{
				Id = Guid.NewGuid(),
				ReceiptItemId = itemId,
				Sequence = 1,
				TaxonomyRevisionId = revision,
				CategoryId = category,
				OperationId = item.ApplyOperationId,
				Actor = header.Actor,
				RecordedAt = captured
			};
			PurchaseClassificationEntity undo = new()
			{
				Id = Guid.NewGuid(),
				ReceiptItemId = itemId,
				Sequence = 2,
				TaxonomyRevisionId = revision,
				PreviousAssignmentId = apply.Id,
				OperationId = item.UndoOperationId,
				Actor = header.Actor,
				RecordedAt = captured
			};
			history = new([new() { Id = category }], [], [new()
			{
				Id = revision, PreviousRevisionId = TaxonomyConfiguration.GenesisId, Sequence = 1,
				Actor = "test", Reason = "test", OperationId = Guid.NewGuid(), RecordedAt = captured
			}], [new() { RevisionId = revision, CategoryId = category, Name = "Target" }], [], [apply, undo], [], [], [], [], []);
			evidence = new([PortableTaxonomyArchive.SealEffectManifest(header, [item])], [item],
				new[] { ("undo", undo), ("apply", apply) }.Select(p => new OrganizationEffectOutcomeEntity
				{
					ManifestId = manifest,
					ItemId = itemId,
					Phase = p.Item1,
					Disposition = "applied",
					Reason = "policy",
					AssignmentId = p.Item2.Id,
					ItemRevision = item.ItemRevision,
					ReceiptRevision = item.ReceiptRevision,
					RecordedAt = captured
				}).ToList());
		}
		var sourceItem = evidence!.Items[0];
		var receipt = SampleData.Entities.ReceiptEntityGenerator.Generate();
		receipt.Id = sourceItem.ReceiptId;
		var source = SampleData.Entities.ReceiptItemEntityGenerator.Generate(receipt.Id);
		source.Id = sourceItem.ItemId;
		source.Category = "";
		source.Subcategory = null;
		db.AddRange(receipt, source);
		await db.SaveChangesAsync();
		await using var transaction = await db.Database.BeginTransactionAsync();
		db.TaxonomyIdentities.AddRange(history.Identities);
		db.TaxonomyRevisions.AddRange(history.Revisions);
		await db.SaveChangesAsync();
		db.TaxonomyNodes.AddRange(history.Nodes);
		await db.SaveChangesAsync();
		foreach (var assignment in history.Assignments.OrderBy(a => a.Sequence))
		{
			db.Classifications.Add(assignment);
			await db.SaveChangesAsync();
		}
		await transaction.CommitAsync();
		return (history, evidence);
	}

	private sealed class FailingContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
	{
		private int saves;
		public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
		{
			int result = await base.SaveChangesAsync(cancellationToken);
			if (++saves == 2) { throw new InvalidOperationException("injected after item insertion"); }
			return result;
		}
	}
}
