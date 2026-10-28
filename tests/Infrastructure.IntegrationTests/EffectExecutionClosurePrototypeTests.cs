using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Npgsql;

namespace Infrastructure.IntegrationTests;

// Structural experiment only: every input is a synthetic, session-local assumption.
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed partial class EffectExecutionClosurePrototypeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Fact]
	public async Task CompleteApplyAndUndo_UseDistinctAutomaticHistory_AndUndoSurvivesDisable()
	{
		await using var db = await Open();
		await Execute(db, Apply);
		(await Value(db, "SELECT source FROM pg_temp.assignments WHERE id=2")).Should().Be("automatic");
		await Execute(db, "UPDATE pg_temp.live SET enabled=false, provider=NULL, settings=gen_random_uuid(), mapping=gen_random_uuid()");
		await Execute(db, Undo);
		(await Value(db, "SELECT category::text FROM pg_temp.assignments WHERE id=3")).Should().Be("10");
		(await Value(db, "SELECT count(*)::text FROM pg_temp.outcomes")).Should().Be("2");
	}

	[Theory]
	[InlineData("actor", "SELECT pg_temp.run_effect('apply','intruder','raw','phase-apply')")]
	[InlineData("source", "SELECT pg_temp.run_effect('apply','owner','changed','phase-apply')")]
	[InlineData("phase", "SELECT pg_temp.run_effect('unknown','owner','raw','phase-apply')")]
	[InlineData("hash", "SELECT pg_temp.run_effect('apply','owner','raw','wrong')")]
	[InlineData("reservation owner", "UPDATE pg_temp.reservations SET manifest=gen_random_uuid();")]
	[InlineData("reservation phase", "UPDATE pg_temp.reservations SET phase='undo' WHERE phase='apply';")]
	[InlineData("reservation class", "UPDATE pg_temp.reservations SET class='ordinary';")]
	[InlineData("witness", "DELETE FROM pg_temp.witness;")]
	[InlineData("receipt revision", "UPDATE pg_temp.live SET receipt_revision=gen_random_uuid();")]
	[InlineData("item revision", "UPDATE pg_temp.live SET item_revision=gen_random_uuid();")]
	[InlineData("taxonomy", "UPDATE pg_temp.live SET taxonomy=gen_random_uuid();")]
	[InlineData("mapping", "UPDATE pg_temp.live SET mapping=gen_random_uuid();")]
	[InlineData("settings", "UPDATE pg_temp.live SET settings=gen_random_uuid();")]
	[InlineData("restore", "UPDATE pg_temp.live SET restore=gen_random_uuid();")]
	[InlineData("disabled", "UPDATE pg_temp.live SET enabled=false;")]
	[InlineData("unavailable", "UPDATE pg_temp.live SET available=false;")]
	[InlineData("suppressed", "UPDATE pg_temp.live SET suppressed=true;")]
	[InlineData("leave as is", "UPDATE pg_temp.live SET leave_as_is=true;")]
	[InlineData("archived category", "UPDATE pg_temp.live SET selectable=false;")]
	[InlineData("manual clear", "UPDATE pg_temp.assignments SET source='manual', category=NULL WHERE id=1;")]
	public async Task InvalidApply_RejectsWithoutAnyPartialRows(string reason, string change)
	{
		await using var db = await Open();
		Func<Task> attempt;
		if (change.StartsWith("SELECT", StringComparison.Ordinal)) { attempt = () => Execute(db, change); }
		else { await Execute(db, change); attempt = () => Execute(db, Apply); }
		await attempt.Should().ThrowAsync<PostgresException>(reason);
		(await Value(db, "SELECT count(*)::text FROM pg_temp.claims")).Should().Be("0");
		(await Value(db, "SELECT count(*)::text FROM pg_temp.assignments")).Should().Be("1");
		(await Value(db, "SELECT current_id::text FROM pg_temp.live")).Should().Be("1");
	}

	[Theory]
	[InlineData("claim")]
	[InlineData("assignment")]
	[InlineData("operation")]
	[InlineData("outcome")]
	[InlineData("current")]
	[InlineData("missing-current")]
	[InlineData("duplicate-current")]
	[InlineData("lineage")]
	[InlineData("assignment-source")]
	[InlineData("operation-actor")]
	[InlineData("outcome-hash")]
	public async Task CaughtStatementFailure_CannotCommitIncompleteOrMismatchedClosure(string damage)
	{
		await using var db = await Open();
		// The function exception is caught at a savepoint. Work before it remains in the outer transaction.
		await using var tx = await db.BeginTransactionAsync();
		await Execute(db, "SELECT pg_temp.run_effect('apply','owner','raw','phase-apply')");
		await tx.SaveAsync("caught");
		Func<Task> failure = () => Execute(db, "INSERT INTO pg_temp.operations SELECT * FROM pg_temp.operations");
		await failure.Should().ThrowAsync<PostgresException>();
		await tx.RollbackAsync("caught");
		string mutation = damage switch
		{
			"claim" => "DELETE FROM pg_temp.claims",
			"assignment" => "DELETE FROM pg_temp.assignments WHERE id=2",
			"operation" => "DELETE FROM pg_temp.operations",
			"outcome" => "DELETE FROM pg_temp.outcomes",
			"current" => "UPDATE pg_temp.live SET current_id=1",
			"missing-current" => "DELETE FROM pg_temp.live",
			"duplicate-current" => "INSERT INTO pg_temp.live SELECT * FROM pg_temp.live",
			"lineage" => "UPDATE pg_temp.assignments SET previous_id=NULL WHERE id=2",
			"assignment-source" => "UPDATE pg_temp.assignments SET source='manual' WHERE id=2",
			"operation-actor" => "UPDATE pg_temp.operations SET actor='intruder'",
			_ => "UPDATE pg_temp.outcomes SET claim_hash='forged'"
		};
		await Execute(db, mutation);
		Func<Task> commit = () => tx.CommitAsync();
		(await commit.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
		(await Value(db, "SELECT count(*)::text FROM pg_temp.assignments")).Should().Be("1");
		(await Value(db, "SELECT count(*)::text FROM pg_temp.claims")).Should().Be("0");
		(await Value(db, "SELECT current_id::text FROM pg_temp.live")).Should().Be("1");
	}

	[Fact]
	public async Task CaughtFailureAfterClaim_StillFailsDeferredCompletionAtCommit()
	{
		await using var db = await Open();
		await using var tx = await db.BeginTransactionAsync();
		await Execute(db, "SELECT pg_temp.make_claim('apply','owner','raw','phase-apply')");
		await tx.SaveAsync("caught");
		Func<Task> fail = () => Execute(db, "SELECT 1/0");
		await fail.Should().ThrowAsync<PostgresException>();
		await tx.RollbackAsync("caught");
		Func<Task> commit = () => tx.CommitAsync();
		await commit.Should().ThrowAsync<PostgresException>();
		(await Value(db, "SELECT count(*)::text FROM pg_temp.claims")).Should().Be("0");
	}

	[Theory]
	[InlineData("UPDATE pg_temp.live SET restore=gen_random_uuid()")]
	[InlineData("UPDATE pg_temp.live SET current_id=1")]
	[InlineData("INSERT INTO pg_temp.assignments VALUES (4,2,NULL,'manual',gen_random_uuid()); UPDATE pg_temp.live SET current_id=4")]
	[InlineData("UPDATE pg_temp.witness SET prior_id=99")]
	[InlineData("UPDATE pg_temp.assignments SET category=99 WHERE id=1")]
	public async Task Undo_RequiresCurrentAutomaticOwnerRestoreAndExactPriorLineage(string damage)
	{
		await using var db = await Open();
		await Execute(db, Apply);
		await Execute(db, damage);
		Func<Task> undo = () => Execute(db, Undo);
		await undo.Should().ThrowAsync<PostgresException>();
		(await Value(db, "SELECT count(*)::text FROM pg_temp.claims")).Should().Be("1");
	}

	[Theory]
	[InlineData("INSERT INTO pg_temp.assignments VALUES (2,1,20,'automatic',gen_random_uuid())")]
	[InlineData("INSERT INTO pg_temp.operations VALUES (gen_random_uuid(),2,'owner','forged')")]
	[InlineData("INSERT INTO pg_temp.outcomes VALUES (gen_random_uuid(),2,'apply','applied','forged')")]
	public async Task OrphanBusinessRows_CannotCommitWithoutClaim(string insert)
	{
		await using var db = await Open();
		Func<Task> orphan = () => Execute(db, insert);
		await orphan.Should().ThrowAsync<PostgresException>();
		(await Value(db, "SELECT count(*)::text FROM pg_temp.assignments")).Should().Be("1");
		(await Value(db, "SELECT count(*)::text FROM pg_temp.operations")).Should().Be("0");
		(await Value(db, "SELECT count(*)::text FROM pg_temp.outcomes")).Should().Be("0");
	}

	[Fact]
	public async Task RolledBackPartialAttempt_AllowsCompleteAttemptInSameOuterTransaction()
	{
		await using var db = await Open();
		await using var tx = await db.BeginTransactionAsync();
		await tx.SaveAsync("partial");
		await Execute(db, "SELECT pg_temp.make_claim('apply','owner','raw','phase-apply')");
		await tx.RollbackAsync("partial");
		await Execute(db, Apply);
		await tx.CommitAsync();
		(await Value(db, "SELECT count(*)::text FROM pg_temp.claims")).Should().Be("1");
		(await Value(db, "SELECT count(*)::text FROM pg_temp.outcomes")).Should().Be("1");
	}

	private const string Apply = "SELECT pg_temp.run_effect('apply','owner','raw','phase-apply')";
	private const string Undo = "SELECT pg_temp.run_effect('undo','owner','raw','phase-undo')";
	private async Task<NpgsqlConnection> Open()
	{
		var db = new NpgsqlConnection(fixture.ConnectionString);
		await db.OpenAsync();
		try { await Execute(db, Schema); return db; }
		catch { await db.DisposeAsync(); throw; }
	}
	private static async Task Execute(NpgsqlConnection db, string sql)
	{
		await using var command = new NpgsqlCommand(sql, db);
		await command.ExecuteNonQueryAsync();
	}
	private static async Task<string?> Value(NpgsqlConnection db, string sql)
	{
		await using var command = new NpgsqlCommand(sql, db);
		return (string?)await command.ExecuteScalarAsync();
	}
}
