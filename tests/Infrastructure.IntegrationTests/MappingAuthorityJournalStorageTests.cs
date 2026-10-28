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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class MappingAuthorityJournalStorageTests : IsolatedPostgresTest
{
	private readonly PostgresFixture server;

	public MappingAuthorityJournalStorageTests(PostgresFixture server) : base(server) => this.server = server;

	[Fact]
	public async Task MultiRowAppend_RejectsWholeStatementThenAllowsSeparateStatementsInOneTransaction()
	{
		var (baseline, mapping) = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		Func<Task> batch = () => db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO taxonomy."MappingAuthorityEvents" ("Id","BaselineId","Kind","MappingRevisionId","CaptureId")
                    VALUES ({Guid.NewGuid()},{baseline},2,{mapping},{Guid.NewGuid()}),
                           ({Guid.NewGuid()},{baseline},2,{mapping},{Guid.NewGuid()})
                    """);
		(await batch.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
		(await TipAsync(db)).Should().Be(0);
		(await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM taxonomy.\"MappingAuthorityEvents\"").SingleAsync()).Should().Be(0);
		await using var transaction = await db.Database.BeginTransactionAsync();
		await AppendAsync(db, baseline, mapping);
		await AppendAsync(db, baseline, mapping);
		await transaction.CommitAsync();
		(await TipAsync(db)).Should().Be(2);
	}

	[Fact]
	public async Task EmptyBaseline_RollbackRetainsNeitherCommitmentNorCheckpoint()
	{
		await using var db = fixture.CreateDbContext();
		Guid baseline = Guid.NewGuid(), stream = Guid.NewGuid();
		FormattableString insert = $"""
                    INSERT INTO taxonomy."MappingAuthorityBaseline" ("Singleton","Id","StreamId") VALUES (1,{baseline},{stream})
                    """;
		await using (var transaction = await db.Database.BeginTransactionAsync())
		{
			await db.Database.ExecuteSqlInterpolatedAsync(insert);
			(await TipAsync(db)).Should().Be(0);
			await transaction.RollbackAsync();
		}
		(await db.Database.SqlQueryRaw<long>("""
                    SELECT (SELECT count(*) FROM taxonomy."MappingAuthorityBaseline")
                        + (SELECT count(*) FROM taxonomy."MappingAuthorityBaselineAnchorsV1")
                        + (SELECT count(*) FROM taxonomy."MappingAuthorityCheckpointsV1") AS "Value"
                    """).SingleAsync()).Should().Be(0);
		await db.Database.ExecuteSqlInterpolatedAsync(insert);
		string hash = await db.Database.SqlQueryRaw<string>("SELECT \"BaselineHash\" AS \"Value\" FROM taxonomy.\"MappingAuthorityBaselineAnchorsV1\"").SingleAsync();
		hash.Should().Be(MappingAuthorityJournalPolicyV1.BaselineHash(new(1, stream, baseline, [])));
		(await TipAsync(db)).Should().Be(0);
	}

	[Fact]
	public async Task BaselineAnchor_MatchesDotNetOrdinalHashAndRejectsOrdinaryMutation()
	{
		var (baseline, _) = await SeedAsync(createBaseline: false);
		await using var db = fixture.CreateDbContext();
		var previous = await db.CategoryMappingRevisions.SingleAsync();
		CategoryMappingService service = new(new Factory(fixture), TimeProvider.System);
		// UTF-16 ordinal order puts the supplementary scalar before U+E000; C collation reverses it.
		foreach (string location in new[] { "\U00010000", "\uE000", "\"&'+<>`\\\t\n\u007Fé", "ascii" })
		{
			NormalizationSourceKey source = new(location, "anchor", "code");
			await service.ChangeAsync(previous.Actor, new(source, NormalizationCatalogPolicy.Hash(source), null,
					previous.TaxonomyRevisionId, previous.CategoryId, Guid.NewGuid(), "Anchor parity"));
		}
		Guid stream = Guid.NewGuid();
		await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO taxonomy."MappingAuthorityBaseline" ("Singleton","Id","StreamId") VALUES (1,{baseline},{stream})
                    """);
		var revisions = await db.CategoryMappingRevisions.AsNoTracking().ToListAsync();
		MappingAuthorityBaseline value = new(1, stream, baseline, revisions.Select(r =>
				new MappingAuthorityHead(new(r.Location, r.Description, r.ItemCode), r.Id)).ToArray());
		string actual = await db.Database.SqlQueryRaw<string>(
				"SELECT \"BaselineHash\" AS \"Value\" FROM taxonomy.\"MappingAuthorityBaselineAnchorsV1\"").SingleAsync();
		actual.Should().Be(MappingAuthorityJournalPolicyV1.BaselineHash(value));
		foreach (string sql in new[]
		{
						"INSERT INTO taxonomy.\"MappingAuthorityCheckpointsV1\" SELECT * FROM taxonomy.\"MappingAuthorityCheckpointsV1\"",
						"INSERT INTO taxonomy.\"MappingAuthorityBaselineAnchorsV1\" SELECT * FROM taxonomy.\"MappingAuthorityBaselineAnchorsV1\""
				})
		{
			Func<Task> insert = () => db.Database.ExecuteSqlRawAsync(sql);
			PostgresException error = (await insert.Should().ThrowAsync<PostgresException>()).Which;
			error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
			error.MessageText.Should().Contain("maintained only");
		}
		foreach (string sql in new[]
		{
						"UPDATE taxonomy.\"MappingAuthorityCheckpointsV1\" SET \"Sequence\" = 0",
						"DELETE FROM taxonomy.\"MappingAuthorityCheckpointsV1\"",
						"TRUNCATE taxonomy.\"MappingAuthorityCheckpointsV1\"",
						"UPDATE taxonomy.\"MappingAuthorityBaselineAnchorsV1\" SET \"Version\" = 1",
						"DELETE FROM taxonomy.\"MappingAuthorityBaselineAnchorsV1\"",
						"TRUNCATE taxonomy.\"MappingAuthorityBaselineAnchorsV1\" CASCADE"
				})
		{
			Func<Task> mutate = () => db.Database.ExecuteSqlRawAsync(sql);
			await mutate.Should().ThrowAsync<PostgresException>();
		}
	}

	[Fact]
	public async Task PrivilegedSuffixLoss_DoesNotRewindCheckpointAndBlocksAppend()
	{
		var (baseline, mapping) = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		await AppendAsync(db, baseline, mapping);
		// Simulate damage requiring privileges beyond ordinary DML; anchors remain independent.
		await db.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE taxonomy."MappingAuthorityEvents" DISABLE TRIGGER immutable_authority_events;
                    DELETE FROM taxonomy."MappingAuthorityEvents";
                    ALTER TABLE taxonomy."MappingAuthorityEvents" ENABLE TRIGGER immutable_authority_events;
                    """);
		(await TipAsync(db)).Should().Be(1);
		Func<Task> append = () => AppendAsync(db, baseline, mapping);
		(await append.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("retained checkpoint");
	}

	[Fact]
	public async Task Upgrade_RetainsLegacyEvidenceWithoutManufacturingAnchor()
	{
		await fixture.DisposeAsync();
		fixture = await server.CreateIsolatedAsync("20261028190000_AddOrganizationEffectCaptureFencesV1");
		var (baseline, mapping) = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		await AppendAsync(db, baseline, mapping);
		await db.GetService<IMigrator>().MigrateAsync();
		(await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM taxonomy.\"MappingAuthorityEvents\"").SingleAsync()).Should().Be(1);
		(await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM taxonomy.\"MappingAuthorityBaselineHeads\"").SingleAsync()).Should().Be(1);
		(await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM taxonomy.\"MappingAuthorityBaselineAnchorsV1\"").SingleAsync()).Should().Be(0);
		Func<Task> append = () => AppendAsync(db, baseline, mapping);
		(await append.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("legacy");
	}

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
