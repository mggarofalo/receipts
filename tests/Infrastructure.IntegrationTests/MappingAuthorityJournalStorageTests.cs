using System.Data;
using System.Diagnostics;
using Application.Normalization;
using Application.Taxonomy;
using Common;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class MappingAuthorityJournalStorageTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}

	[Fact]
	public async Task SnapshotAndAppend_RetainEvidenceRejectForgeryAndReuseRolledBackOrdinal()
	{
		var (baseline, mapping) = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		(await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM taxonomy.\"MappingAuthorityBaselineHeads\"").SingleAsync()).Should().Be(1);
		foreach (string sql in new[]
		{
			"DELETE FROM taxonomy.\"MappingAuthorityBaselineHeads\"",
			"UPDATE taxonomy.\"MappingAuthorityBaseline\" SET \"Version\" = 1",
			"TRUNCATE taxonomy.\"MappingAuthorityEvents\"",
			$"INSERT INTO taxonomy.\"MappingAuthorityBaselineHeads\" VALUES ('{mapping}', '{baseline}')",
			$"INSERT INTO taxonomy.\"MappingAuthorityEvents\" (\"Id\",\"BaselineId\",\"Kind\",\"MappingRevisionId\",\"Sequence\") VALUES (gen_random_uuid(),'{baseline}',1,'{mapping}',2)",
			$"INSERT INTO taxonomy.\"MappingAuthorityEvents\" (\"Id\",\"BaselineId\",\"Kind\",\"MappingRevisionId\") VALUES (gen_random_uuid(),'{baseline}',1,'{mapping}')"
		})
		{
			Func<Task> invalid = () => db.Database.ExecuteSqlRawAsync(sql);
			await invalid.Should().ThrowAsync<PostgresException>();
		}
		await using (var transaction = await db.Database.BeginTransactionAsync())
		{
			await AppendAsync(db, baseline, mapping);
			await transaction.RollbackAsync();
		}
		await AppendAsync(db, baseline, mapping);
		Func<Task> fork = () => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO taxonomy."MappingAuthorityEvents" ("Id","BaselineId","Kind","MappingRevisionId","CaptureId","PreviousEventId")
            VALUES ({Guid.NewGuid()},{baseline},2,{mapping},{Guid.NewGuid()},{Guid.NewGuid()})
            """);
		await fork.Should().ThrowAsync<PostgresException>();
		(await TipAsync(db)).Should().Be(1);
		Func<Task> mutate = () => db.Database.ExecuteSqlRawAsync("DELETE FROM taxonomy.\"MappingAuthorityEvents\"");
		await mutate.Should().ThrowAsync<PostgresException>();
	}

	[Fact]
	public async Task ConcurrentCaptures_SerializeBehindTaxonomyHeadAndAllocateContiguousPositions()
	{
		var (baseline, mapping) = await SeedAsync();
		await using var first = fixture.CreateDbContext();
		await using var second = fixture.CreateDbContext();
		await using var transaction = await first.Database.BeginTransactionAsync();
		await first.Database.ExecuteSqlRawAsync("SELECT 1 FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE");
		await AppendAsync(first, baseline, mapping);
		await second.Database.OpenConnectionAsync();
		int blockedPid = await second.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
		int blockerPid = await first.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
		Task contender = AppendAsync(second, baseline, mapping);
		bool observedWaiting = await ObserveBlockedAsync(blockedPid, blockerPid, contender);
		await transaction.CommitAsync();
		await contender.WaitAsync(TimeSpan.FromSeconds(20));
		observedWaiting.Should().BeTrue("the contender must actually wait on the winner before it commits");
		(await TipAsync(second)).Should().Be(2);
		(await second.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM taxonomy.\"MappingAuthorityEvents\" e JOIN taxonomy.\"MappingAuthorityEvents\" p ON e.\"PreviousEventId\" = p.\"Id\" WHERE e.\"Sequence\" = 2 AND p.\"Sequence\" = 1").SingleAsync()).Should().Be(1);
	}

	[Fact]
	public async Task UnjournaledHeadChange_BlocksCaptureOfBothOldAndNewHead()
	{
		var (baseline, mapping) = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		var previous = await db.CategoryMappingRevisions.SingleAsync();
		CategoryMappingService service = new(new Factory(fixture), TimeProvider.System);
		var revoked = await service.ChangeAsync(previous.Actor, new(new(previous.Location, previous.Description, previous.ItemCode),
			previous.SourceFingerprint, mapping, previous.TaxonomyRevisionId, null, Guid.NewGuid(), "Revoke"));
		foreach (Guid id in new[] { mapping, revoked.Id })
		{
			Func<Task> capture = () => AppendAsync(db, baseline, id);
			await capture.Should().ThrowAsync<PostgresException>();
		}
		await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO taxonomy."MappingAuthorityEvents" ("Id","BaselineId","Kind","MappingRevisionId")
            VALUES ({Guid.NewGuid()},{baseline},1,{revoked.Id})
            """);
		(await TipAsync(db)).Should().Be(1);
	}


	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	public async Task Transition_RejectsWitnessVersionButAllowsUntypedTransition(int witnessVersion)
	{
		var (baseline, mapping) = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		var previous = await db.CategoryMappingRevisions.SingleAsync();
		CategoryMappingService service = new(new Factory(fixture), TimeProvider.System);
		var revoked = await service.ChangeAsync(previous.Actor, new(new(previous.Location, previous.Description, previous.ItemCode),
			previous.SourceFingerprint, mapping, previous.TaxonomyRevisionId, null, Guid.NewGuid(), "Revoke"));
		Func<Task> typedTransition = () => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO taxonomy."MappingAuthorityEvents" ("Id","BaselineId","Kind","MappingRevisionId","CaptureWitnessVersion")
            VALUES ({Guid.NewGuid()},{baseline},1,{revoked.Id},{witnessVersion})
            """);
		(await typedTransition.Should().ThrowAsync<PostgresException>()).Which.SqlState
			.Should().Be(PostgresErrorCodes.CheckViolation);
		(await TipAsync(db)).Should().Be(0);
		await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO taxonomy."MappingAuthorityEvents" ("Id","BaselineId","Kind","MappingRevisionId","CaptureWitnessVersion")
            VALUES ({Guid.NewGuid()},{baseline},1,{revoked.Id},NULL)
            """);
		(await TipAsync(db)).Should().Be(1);
	}

	[Theory]
	[InlineData(IsolationLevel.RepeatableRead)]
	[InlineData(IsolationLevel.Serializable)]
	public async Task StaleSnapshot_CannotSealBaselineAfterCommittedRevocation(IsolationLevel isolation)
	{
		var (baseline, mapping) = await SeedAsync(createBaseline: false);
		await using var stale = fixture.CreateDbContext();
		await using var transaction = await stale.Database.BeginTransactionAsync(isolation);
		(await stale.CategoryMappingHeads.AsNoTracking().SingleAsync()).RevisionId.Should().Be(mapping);
		Guid revoked = await RevokeAsync(); // A separate connection commits after this snapshot was established.
		Func<Task> seal = () => stale.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO taxonomy."MappingAuthorityBaseline" ("Singleton","Id","StreamId") VALUES (1,{baseline},{Guid.NewGuid()})
            """);
		var error = (await seal.Should().ThrowAsync<PostgresException>()).Which;
		error.SqlState.Should().Be("25000");
		error.MessageText.Should().Contain("READ COMMITTED");
		await transaction.RollbackAsync();
		await using var verify = fixture.CreateDbContext();
		(await verify.CategoryMappingHeads.AsNoTracking().SingleAsync()).RevisionId.Should().Be(revoked);
		(await verify.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM taxonomy.\"MappingAuthorityBaseline\"").SingleAsync()).Should().Be(0);
	}

	[Theory]
	[InlineData(IsolationLevel.RepeatableRead)]
	[InlineData(IsolationLevel.Serializable)]
	public async Task StaleSnapshot_CannotCaptureApprovalAfterCommittedRevocation(IsolationLevel isolation)
	{
		var (baseline, mapping) = await SeedAsync();
		await using var stale = fixture.CreateDbContext();
		await using var transaction = await stale.Database.BeginTransactionAsync(isolation);
		(await stale.CategoryMappingHeads.AsNoTracking().SingleAsync()).RevisionId.Should().Be(mapping);
		Guid revoked = await RevokeAsync();
		Func<Task> capture = () => AppendAsync(stale, baseline, mapping);
		var error = (await capture.Should().ThrowAsync<PostgresException>()).Which;
		error.SqlState.Should().Be("25000");
		error.MessageText.Should().Contain("READ COMMITTED");
		await transaction.RollbackAsync();
		await using var verify = fixture.CreateDbContext();
		(await verify.CategoryMappingHeads.AsNoTracking().SingleAsync()).RevisionId.Should().Be(revoked);
		(await TipAsync(verify)).Should().Be(0);
	}

	private async Task<Guid> RevokeAsync()
	{
		await using var db = fixture.CreateDbContext();
		var previous = await db.CategoryMappingRevisions.SingleAsync();
		CategoryMappingService service = new(new Factory(fixture), TimeProvider.System);
		return (await service.ChangeAsync(previous.Actor, new(new(previous.Location, previous.Description, previous.ItemCode),
			previous.SourceFingerprint, previous.Id, previous.TaxonomyRevisionId, null, Guid.NewGuid(), "Revoke"))).Id;
	}

	private async Task<bool> ObserveBlockedAsync(int blockedPid, int blockerPid, Task contender)
	{
		await using var observer = fixture.CreateDbContext();
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(10) && !contender.IsCompleted)
		{
			if (await observer.Database.SqlQuery<bool>($"SELECT {blockerPid} = ANY(pg_blocking_pids({blockedPid})) AS \"Value\"").SingleAsync())
			{
				return true;
			}
			await Task.Delay(25);
		}
		return false;
	}

	private static Task AppendAsync(ApplicationDbContext db, Guid baseline, Guid mapping) =>
		db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO taxonomy."MappingAuthorityEvents" ("Id","BaselineId","Kind","MappingRevisionId","CaptureId")
            VALUES ({Guid.NewGuid()},{baseline},2,{mapping},{Guid.NewGuid()})
            """);

	private static Task<long> TipAsync(ApplicationDbContext db) => db.Database.SqlQueryRaw<long>(
		"SELECT \"Sequence\" AS \"Value\" FROM taxonomy.\"MappingAuthorityTip\"").SingleAsync();

	private async Task<(Guid Baseline, Guid Mapping)> SeedAsync(bool createBaseline = true)
	{
		const string actor = "journal-admin";
		await using var db = fixture.CreateDbContext();
		db.Users.Add(new ApplicationUser { Id = actor, UserName = actor });
		var role = new IdentityRole(AppRoles.Admin);
		db.Roles.Add(role);
		db.UserRoles.Add(new IdentityUserRole<string> { UserId = actor, RoleId = role.Id });
		await db.SaveChangesAsync();
		Guid head = await db.TaxonomyHeads.Select(x => x.RevisionId).SingleAsync();
		Guid category = Guid.NewGuid();
		TaxonomyService taxonomy = new(new Factory(fixture), TimeProvider.System);
		var revision = await taxonomy.ChangeAsync(actor, new(head, Guid.NewGuid(), "Journal test category",
			new[] { new TaxonomyEdit("create", category, Name: "Journal category") }));
		NormalizationSourceKey source = new("store", "product", null);
		CategoryMappingService mappings = new(new Factory(fixture), TimeProvider.System);
		var mapping = await mappings.ChangeAsync(actor, new(source, NormalizationCatalogPolicy.Hash(source), null,
			revision.Id, category, Guid.NewGuid(), "Approve"));
		Guid baseline = Guid.NewGuid();
		if (createBaseline)
		{
			await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO taxonomy."MappingAuthorityBaseline" ("Singleton","Id","StreamId") VALUES (1,{baseline},{Guid.NewGuid()})
            """);
		}
		return (baseline, mapping.Id);
	}
}
