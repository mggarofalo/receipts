using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Application.Normalization;
using Application.Taxonomy;
using Common;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class OrganizationEffectCaptureWitnessTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	private const string Actor = "capture-admin";
	private static readonly DateTimeOffset Captured = new(2026, 10, 28, 12, 34, 56, TimeSpan.Zero);
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
	private sealed record Seed(Guid Baseline, Guid Mapping, Guid Revision, Guid Category,
		OrganizationEffectContractV1.Source[] Sources, Guid?[] Assignments, string Fingerprint);
	private sealed record Capture(Guid Event, OrganizationEffectContractV1.ManifestRequest Request);

	[Fact]
	public async Task Seal_DerivesExactDotNetCanonicalBytesAndHash_PreservingDeclaredOrder()
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		await using var tx = await db.Database.BeginTransactionAsync();
		var capture = await DeclareAsync(db, seed);
		Guid[] ids = capture.Request.Members.Select(x => x.Source.ItemId).ToArray();
		await SealAsync(db, capture, ids);
		byte[] bytes = await db.Database.SqlQuery<byte[]>($"SELECT \"CanonicalRequest\" AS \"Value\" FROM taxonomy.\"OrganizationEffectCaptureWitnessesV1\" WHERE \"ManifestId\" = {capture.Request.ManifestId}").SingleAsync();
		Encoding.UTF8.GetString(bytes).Should().Be(OrganizationEffectContractV1.WriteManifestRequest(capture.Request));
		(await db.Database.SqlQuery<string>($"SELECT \"RequestHash\" AS \"Value\" FROM taxonomy.\"OrganizationEffectCaptureWitnessesV1\" WHERE \"ManifestId\" = {capture.Request.ManifestId}").SingleAsync())
			.Should().Be(OrganizationEffectContractV1.ManifestRequestHash(capture.Request));
		(await db.Database.SqlQuery<Guid[]>($"SELECT \"OrderedItemIds\" AS \"Value\" FROM taxonomy.\"OrganizationEffectCaptureWitnessesV1\" WHERE \"ManifestId\" = {capture.Request.ManifestId}").SingleAsync()).Should().Equal(ids);
		OrganizationEffectContractV1.ManifestRequestHash(capture.Request with { Members = capture.Request.Members.Reverse().ToArray() })
			.Should().NotBe(OrganizationEffectContractV1.ManifestRequestHash(capture.Request));
		await tx.CommitAsync();
		foreach (string sql in new[] { "UPDATE taxonomy.\"OrganizationEffectCaptureWitnessesV1\" SET \"RequestHash\" = 'forged'", "DELETE FROM taxonomy.\"OrganizationEffectCaptureWitnessesV1\"", "TRUNCATE taxonomy.\"OrganizationEffectCaptureWitnessesV1\"" })
		{
			Func<Task> mutate = () => db.Database.ExecuteSqlRawAsync(sql);
			await mutate.Should().ThrowAsync<PostgresException>();
		}
	}


	[Fact]
	public async Task PolicyFence_DerivesContractBytesFromLockedSettings_AndRetainsEvidence()
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		await db.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"Settings\" SET \"Enabled\"=true, \"Provider\"='jev', \"Model\"='jev-1.14.0'");
		await using var tx = await db.Database.BeginTransactionAsync();
		var capture = await DeclareAsync(db, seed);
		await SealAsync(db, capture, fence: true);
		var settings = await db.CategoryAssistanceSettings.AsNoTracking().SingleAsync();
		var expected = new OrganizationEffectFenceContractV1.CaptureEvidence(1, capture.Request.ManifestId,
			OrganizationEffectContractV1.ManifestRequestHash(capture.Request), settings.Revision, settings.RestoreGeneration);
		byte[] bytes = await db.Database.SqlQueryRaw<byte[]>("SELECT \"CanonicalEvidence\" AS \"Value\" FROM taxonomy.\"OrganizationEffectCaptureFencesV1\"").SingleAsync();
		Encoding.UTF8.GetString(bytes).Should().Be(OrganizationEffectFenceContractV1.WriteCapture(expected));
		(await db.Database.SqlQueryRaw<string>("SELECT \"EvidenceHash\" AS \"Value\" FROM taxonomy.\"OrganizationEffectCaptureFencesV1\"").SingleAsync())
			.Should().Be(OrganizationEffectFenceContractV1.CaptureHash(expected));
		await tx.CommitAsync();
		foreach (string sql in new[] { "UPDATE taxonomy.\"OrganizationEffectCaptureFencesV1\" SET \"EvidenceHash\"='forged'", "DELETE FROM taxonomy.\"OrganizationEffectCaptureFencesV1\"", "TRUNCATE taxonomy.\"OrganizationEffectCaptureFencesV1\"" })
		{
			Func<Task> mutate = () => db.Database.ExecuteSqlRawAsync(sql);
			await mutate.Should().ThrowAsync<PostgresException>();
		}
	}

	[Fact]
	public async Task PolicyFence_OpaqueWitnessCannotBeUpgradedOrGivenForgedFacts()
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		await using (var tx = await db.Database.BeginTransactionAsync())
		{
			var capture = await DeclareAsync(db, seed);
			await SealAsync(db, capture);
			await tx.CommitAsync();
		}
		(await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM taxonomy.\"OrganizationEffectCaptureFencesV1\"").SingleAsync()).Should().Be(0);
		Func<Task> upgrade = () => db.Database.ExecuteSqlRawAsync("UPDATE taxonomy.\"OrganizationEffectCaptureWitnessesV1\" SET \"CaptureFenceVersion\"=1");
		await upgrade.Should().ThrowAsync<PostgresException>();
		Func<Task> fabricate = () => db.Database.ExecuteSqlRawAsync("INSERT INTO taxonomy.\"OrganizationEffectCaptureFencesV1\" SELECT \"ManifestId\",1,gen_random_uuid(),gen_random_uuid(),decode('00','hex'),'forged' FROM taxonomy.\"OrganizationEffectCaptureWitnessesV1\"");
		await fabricate.Should().ThrowAsync<PostgresException>();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PolicyFence_SettingsRaceWaitsThenReadsCommittedDisableOrGeneration(bool disable)
	{
		var seed = await SeedAsync();
		await using var blocker = fixture.CreateDbContext();
		await blocker.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"Settings\" SET \"Enabled\"=true, \"Provider\"='jev', \"Model\"='jev-1.14.0'");
		await using var tx = await blocker.Database.BeginTransactionAsync();
		await blocker.Database.ExecuteSqlRawAsync("SELECT 1 FROM category_assistance.\"Settings\" WHERE \"Id\"=1 FOR UPDATE");
		Guid generation = Guid.NewGuid();
		await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE category_assistance.\"Settings\" SET \"Enabled\"={!disable}, \"RestoreGeneration\"={generation}, \"Revision\"={Guid.NewGuid()}");
		await using var contender = fixture.CreateDbContext();
		await contender.Database.OpenConnectionAsync();
		int pid = await contender.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
		int blockerPid = await blocker.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
		async Task CaptureAsync()
		{
			await using var captureTx = await contender.Database.BeginTransactionAsync();
			var capture = await DeclareAsync(contender, seed);
			await SealAsync(contender, capture, fence: true);
			await captureTx.CommitAsync();
		}
		Task pending = CaptureAsync();
		await using var observer = fixture.CreateDbContext();
		bool waiting = false;
		var timer = Stopwatch.StartNew();
		while (timer.Elapsed < TimeSpan.FromSeconds(10) && !pending.IsCompleted)
		{
			if (await observer.Database.SqlQuery<bool>($"SELECT {blockerPid}=ANY(pg_blocking_pids({pid})) AS \"Value\"").SingleAsync()) { waiting = true; break; }
			await Task.Delay(25);
		}
		await tx.CommitAsync();
		waiting.Should().BeTrue();
		if (disable)
		{
			Func<Task> finish = () => pending.WaitAsync(TimeSpan.FromSeconds(20));
			await finish.Should().ThrowAsync<PostgresException>();
			await AssertNoCaptureAsync();
		}
		else
		{
			await pending.WaitAsync(TimeSpan.FromSeconds(20));
			(await observer.Database.SqlQueryRaw<Guid>("SELECT \"RestoreGeneration\" AS \"Value\" FROM taxonomy.\"OrganizationEffectCaptureFencesV1\"").SingleAsync()).Should().Be(generation);
		}
	}



	[Fact]
	public async Task PolicyFence_SealedCaptureBlocksSettingsUpdateUntilCommit()
	{
		var seed = await SeedAsync();
		await using var captureDb = fixture.CreateDbContext();
		await captureDb.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"Settings\" SET \"Enabled\"=true, \"Provider\"='jev', \"Model\"='jev-1.14.0'");
		var policy = await captureDb.CategoryAssistanceSettings.AsNoTracking().SingleAsync();
		await using var captureTx = await captureDb.Database.BeginTransactionAsync();
		var capture = await DeclareAsync(captureDb, seed);
		await SealAsync(captureDb, capture, fence: true);
		await using var updateDb = fixture.CreateDbContext();
		await updateDb.Database.OpenConnectionAsync();
		int updatePid = await updateDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
		int capturePid = await captureDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
		async Task UpdateAsync()
		{
			await using var updateTx = await updateDb.Database.BeginTransactionAsync();
			await CategoryAssistanceSettingsStore.UpdateInTransactionAsync(updateDb, Actor, policy.Revision,
				new(false, policy.Provider, policy.Model, policy.DailyLimit, policy.PendingLimit, policy.Revision), DateTimeOffset.UtcNow);
			await updateTx.CommitAsync();
		}
		Task pending = UpdateAsync();
		bool waiting = await IsBlockedByAsync(updatePid, capturePid, pending);
		await captureTx.CommitAsync();
		await pending.WaitAsync(TimeSpan.FromSeconds(20));
		waiting.Should().BeTrue();
		await using var observer = fixture.CreateDbContext();
		var updated = await observer.CategoryAssistanceSettings.AsNoTracking().SingleAsync();
		updated.Enabled.Should().BeFalse();
		updated.Revision.Should().NotBe(policy.Revision);
		(await observer.Database.SqlQueryRaw<Guid>("SELECT \"SettingsRevision\" AS \"Value\" FROM taxonomy.\"OrganizationEffectCaptureFencesV1\"").SingleAsync()).Should().Be(policy.Revision);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PolicyFence_RestoreWaitsForCapture_ThenRotatesGenerationAtomically(bool rollbackRestore)
	{
		var seed = await SeedAsync();
		await using var captureDb = fixture.CreateDbContext();
		await captureDb.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"Settings\" SET \"Enabled\"=true, \"Provider\"='jev', \"Model\"='jev-1.14.0'");
		var policy = await captureDb.CategoryAssistanceSettings.AsNoTracking().SingleAsync();
		await using var captureTx = await captureDb.Database.BeginTransactionAsync();
		var capture = await DeclareAsync(captureDb, seed);
		await SealAsync(captureDb, capture, fence: true);
		await using var restoreDb = fixture.CreateDbContext();
		await restoreDb.Database.OpenConnectionAsync();
		int restorePid = await restoreDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
		int capturePid = await captureDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
		Guid restoredGeneration = Guid.Empty, restoredRevision = Guid.Empty;
		async Task RestoreAsync()
		{
			await using var restoreTx = await restoreDb.Database.BeginTransactionAsync();
			await Infrastructure.Utilities.NormalizationWriteGuard.AcquireWriteGateAsync(restoreDb, default);
			await CategoryAssistanceRestoreFence.FenceAsync(restoreDb, default);
			var restored = await restoreDb.CategoryAssistanceSettings.AsNoTracking().SingleAsync();
			restoredGeneration = restored.RestoreGeneration;
			restoredRevision = restored.Revision;
			restored.Enabled.Should().BeFalse();
			restored.Revision.Should().NotBe(policy.Revision);
			restoredGeneration.Should().NotBe(policy.RestoreGeneration);
			if (rollbackRestore) { await restoreTx.RollbackAsync(); }
			else { await restoreTx.CommitAsync(); }
		}
		Task pending = RestoreAsync();
		bool waiting = await IsBlockedByAsync(restorePid, capturePid, pending);
		await captureTx.CommitAsync();
		await pending.WaitAsync(TimeSpan.FromSeconds(20));
		waiting.Should().BeTrue();
		await using var observer = fixture.CreateDbContext();
		var current = await observer.CategoryAssistanceSettings.AsNoTracking().SingleAsync();
		current.RestoreGeneration.Should().Be(rollbackRestore ? policy.RestoreGeneration : restoredGeneration);
		current.Enabled.Should().Be(rollbackRestore);
		current.Revision.Should().Be(rollbackRestore ? policy.Revision : restoredRevision);
		(await observer.CategoryAssistanceSettingsAudits.CountAsync(x => x.Actor == "backup_restore")).Should().Be(rollbackRestore ? 0 : 1);
		(await observer.Database.SqlQueryRaw<Guid>("SELECT \"RestoreGeneration\" AS \"Value\" FROM taxonomy.\"OrganizationEffectCaptureFencesV1\"").SingleAsync()).Should().Be(policy.RestoreGeneration);
	}

	private async Task<bool> IsBlockedByAsync(int waitingPid, int blockerPid, Task pending)
	{
		await using var observer = fixture.CreateDbContext();
		var timer = Stopwatch.StartNew();
		while (timer.Elapsed < TimeSpan.FromSeconds(10) && !pending.IsCompleted)
		{
			if (await observer.Database.SqlQuery<bool>($"SELECT {blockerPid}=ANY(pg_blocking_pids({waitingPid})) AS \"Value\"").SingleAsync()) { return true; }
			await Task.Delay(25);
		}
		return false;
	}

	[Fact]
	public async Task PolicyFence_GateBeforeBaseline_AllowsSameTransactionCapture()
	{
		var seed = await SeedAsync(createBaseline: false);
		await using var db = fixture.CreateDbContext();
		await db.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"Settings\" SET \"Enabled\"=true, \"Provider\"='jev', \"Model\"='jev-1.14.0'");
		await using var tx = await db.Database.BeginTransactionAsync();
		await Infrastructure.Utilities.NormalizationWriteGuard.AcquireWriteGateAsync(db, default);
		await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO taxonomy.\"MappingAuthorityBaseline\" (\"Singleton\",\"Id\",\"StreamId\") VALUES (1,{seed.Baseline},{Guid.NewGuid()})");
		var capture = await DeclareAsync(db, seed);
		await SealAsync(db, capture, fence: true);
		await tx.CommitAsync();
		(await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM taxonomy.\"OrganizationEffectCaptureFencesV1\"").SingleAsync()).Should().Be(1);
	}

	[Fact]
	public async Task PolicyFence_RollbackRemovesWitnessFenceAndDeclarations()
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		await db.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"Settings\" SET \"Enabled\"=true, \"Provider\"='jev', \"Model\"='jev-1.14.0'");
		await using var tx = await db.Database.BeginTransactionAsync();
		var capture = await DeclareAsync(db, seed);
		await SealAsync(db, capture, fence: true);
		await tx.RollbackAsync();
		await AssertNoCaptureAsync();
		(await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM taxonomy.\"OrganizationEffectCaptureFencesV1\"").SingleAsync()).Should().Be(0);
	}

	[Theory]
	[InlineData("empty")]
	[InlineData("duplicate")]
	[InlineData("missing")]
	[InlineData("unknown")]
	[InlineData("reversed")]
	public async Task Seal_RejectsIncompleteOrChangedOrderedMembership(string vector)
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		await using var tx = await db.Database.BeginTransactionAsync();
		var capture = await DeclareAsync(db, seed);
		Guid[] ids = capture.Request.Members.Select(x => x.Source.ItemId).ToArray();
		ids = vector switch { "empty" => [], "duplicate" => [ids[0], ids[0]], "missing" => [ids[0]], "unknown" => [ids[0], Guid.NewGuid()], _ => ids.Reverse().ToArray() };
		Func<Task> seal = () => SealAsync(db, capture, ids);
		await seal.Should().ThrowAsync<PostgresException>();
		await tx.RollbackAsync();
		await AssertNoCaptureAsync();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SealedMembership_RejectsAdditionalItemImmediately(bool commitBeforeAttempt)
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		await using var tx = await db.Database.BeginTransactionAsync();
		var capture = await DeclareAsync(db, seed);
		await SealAsync(db, capture);
		if (commitBeforeAttempt)
		{
			await tx.CommitAsync();
		}
		else
		{
			await tx.CreateSavepointAsync("sealed");
		}

		var extra = capture.Request.Members[0] with
		{
			Source = capture.Request.Members[0].Source with { ItemId = Guid.NewGuid() },
			ApplyOperationId = Guid.NewGuid(),
			UndoOperationId = Guid.NewGuid(),
			ExpectedAssignmentId = null
		};
		Func<Task> extend = () => InsertItemAsync(db, extra);
		var failure = (await extend.Should().ThrowAsync<PostgresException>()).Which;
		failure.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
		failure.MessageText.Should().Contain("sealed");
		if (!commitBeforeAttempt) { await tx.RollbackToSavepointAsync("sealed"); await tx.CommitAsync(); }
		(await db.OrganizationEffectItems.CountAsync()).Should().Be(2);
		(await db.OrganizationEffectOperations.CountAsync()).Should().Be(4);
	}

	[Theory]
	[InlineData("manifest")]
	[InlineData("event")]
	[InlineData("caught-seal")]
	public async Task TypedDeclarations_CannotCommitWithoutWitness_EvenAfterCaughtSealFailure(string declaration)
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		await using var tx = await db.Database.BeginTransactionAsync();
		var capture = await DeclareAsync(db, seed, declaration != "event", declaration != "manifest");
		if (declaration == "caught-seal")
		{
			await tx.CreateSavepointAsync("attempt");
			Func<Task> fail = () => SealAsync(db, capture, []);
			await fail.Should().ThrowAsync<PostgresException>();
			await tx.RollbackToSavepointAsync("attempt");
		}
		Func<Task> commit = () => tx.CommitAsync();
		(await commit.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
		await AssertNoCaptureAsync();
	}

	[Theory]
	[InlineData("item-revision")]
	[InlineData("receipt-revision")]
	[InlineData("description")]
	[InlineData("store")]
	[InlineData("item-deleted")]
	[InlineData("receipt-deleted")]
	[InlineData("assignment")]
	[InlineData("mixed-group")]
	public async Task Seal_RejectsStaleOrIneligibleSource(string change)
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		await using var tx = await db.Database.BeginTransactionAsync();
		var capture = await DeclareAsync(db, seed, mixedGroup: change == "mixed-group");
		Guid item = seed.Sources[0].ItemId, receipt = seed.Sources[0].ReceiptId;
		switch (change)
		{
			case "item-revision": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"NormalizationRevision\" = gen_random_uuid() WHERE \"Id\" = {item}"); break;
			case "receipt-revision": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"Receipts\" SET \"NormalizationRevision\" = gen_random_uuid() WHERE \"Id\" = {receipt}"); break;
			case "description": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"Description\" = 'changed' WHERE \"Id\" = {item}"); break;
			case "store": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"Receipts\" SET \"Location\" = 'changed' WHERE \"Id\" = {receipt}"); break;
			case "item-deleted": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"DeletedAt\" = now() WHERE \"Id\" = {item}"); break;
			case "receipt-deleted": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"Receipts\" SET \"DeletedAt\" = now() WHERE \"Id\" = {receipt}"); break;
			case "assignment":
				Guid assignment = Guid.NewGuid();
				db.Classifications.Add(new() { Id = assignment, ReceiptItemId = item, Sequence = seed.Assignments[0] is null ? 1 : 2, PreviousAssignmentId = seed.Assignments[0], CategoryId = seed.Category, TaxonomyRevisionId = seed.Revision, RecordedAt = Captured, Actor = Actor, OperationId = Guid.NewGuid() });
				var current = await db.CurrentClassifications.SingleOrDefaultAsync(x => x.ReceiptItemId == item);
				if (current is null)
				{
					db.CurrentClassifications.Add(new() { ReceiptItemId = item, AssignmentId = assignment });
				}
				else
				{
					current.AssignmentId = assignment;
				}

				await db.SaveChangesAsync(); break;
		}
		Func<Task> seal = () => SealAsync(db, capture);
		await seal.Should().ThrowAsync<PostgresException>();
		await tx.RollbackAsync();
		await AssertNoCaptureAsync();
	}

	[Theory]
	[InlineData("revoked")]
	[InlineData("superseded")]
	[InlineData("taxonomy")]
	public async Task Seal_RejectsCommittedAuthorityChanges(string change)
	{
		var seed = await SeedAsync();
		if (change == "taxonomy")
		{
			TaxonomyService service = new(new Factory(fixture), TimeProvider.System);
			await service.ChangeAsync(Actor, new(seed.Revision, Guid.NewGuid(), "Rename", new[] { new TaxonomyEdit("rename", seed.Category, Name: "Changed") }));
		}
		else
		{
			CategoryMappingService service = new(new Factory(fixture), TimeProvider.System);
			var source = new NormalizationSourceKey(seed.Sources[0].Store, seed.Sources[0].RawDescription, seed.Sources[0].ItemCode);
			await service.ChangeAsync(Actor, new(source, seed.Fingerprint, seed.Mapping, seed.Revision,
				change == "revoked" ? null : seed.Category, Guid.NewGuid(), "Change approval"));
		}
		await using var db = fixture.CreateDbContext();
		await using var tx = await db.Database.BeginTransactionAsync();
		// The journal itself can reject stale authority before witness creation.
		Func<Task> capture = async () => { var candidate = await DeclareAsync(db, seed); await SealAsync(db, candidate); };
		await capture.Should().ThrowAsync<PostgresException>();
		await tx.RollbackAsync();
		await AssertNoCaptureAsync();
	}

	[Theory]
	[InlineData(IsolationLevel.RepeatableRead)]
	[InlineData(IsolationLevel.Serializable)]
	public async Task Capture_RefusesSnapshotIsolation(IsolationLevel isolation)
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		await using var tx = await db.Database.BeginTransactionAsync(isolation);
		Func<Task> capture = async () => { var candidate = await DeclareAsync(db, seed); await SealAsync(db, candidate); };
		(await capture.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("25000");
		await tx.RollbackAsync();
		await AssertNoCaptureAsync();
	}

	[Fact]
	public async Task Rollback_RemovesWitnessMembersReservationsAndJournalPosition()
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		await using (var tx = await db.Database.BeginTransactionAsync())
		{
			var capture = await DeclareAsync(db, seed);
			await SealAsync(db, capture);
			await tx.RollbackAsync();
		}
		await AssertNoCaptureAsync();
		await using (var tx = await db.Database.BeginTransactionAsync())
		{
			var capture = await DeclareAsync(db, seed);
			await SealAsync(db, capture);
			await tx.CommitAsync();
		}
		(await db.Database.SqlQueryRaw<long>("SELECT \"Sequence\" AS \"Value\" FROM taxonomy.\"MappingAuthorityTip\"").SingleAsync()).Should().Be(1);
	}

	[Fact]
	public async Task Capture_WaitsForTaxonomyHeadLock_ThenRechecksSource()
	{
		var seed = await SeedAsync();
		await using var blocker = fixture.CreateDbContext();
		await using var tx = await blocker.Database.BeginTransactionAsync();
		await blocker.Database.ExecuteSqlRawAsync("SELECT 1 FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE");
		await using var contender = fixture.CreateDbContext();
		await contender.Database.OpenConnectionAsync();
		int pid = await contender.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
		int blockerPid = await blocker.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
		Task capture = CaptureContenderAsync(contender, seed);
		await using var observer = fixture.CreateDbContext();
		bool waiting = false;
		var timer = Stopwatch.StartNew();
		while (timer.Elapsed < TimeSpan.FromSeconds(10) && !capture.IsCompleted)
		{
			if (await observer.Database.SqlQuery<bool>($"SELECT {blockerPid} = ANY(pg_blocking_pids({pid})) AS \"Value\"").SingleAsync()) { waiting = true; break; }
			await Task.Delay(25);
		}
		await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"NormalizationRevision\" = gen_random_uuid() WHERE \"Id\" = {seed.Sources[0].ItemId}");
		await tx.CommitAsync();
		waiting.Should().BeTrue();
		Func<Task> finish = () => capture.WaitAsync(TimeSpan.FromSeconds(20));
		await finish.Should().ThrowAsync<PostgresException>();
		await AssertNoCaptureAsync();
	}

	[Fact]
	public async Task Seal_RejectsJournalEventForAnotherCapture()
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		await using var tx = await db.Database.BeginTransactionAsync();
		var capture = await DeclareAsync(db, seed, wrongCaptureId: true);
		Func<Task> seal = () => SealAsync(db, capture);
		(await seal.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
		await tx.RollbackAsync();
		await AssertNoCaptureAsync();
	}
	[Theory]
	[InlineData("Legacy category", null, false)]
	[InlineData("", "Legacy subcategory", false)]
	[InlineData(" \t", "Legacy subcategory", false)]
	[InlineData(" \t\u00A0", null, true)]
	[InlineData("", "\u2003\u2028", true)]
	public async Task NoCurrentAssignment_RespectsLegacyLabels(string category, string? subcategory, bool eligible)
	{
		var seed = await SeedAsync();
		seed.Assignments.Should().OnlyContain(x => x == null);
		await using var db = fixture.CreateDbContext();
		Guid item = seed.Sources[0].ItemId;
		await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"Category\" = {category}, \"Subcategory\" = {subcategory} WHERE \"Id\" = {item}");
		Guid currentRevision = await db.ReceiptItems.Where(x => x.Id == item).Select(x => x.NormalizationRevision).SingleAsync();
		seed.Sources[0] = seed.Sources[0] with { ItemRevision = currentRevision };
		await using var tx = await db.Database.BeginTransactionAsync();
		var capture = await DeclareAsync(db, seed);
		if (eligible)
		{
			await SealAsync(db, capture);
			await tx.CommitAsync();
			(await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM taxonomy.\"OrganizationEffectCaptureWitnessesV1\"").SingleAsync()).Should().Be(1);
		}
		else
		{
			Func<Task> seal = () => SealAsync(db, capture);
			var failure = (await seal.Should().ThrowAsync<PostgresException>()).Which;
			failure.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
			failure.MessageText.Should().Contain("prior assignment");
			await tx.RollbackAsync();
			await AssertNoCaptureAsync();
		}
	}

	[Theory]
	[InlineData("mapping", "apply")]
	[InlineData("mapping", "undo")]
	[InlineData("taxonomy", "apply")]
	[InlineData("taxonomy", "undo")]
	[InlineData("assignment", "apply")]
	[InlineData("assignment", "undo")]
	[InlineData("historical-id", "apply")]
	[InlineData("historical-id", "undo")]
	[InlineData("historical-apply", "apply")]
	[InlineData("historical-apply", "undo")]
	[InlineData("historical-undo", "apply")]
	[InlineData("historical-undo", "undo")]
	public async Task Seal_RejectsPhaseIdentityAlreadyVisibleInOrdinaryHistory(string history, string phase)
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		Guid collision;
		if (history == "mapping")
		{
			collision = await db.CategoryMappingRevisions.Select(x => x.OperationId).SingleAsync();
		}
		else if (history == "taxonomy")
		{
			collision = await db.TaxonomyRevisions.Where(x => x.Id == seed.Revision).Select(x => x.OperationId).SingleAsync();
		}
		else if (history == "assignment")
		{
			collision = Guid.NewGuid();
			// A prior immutable assignment remains history even without being the current pointer.
			db.Classifications.Add(new()
			{
				Id = Guid.NewGuid(),
				ReceiptItemId = seed.Sources[0].ItemId,
				Sequence = 1,
				CategoryId = null,
				TaxonomyRevisionId = seed.Revision,
				RecordedAt = Captured,
				Actor = Actor,
				Source = "legacy_import",
				OperationId = collision
			});
			await db.SaveChangesAsync();
		}
		else
		{
			Guid operationId = Guid.NewGuid(), apply = Guid.NewGuid(), undo = Guid.NewGuid();
			db.HistoricalOperations.Add(new()
			{
				Id = operationId,
				Actor = Actor,
				RequestHash = "test-history",
				SelectionJson = "[]",
				TaxonomyRevisionId = seed.Revision,
				CapturedAt = Captured,
				Reason = "Existing historical operation",
				State = HistoricalOperationState.Preview
			});
			var source = seed.Sources[0];
			db.HistoricalOperationItems.Add(new()
			{
				OperationId = operationId,
				ItemId = source.ItemId,
				ReceiptId = source.ReceiptId,
				Store = source.Store,
				RawDescription = source.RawDescription,
				ItemCode = source.ItemCode,
				ItemRevision = source.ItemRevision,
				ReceiptRevision = source.ReceiptRevision,
				TargetCategoryId = seed.Category,
				ApplyOperationId = apply,
				UndoOperationId = undo
			});
			await db.SaveChangesAsync();
			collision = history switch { "historical-id" => operationId, "historical-apply" => apply, _ => undo };
		}
		await using var tx = await db.Database.BeginTransactionAsync();
		var capture = await DeclareAsync(db, seed, collision: collision, phase: phase);
		Func<Task> seal = () => SealAsync(db, capture);
		var failure = (await seal.Should().ThrowAsync<PostgresException>()).Which;
		failure.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
		failure.MessageText.Should().Contain("ordinary history");
		await tx.RollbackAsync();
		await AssertNoCaptureAsync();
	}
	private static async Task CaptureContenderAsync(ApplicationDbContext db, Seed seed)
	{
		await using var tx = await db.Database.BeginTransactionAsync();
		var capture = await DeclareAsync(db, seed);
		await SealAsync(db, capture);
		await tx.CommitAsync();
	}

	private async Task AssertNoCaptureAsync()
	{
		await using var db = fixture.CreateDbContext();
		(await db.OrganizationEffectManifests.CountAsync()).Should().Be(0);
		(await db.OrganizationEffectItems.CountAsync()).Should().Be(0);
		(await db.OrganizationEffectOperations.CountAsync()).Should().Be(0);
		(await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM taxonomy.\"OrganizationEffectCaptureWitnessesV1\"").SingleAsync()).Should().Be(0);
		(await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM taxonomy.\"MappingAuthorityEvents\"").SingleAsync()).Should().Be(0);
	}

	private static Task SealAsync(ApplicationDbContext db, Capture capture, Guid[]? ids = null, bool fence = false) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO taxonomy."OrganizationEffectCaptureWitnessesV1" ("ManifestId","EventId","OrderedItemIds","CanonicalRequest","RequestHash","CaptureFenceVersion")
        VALUES ({capture.Request.ManifestId},{capture.Event},{ids ?? capture.Request.Members.Select(x => x.Source.ItemId).ToArray()},{Encoding.UTF8.GetBytes("forged caller bytes")},'forged caller hash',{(int?)(fence ? 1 : null)})
        """);

	private static async Task<Capture> DeclareAsync(ApplicationDbContext db, Seed seed, bool typedManifest = true, bool typedEvent = true, bool mixedGroup = false, bool wrongCaptureId = false, Guid? collision = null, string phase = "apply")
	{
		Guid id = Guid.NewGuid(), eventId = Guid.NewGuid();
		var members = seed.Sources.Select((source, index) => new OrganizationEffectContractV1.ItemRequest(1, id, Actor, "v1", Captured,
			mixedGroup && index == 1 ? source with { RawDescription = "different" } : source,
			seed.Assignments[index], new(seed.Category, seed.Revision, [seed.Category]),
			index == 0 && phase == "apply" ? collision ?? Guid.NewGuid() : Guid.NewGuid(),
			index == 0 && phase == "undo" ? collision ?? Guid.NewGuid() : Guid.NewGuid(),
			new(1, "approved-exact-mapping", seed.Mapping, seed.Revision, seed.Fingerprint))).Reverse().ToArray();
		var request = new OrganizationEffectContractV1.ManifestRequest(1, id, Actor, "v1", seed.Revision, Captured, members);
		await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO taxonomy."OrganizationEffectManifests" ("Id","Actor","RequestHash","PolicyVersion","TaxonomyRevisionId","CapturedAt","CaptureWitnessVersion")
            VALUES ({id},{Actor},{OrganizationEffectContractV1.ManifestRequestHash(request)},'v1',{seed.Revision},{Captured},{(int?)(typedManifest ? 1 : null)})
            """);
		foreach (var member in members)
		{
			await InsertItemAsync(db, member);
		}

		await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO taxonomy."MappingAuthorityEvents" ("Id","BaselineId","Kind","MappingRevisionId","CaptureId","CaptureWitnessVersion")
            VALUES ({eventId},{seed.Baseline},2,{seed.Mapping},{(wrongCaptureId ? Guid.NewGuid() : id)},{(int?)(typedEvent ? 1 : null)})
            """);
		return new(eventId, request);
	}

	private static Task InsertItemAsync(ApplicationDbContext db, OrganizationEffectContractV1.ItemRequest m) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO taxonomy."OrganizationEffectItems" ("ManifestId","ItemId","ReceiptId","Store","RawDescription","ItemCode","ItemRevision","ReceiptRevision","ExpectedAssignmentId","TaxonomyRevisionId","TargetCategoryId","ApplyOperationId","UndoOperationId","DecisionEvidenceJson")
        VALUES ({m.ManifestId},{m.Source.ItemId},{m.Source.ReceiptId},{m.Source.Store},{m.Source.RawDescription},{m.Source.ItemCode},{m.Source.ItemRevision},{m.Source.ReceiptRevision},{m.ExpectedAssignmentId},{m.Target.TaxonomyRevisionId},{m.Target.CategoryId},{m.ApplyOperationId},{m.UndoOperationId},CAST({JsonSerializer.Serialize(m.Decision)} AS jsonb))
        """);

	private async Task<Seed> SeedAsync(bool createBaseline = true)
	{
		await using var db = fixture.CreateDbContext();
		db.Users.Add(new ApplicationUser { Id = Actor, UserName = Actor });
		var role = new IdentityRole(AppRoles.Admin);
		db.Roles.Add(role);
		db.UserRoles.Add(new IdentityUserRole<string> { UserId = Actor, RoleId = role.Id });
		await db.SaveChangesAsync();
		Guid head = await db.TaxonomyHeads.Select(x => x.RevisionId).SingleAsync(), category = Guid.NewGuid();
		TaxonomyService taxonomy = new(new Factory(fixture), TimeProvider.System);
		var revision = await taxonomy.ChangeAsync(Actor, new(head, Guid.NewGuid(), "Capture test", new[] { new TaxonomyEdit("create", category, Name: "Test") }));
		var source = new NormalizationSourceKey("store \"<é😀", "product\t\\ &+`", null);
		string fingerprint = NormalizationCatalogPolicy.Hash(source);
		CategoryMappingService mappings = new(new Factory(fixture), TimeProvider.System);
		var mapping = await mappings.ChangeAsync(Actor, new(source, fingerprint, null, revision.Id, category, Guid.NewGuid(), "Approve"));
		Guid baseline = Guid.NewGuid();
		if (createBaseline) { await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO taxonomy.\"MappingAuthorityBaseline\" (\"Singleton\",\"Id\",\"StreamId\") VALUES (1,{baseline},{Guid.NewGuid()})"); }
		var receipt = SampleData.Entities.ReceiptEntityGenerator.Generate();
		receipt.Location = source.Location;
		db.Add(receipt);
		var items = Enumerable.Range(0, 2).Select(_ => SampleData.Entities.ReceiptItemEntityGenerator.Generate(receipt.Id)).ToArray();
		foreach (var item in items) { item.Description = source.Description; item.ReceiptItemCode = source.ItemCode; item.Category = ""; item.Subcategory = null; db.Add(item); }
		await db.SaveChangesAsync();
		db.ChangeTracker.Clear();
		receipt = await db.Set<Infrastructure.Entities.Core.ReceiptEntity>().SingleAsync();
		items = await db.Set<Infrastructure.Entities.Core.ReceiptItemEntity>().OrderBy(x => x.Id).ToArrayAsync();
		var assignments = new Guid?[items.Length];
		for (int i = 0; i < items.Length; i++)
		{
			assignments[i] = await db.Set<CurrentClassificationEntity>().Where(x => x.ReceiptItemId == items[i].Id).Select(x => (Guid?)x.AssignmentId).SingleOrDefaultAsync();
		}

		return new(baseline, mapping.Id, revision.Id, category, items.Select(x => new OrganizationEffectContractV1.Source(receipt.Id, x.Id, receipt.Location, x.Description, x.ReceiptItemCode, receipt.NormalizationRevision, x.NormalizationRevision)).ToArray(), assignments, fingerprint);
	}
}




