using System.Diagnostics;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Npgsql;

namespace Infrastructure.IntegrationTests;

// Inactive candidate: only session-local LIKE clones receive triggers.
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class EffectOperationIdOrdinaryTableCandidateTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Fact]
	public async Task TableShapes_PreserveCrossActorAndDomainReuse_AndRetainChangedAndDeletedIds()
	{
		await using var connection = await Open();
		Guid id = Guid.NewGuid(), changed = Guid.NewGuid();
		await Ordinary(connection, id, "one", false);
		await Ordinary(connection, id, "one", true);
		await Ordinary(connection, id, "two", true);
		await using (var tx = await connection.BeginTransactionAsync())
		{
			await Enter(connection);
			await Execute(connection, "UPDATE pg_temp.review_operations SET \"OperationId\"=@id WHERE \"Actor\"='one'", ("id", changed));
			await Execute(connection, "DELETE FROM pg_temp.disposition_operations");
			await tx.CommitAsync();
		}
		(await Class(connection, id)).Should().Be("ordinary");
		(await Class(connection, changed)).Should().Be("ordinary");
		(await Rows(connection)).Should().Be(2);
		Func<Task> mutate = () => Execute(connection, "DELETE FROM taxonomy.\"EffectOperationIdRegistry\" WHERE \"OperationId\"=@id", ("id", id));
		await mutate.Should().ThrowAsync<PostgresException>();
	}

	[Fact]
	public async Task Entry_IsRequiredForEveryTransaction_AndRollbackReleasesBusinessRowAndClaim()
	{
		await using var connection = await Open();
		Guid id = Guid.NewGuid();
		await Reject(() => Write(connection, id, "one", true), "55000");
		await using (var tx = await connection.BeginTransactionAsync())
		{
			await Enter(connection);
			await Write(connection, id, "one", true);
			await tx.RollbackAsync();
		}
		(await Class(connection, id)).Should().BeNull();
		(await Rows(connection)).Should().Be(0);
		await Reject(() => Write(connection, id, "one", false), "55000");
		// A committed entry also cannot authorize a later transaction.
		await using (var tx = await connection.BeginTransactionAsync())
		{
			await Enter(connection);
			await tx.CommitAsync();
		}
		await Reject(() => Write(connection, id, "one", true), "55000");
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task ReservedAndBlocked_RejectInsertAndUpdate_WithoutSessionOwnerBypass(bool review, bool legacyBlocked)
	{
		await using var connection = await Open();
		Guid apply = Guid.NewGuid(), undo = Guid.NewGuid(), ordinary = Guid.NewGuid(), blocked = Guid.NewGuid();
		await Effect(connection, apply, undo);
		await Execute(connection, """
			INSERT INTO taxonomy."EffectOperationIdRegistry" ("OperationId","Class","ManifestId","ItemId","Phase")
			VALUES (@id,'legacy_blocked',@manifest,@item,'apply')
			""", ("id", blocked), ("manifest", Guid.NewGuid()), ("item", Guid.NewGuid()));
		await Execute(connection, "SET receipts.effect_execution = 'true'");
		await Reject(() => Ordinary(connection, apply, "one", review), "23505");
		await Reject(() => Ordinary(connection, blocked, "one", review), "23505");
		await Ordinary(connection, ordinary, "one", review);
		await using (var tx = await connection.BeginTransactionAsync())
		{
			await Enter(connection);
			string table = review ? "review_operations" : "disposition_operations";
			await Reject(() => Execute(connection, $"UPDATE pg_temp.{table} SET \"OperationId\"=@id", ("id", legacyBlocked ? blocked : undo)), "23505");
			await tx.RollbackAsync();
		}
		(await Rows(connection)).Should().Be(1);
		(await Class(connection, ordinary)).Should().Be("ordinary");
		(await Class(connection, blocked)).Should().Be("legacy_blocked");
	}

	[Theory]
	[InlineData(false, true, true)]
	[InlineData(false, false, true)]
	[InlineData(false, true, false)]
	[InlineData(false, false, false)]
	[InlineData(true, true, true)]
	[InlineData(true, false, true)]
	[InlineData(true, true, false)]
	[InlineData(true, false, false)]
	public async Task EntryGate_ArbitratesBothOrders_AndCommitOrRollback(bool review, bool ordinaryFirst, bool commit)
	{
		await using var winner = await Open();
		await using var contender = await Open();
		await using var observer = await Open();
		Guid id = Guid.NewGuid(), other = Guid.NewGuid();
		await using var tx = await winner.BeginTransactionAsync();
		await Enter(winner); // Before any domain lock or registry reservation.
		if (ordinaryFirst)
		{
			await Write(winner, id, "one", review);
		}
		else
		{
			await ClaimEffect(winner, id, other);
		}

		Task pending = ordinaryFirst ? Effect(contender, id, other) : Ordinary(contender, id, "two", review);
		bool blocked = await WaitForBlocked(observer, contender.ProcessID, pending);
		if (commit)
		{
			await tx.CommitAsync();
		}
		else
		{
			await tx.RollbackAsync();
		}

		if (commit)
		{
			await Reject(() => pending.WaitAsync(TimeSpan.FromSeconds(15)), "23505");
		}
		else
		{
			await pending.WaitAsync(TimeSpan.FromSeconds(15));
		}

		blocked.Should().BeTrue("PostgreSQL must report a real blocker");
		(await Class(observer, id)).Should().Be(ordinaryFirst == commit ? "ordinary" : "effect");
		(await Class(observer, other)).Should().Be(ordinaryFirst == commit ? null : "effect");
		(await Rows(ordinaryFirst ? winner : contender)).Should().Be(ordinaryFirst == commit ? 1 : 0);
	}

	[Fact]
	public async Task ConcurrentOrdinaryClaims_AllowDifferentActorAndDomainAfterGateWait()
	{
		await using var first = await Open();
		await using var second = await Open();
		await using var observer = await Open();
		Guid id = Guid.NewGuid();
		await using var tx = await first.BeginTransactionAsync();
		await Enter(first);
		await Write(first, id, "one", false);
		Task pending = Ordinary(second, id, "two", true);
		bool blocked = await WaitForBlocked(observer, second.ProcessID, pending);
		await tx.CommitAsync();
		await pending.WaitAsync(TimeSpan.FromSeconds(15));
		blocked.Should().BeTrue();
		(await Rows(first)).Should().Be(1);
		(await Rows(second)).Should().Be(1);
		(await Class(observer, id)).Should().Be("ordinary");
	}

	[Fact]
	public async Task MultirowStatement_RejectionRollsBackEarlierRowAndClaim()
	{
		await using var connection = await Open();
		Guid fresh = Guid.NewGuid(), apply = Guid.NewGuid(), undo = Guid.NewGuid();
		await Effect(connection, apply, undo);
		await using (var tx = await connection.BeginTransactionAsync())
		{
			await Enter(connection);
			await Execute(connection, "SAVEPOINT before_statement");
			await Reject(() => Execute(connection, """
				INSERT INTO pg_temp.review_operations ("Actor","OperationId","AttemptId","RequestHash","ResultJson","RecordedAt")
				SELECT 'one',id,id,'hash','{}',CURRENT_TIMESTAMP
				FROM (VALUES (1,@fresh),(2,@apply)) rows(position,id) ORDER BY position
				""", ("fresh", fresh), ("apply", apply)), "23505");
			await Execute(connection, "ROLLBACK TO SAVEPOINT before_statement");
			// Check statement rollback before ending the surrounding transaction.
			(await Class(connection, fresh)).Should().BeNull();
			(await Rows(connection)).Should().Be(0);
			await tx.CommitAsync();
		}
	}

	private async Task<NpgsqlConnection> Open()
	{
		var connection = new NpgsqlConnection(fixture.ConnectionString);
		await connection.OpenAsync();
		try
		{
			await Execute(connection, """
				CREATE TEMP TABLE disposition_operations (LIKE taxonomy."HistoricalDispositionOperations" INCLUDING ALL);
				CREATE TEMP TABLE review_operations (LIKE category_assistance."ReviewOperations" INCLUDING ALL);
				-- LIKE does not copy foreign keys or triggers. These are column/index candidates,
				-- not tests of domain persistence, assignment authority or portable restore.
				CREATE TEMP TABLE ordinary_gate_entry (transaction_id xid8 PRIMARY KEY) ON COMMIT DELETE ROWS;
				CREATE FUNCTION pg_temp.enter_ordinary_candidate() RETURNS void LANGUAGE plpgsql AS $$
				BEGIN
				    -- Prototype-only global serialization. Every participating transaction enters
				    -- before RCPT/NORM, actor/source locks, taxonomy Head and reservations.
				    PERFORM pg_advisory_xact_lock(1078, 831);
				    INSERT INTO pg_temp.ordinary_gate_entry VALUES (pg_current_xact_id()) ON CONFLICT DO NOTHING;
				END $$;
				CREATE FUNCTION pg_temp.guard_ordinary_candidate() RETURNS trigger LANGUAGE plpgsql AS $$
				BEGIN
				    IF NOT EXISTS (SELECT 1 FROM pg_temp.ordinary_gate_entry WHERE transaction_id=pg_current_xact_id()) THEN
				        RAISE EXCEPTION 'Enter the candidate transaction gate before domain locks' USING ERRCODE='55000';
				    END IF;
				    PERFORM taxonomy.claim_ordinary_operation_id(NEW."OperationId");
				    RETURN NEW;
				END $$;
				CREATE TRIGGER ordinary_candidate BEFORE INSERT OR UPDATE ON pg_temp.disposition_operations
				    FOR EACH ROW EXECUTE FUNCTION pg_temp.guard_ordinary_candidate();
				CREATE TRIGGER ordinary_candidate BEFORE INSERT OR UPDATE ON pg_temp.review_operations
				    FOR EACH ROW EXECUTE FUNCTION pg_temp.guard_ordinary_candidate();
				""");
			return connection;
		}
		catch { await connection.DisposeAsync(); throw; }
	}

	private static Task Enter(NpgsqlConnection connection) => Execute(connection, "SELECT pg_temp.enter_ordinary_candidate()");
	private static async Task Ordinary(NpgsqlConnection connection, Guid id, string actor, bool review)
	{
		await using var tx = await connection.BeginTransactionAsync();
		await Enter(connection);
		await Write(connection, id, actor, review);
		await tx.CommitAsync();
	}
	private static Task Write(NpgsqlConnection connection, Guid id, string actor, bool review) => Execute(connection,
		review ? """
			INSERT INTO pg_temp.review_operations ("Actor","OperationId","AttemptId","RequestHash","ResultJson","RecordedAt")
			VALUES (@actor,@id,@id,'hash','{}',CURRENT_TIMESTAMP)
			""" : """
			INSERT INTO pg_temp.disposition_operations ("OperationId","Actor","SourceHash","Store","RawDescription","ExpectedVersion","ResultVersion","ResultUpdatedAt")
			VALUES (@id,@actor,'hash','store','description',0,1,CURRENT_TIMESTAMP)
			""", ("id", id), ("actor", actor));
	private static async Task Effect(NpgsqlConnection connection, Guid apply, Guid undo)
	{
		await using var tx = await connection.BeginTransactionAsync();
		await Enter(connection);
		await ClaimEffect(connection, apply, undo);
		await tx.CommitAsync();
	}
	private static Task ClaimEffect(NpgsqlConnection connection, Guid apply, Guid undo) => Execute(connection,
		"SELECT taxonomy.claim_effect_operation_ids(@manifest,@item,@apply,@undo)",
		("manifest", Guid.NewGuid()), ("item", Guid.NewGuid()), ("apply", apply), ("undo", undo));
	private static async Task Execute(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
	{
		await using var command = new NpgsqlCommand(sql, connection);
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value);
		}

		await command.ExecuteNonQueryAsync();
	}
	private static async Task<string?> Class(NpgsqlConnection connection, Guid id)
	{
		await using var command = new NpgsqlCommand("SELECT \"Class\" FROM taxonomy.\"EffectOperationIdRegistry\" WHERE \"OperationId\"=@id", connection);
		command.Parameters.AddWithValue("id", id);
		return (string?)await command.ExecuteScalarAsync();
	}
	private static async Task<long> Rows(NpgsqlConnection connection)
	{
		await using var command = new NpgsqlCommand("SELECT (SELECT count(*) FROM pg_temp.review_operations) + (SELECT count(*) FROM pg_temp.disposition_operations)", connection);
		return (long)(await command.ExecuteScalarAsync())!;
	}
	private static async Task Reject(Func<Task> action, string state) =>
		(await action.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(state);
	private static async Task<bool> WaitForBlocked(NpgsqlConnection observer, int pid, Task pending)
	{
		var timer = Stopwatch.StartNew();
		while (timer.Elapsed < TimeSpan.FromSeconds(10) && !pending.IsCompleted)
		{
			await using var command = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid)) > 0", observer);
			command.Parameters.AddWithValue("pid", pid);
			if ((bool)(await command.ExecuteScalarAsync())!)
			{
				return true;
			}

			await Task.Delay(25);
		}
		return false;
	}
}
