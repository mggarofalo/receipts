using System.Diagnostics;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class EffectOperationIdRegistryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Fact]
	public async Task Claims_AreIdempotent_ButRejectOtherOwnersAndRollBackBothPhases()
	{
		await using var db = fixture.CreateDbContext();
		Guid manifest = Guid.NewGuid(), item = Guid.NewGuid(), apply = Guid.NewGuid(), undo = Guid.NewGuid();
		await Effect(db, manifest, item, apply, undo);
		await Effect(db, manifest, item, apply, undo);
		Func<Task> ordinary = () => Ordinary(db, apply);
		(await ordinary.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
		Guid fresh = Guid.NewGuid();
		Func<Task> wrongOwner = () => Effect(db, Guid.NewGuid(), item, apply, fresh);
		(await wrongOwner.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
		(await Count(db, fresh)).Should().Be(0);
		Func<Task> swapped = () => Effect(db, manifest, item, undo, apply);
		await swapped.Should().ThrowAsync<PostgresException>();
		Guid changedApply = Guid.NewGuid(), changedUndo = Guid.NewGuid();
		Func<Task> changedPair = () => Effect(db, manifest, item, changedApply, changedUndo);
		await changedPair.Should().ThrowAsync<PostgresException>();
		(await Count(db, changedApply)).Should().Be(0);
		(await Count(db, changedUndo)).Should().Be(0);
		await Ordinary(db, fresh);
		await Ordinary(db, fresh);
		(await Count(db, fresh)).Should().Be(1);
		Func<Task> mutate = () => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM taxonomy.\"EffectOperationIdRegistry\" WHERE \"OperationId\" = {fresh}");
		await mutate.Should().ThrowAsync<PostgresException>();
		Func<Task> update = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE taxonomy.\"EffectOperationIdRegistry\" SET \"Class\" = 'ordinary' WHERE \"OperationId\" = {apply}");
		await update.Should().ThrowAsync<PostgresException>();
		Func<Task> truncate = () => db.Database.ExecuteSqlRawAsync("TRUNCATE taxonomy.\"EffectOperationIdRegistry\"");
		await truncate.Should().ThrowAsync<PostgresException>();
	}

	[Theory]
	[InlineData(true, true)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(false, false)]
	public async Task OppositeClaims_WaitForUniqueIndex_ThenObserveCommitOrRollback(bool ordinaryFirst, bool commit)
	{
		Guid id = Guid.NewGuid(), other = Guid.NewGuid(), manifest = Guid.NewGuid(), item = Guid.NewGuid();
		await using var winner = fixture.CreateDbContext();
		await using var loser = fixture.CreateDbContext();
		await loser.Database.OpenConnectionAsync();
		int pid = await loser.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
		await using var tx = await winner.Database.BeginTransactionAsync();
		if (ordinaryFirst)
		{
			await Ordinary(winner, id);
		}
		else
		{
			await Effect(winner, manifest, item, id, other);
		}

		Task contender = ordinaryFirst ? Effect(loser, manifest, item, id, other) : Ordinary(loser, id);
		await using var observer = fixture.CreateDbContext();
		bool waiting = false;
		var timer = Stopwatch.StartNew();
		while (timer.Elapsed < TimeSpan.FromSeconds(10) && !contender.IsCompleted)
		{
			if (await observer.Database.SqlQuery<bool>($"SELECT cardinality(pg_blocking_pids({pid})) > 0 AS \"Value\"").SingleAsync())
			{
				waiting = true;
				break;
			}
			await Task.Delay(25);
		}
		if (commit)
		{
			await tx.CommitAsync();
		}
		else
		{
			await tx.RollbackAsync();
		}

		waiting.Should().BeTrue();
		if (commit)
		{
			Func<Task> finish = () => contender.WaitAsync(TimeSpan.FromSeconds(15));
			(await finish.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
		}
		else
		{
			await contender.WaitAsync(TimeSpan.FromSeconds(15));
		}

		(await Count(observer, other)).Should().Be(ordinaryFirst == commit ? 0 : 1);
	}

	[Fact]
	public async Task Migration_PreservesOrdinaryReuseAndLegacyConflict_WithoutActivatingWriters()
	{
		await using var database = await MigrationDatabase.CreateAsync(fixture, "20261028170000_AddOrganizationEffectCaptureWitnessV1");
		await using var db = database.CreateDbContext();
		Guid standaloneDisposition = Guid.NewGuid(), overlappingDisposition = Guid.NewGuid();
		await using (var tx = await db.Database.BeginTransactionAsync())
		{
			Guid category = Guid.NewGuid(), revision = Guid.NewGuid();
			Guid shared = Infrastructure.Configurations.TaxonomyConfiguration.GenesisId;
			db.TaxonomyIdentities.Add(new() { Id = category });
			db.TaxonomyRevisions.Add(new()
			{
				Id = revision,
				Sequence = 1,
				PreviousRevisionId = shared,
				Actor = "test",
				Reason = "registry",
				RecordedAt = DateTimeOffset.UtcNow,
				OperationId = shared
			});
			await db.SaveChangesAsync();
			db.TaxonomyNodes.Add(new() { RevisionId = revision, CategoryId = category, Name = "Test" });
			db.TaxonomyOperations.AddRange(new TaxonomyOperationEntity { Actor = "one", OperationId = shared, RevisionId = revision },
				new TaxonomyOperationEntity { Actor = "two", OperationId = shared, RevisionId = revision });
			// Disposition state has no FK requiring a HistoricalDispositionOperations row.
			db.HistoricalGroupDispositions.AddRange(
				new HistoricalGroupDispositionEntity
				{
					Id = Guid.NewGuid(),
					Actor = "test",
					SourceHash = "standalone",
					OperationId = standaloneDisposition,
					UpdatedAt = DateTimeOffset.UtcNow
				},
				new HistoricalGroupDispositionEntity
				{
					Id = Guid.NewGuid(),
					Actor = "test",
					SourceHash = "overlap",
					OperationId = overlappingDisposition,
					UpdatedAt = DateTimeOffset.UtcNow
				});
			Guid manifest = Guid.NewGuid();
			db.OrganizationEffectManifests.Add(new()
			{
				Id = manifest,
				Actor = "test",
				PolicyVersion = "v1",
				RequestHash = "legacy",
				TaxonomyRevisionId = revision,
				CapturedAt = DateTimeOffset.UtcNow
			});
			await db.SaveChangesAsync();
			db.OrganizationEffectItems.Add(new()
			{
				ManifestId = manifest,
				ItemId = Guid.NewGuid(),
				ReceiptId = Guid.NewGuid(),
				ItemRevision = Guid.NewGuid(),
				ReceiptRevision = Guid.NewGuid(),
				TaxonomyRevisionId = revision,
				TargetCategoryId = category,
				ApplyOperationId = shared,
				UndoOperationId = overlappingDisposition
			});
			await db.SaveChangesAsync();
			await tx.CommitAsync();
		}
		await db.GetService<IMigrator>().MigrateAsync();
		(await db.HistoricalDispositionOperations.CountAsync()).Should().Be(0);
		(await db.HistoricalGroupDispositions.CountAsync()).Should().Be(2);
		(await db.Database.SqlQuery<string>($"SELECT \"Class\" AS \"Value\" FROM taxonomy.\"EffectOperationIdRegistry\" WHERE \"OperationId\" = {standaloneDisposition}").SingleAsync())
			.Should().Be("ordinary");
		await Ordinary(db, standaloneDisposition);
		Func<Task> occupiedDisposition = () => Effect(db, Guid.NewGuid(), Guid.NewGuid(), standaloneDisposition, Guid.NewGuid());
		await occupiedDisposition.Should().ThrowAsync<PostgresException>();
		(await db.Database.SqlQuery<string>($"SELECT \"Class\" AS \"Value\" FROM taxonomy.\"EffectOperationIdRegistry\" WHERE \"OperationId\" = {overlappingDisposition}").SingleAsync())
			.Should().Be("legacy_blocked");
		Guid id = Infrastructure.Configurations.TaxonomyConfiguration.GenesisId;
		(await db.Database.SqlQuery<string>($"SELECT \"Class\" AS \"Value\" FROM taxonomy.\"EffectOperationIdRegistry\" WHERE \"OperationId\" = {id}").SingleAsync())
			.Should().Be("legacy_blocked");
		(await db.TaxonomyOperations.CountAsync(x => x.OperationId == id)).Should().Be(2);
		(await db.OrganizationEffectOperations.CountAsync()).Should().Be(2);
		Func<Task> blocked = () => Ordinary(db, id);
		await blocked.Should().ThrowAsync<PostgresException>();
		var owner = await db.OrganizationEffectItems.SingleAsync();
		Func<Task> blockedEffect = () => Effect(db, owner.ManifestId, owner.ItemId, owner.ApplyOperationId, owner.UndoOperationId);
		await blockedEffect.Should().ThrowAsync<PostgresException>();
		(await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM taxonomy.\"OrganizationEffectCaptureWitnessesV1\"").SingleAsync()).Should().Be(0);
		// A writer that has not joined the protocol still bypasses the registry.
		Guid bypass = Guid.NewGuid();
		db.TaxonomyOperations.Add(new() { Actor = "later", OperationId = bypass, RevisionId = owner.TaxonomyRevisionId });
		await db.SaveChangesAsync();
		(await Count(db, bypass)).Should().Be(0);
	}

	private static Task Ordinary(ApplicationDbContext db, Guid id) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT taxonomy.claim_ordinary_operation_id({id})");
	private static Task Effect(ApplicationDbContext db, Guid manifest, Guid item, Guid apply, Guid undo) =>
		db.Database.ExecuteSqlInterpolatedAsync($"SELECT taxonomy.claim_effect_operation_ids({manifest},{item},{apply},{undo})");
	private static Task<long> Count(ApplicationDbContext db, Guid id) =>
		db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM taxonomy.\"EffectOperationIdRegistry\" WHERE \"OperationId\" = {id}").SingleAsync();
}
