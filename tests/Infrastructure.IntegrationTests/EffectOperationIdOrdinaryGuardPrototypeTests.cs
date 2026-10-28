using System.Diagnostics;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Npgsql;

namespace Infrastructure.IntegrationTests;

// Test-only shape shared by independent sessions. No triggers attach to application tables.
// Deliberately has NO effect execution exception: a reservation is not execution authority.
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class EffectOperationIdOrdinaryGuardPrototypeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Fact]
	public async Task OrdinaryReuse_ClaimsAllNewIdsOnInsertAndUpdate_AndRetainsOldReservations()
	{
		await using var first = await Open();
		await using var second = await Open();
		Guid id = Guid.NewGuid(), changed = Guid.NewGuid(), request = Guid.NewGuid();
		await Write(first, id);
		// Different actor/domain/related row shapes may legitimately share an ordinary UUID.
		await Write(first, id);
		await Write(second, id);
		await Execute(first, "UPDATE pg_temp.ordinary_rows SET operation_id = @id, request_id = @request", ("id", changed), ("request", request));
		foreach (Guid reserved in new[] { id, changed, request })
		{
			(await Class(first, reserved)).Should().Be("ordinary");
		}
		await Execute(first, "INSERT INTO pg_temp.ordinary_rows DEFAULT VALUES"); // optional NULL is not a claim
	}

	[Fact]
	public async Task ConcurrentOrdinaryReuse_WaitsAndThenAllowsBothBusinessRows()
	{
		await using var first = await Open();
		await using var second = await Open();
		await using var observer = await Open();
		Guid id = Guid.NewGuid();
		await using var transaction = await first.BeginTransactionAsync();
		await Write(first, id);
		Task pending = Write(second, id);
		bool blocked = await WaitForBlocked(observer, second.ProcessID, pending);
		await transaction.CommitAsync();
		await pending.WaitAsync(TimeSpan.FromSeconds(15));
		blocked.Should().BeTrue();
		(await BusinessRows(first)).Should().Be(1);
		(await BusinessRows(second)).Should().Be(1);
		(await Class(observer, id)).Should().Be("ordinary");
	}

	[Fact]
	public async Task ReservedAndLegacyBlockedIds_RejectWritesAndUpdates_WithoutOwnerMatchBypass()
	{
		await using var connection = await Open();
		Guid manifest = Guid.NewGuid(), item = Guid.NewGuid();
		Guid apply = Guid.Parse("10780000-0000-0000-0000-000000000002");
		Guid undo = Guid.Parse("10780000-0000-0000-0000-000000000003");
		await Effect(connection, manifest, item, apply, undo);
		await Reject(() => Write(connection, apply));
		Guid ordinary = Guid.NewGuid();
		// PostgreSQL UUID order guarantees this fresh claim precedes the rejected undo claim.
		Guid rolledBack = Guid.Parse("10780000-0000-0000-0000-000000000001");
		await Write(connection, ordinary);
		await Reject(() => Execute(connection, "UPDATE pg_temp.ordinary_rows SET operation_id = @id, request_id = @request", ("id", rolledBack), ("request", undo)));
		(await Class(connection, rolledBack)).Should().BeNull();
		(await Class(connection, ordinary)).Should().Be("ordinary");
		await using (var command = new NpgsqlCommand("SELECT operation_id, request_id FROM pg_temp.ordinary_rows", connection))
		await using (var reader = await command.ExecuteReaderAsync())
		{
			(await reader.ReadAsync()).Should().BeTrue();
			reader.GetGuid(0).Should().Be(ordinary);
			reader.IsDBNull(1).Should().BeTrue();
			(await reader.ReadAsync()).Should().BeFalse();
		}
		Guid blocked = Guid.NewGuid();
		await Execute(connection, """
			INSERT INTO taxonomy."EffectOperationIdRegistry" ("OperationId","Class","ManifestId","ItemId","Phase")
			VALUES (@id,'legacy_blocked',@manifest,@item,'apply')
			""", ("id", blocked), ("manifest", Guid.NewGuid()), ("item", Guid.NewGuid()));
		await Reject(() => Write(connection, blocked));
		(await Class(connection, blocked)).Should().Be("legacy_blocked");
	}

	[Theory]
	[InlineData(true, true)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(false, false)]
	public async Task TriggerAndEffectClaims_ArbitrateBothOrdersAndRollback(bool ordinaryFirst, bool commit)
	{
		await using var winner = await Open();
		await using var contender = await Open();
		await using var observer = await Open();
		Guid id = Guid.NewGuid(), other = Guid.NewGuid(), manifest = Guid.NewGuid(), item = Guid.NewGuid();
		await using var transaction = await winner.BeginTransactionAsync();
		if (ordinaryFirst)
		{
			await Write(winner, id);
		}
		else
		{
			await Effect(winner, manifest, item, id, other);
		}

		Task pending = ordinaryFirst ? Effect(contender, manifest, item, id, other) : Write(contender, id);
		bool blocked = await WaitForBlocked(observer, contender.ProcessID, pending);
		if (commit)
		{
			await transaction.CommitAsync();
		}
		else
		{
			await transaction.RollbackAsync();
		}

		if (commit)
		{
			await Reject(() => pending.WaitAsync(TimeSpan.FromSeconds(15)));
		}
		else
		{
			await pending.WaitAsync(TimeSpan.FromSeconds(15));
		}

		blocked.Should().BeTrue("the assertion must observe PostgreSQL blocking, not a timed task delay");
		(await Class(observer, id)).Should().Be(ordinaryFirst == commit ? "ordinary" : "effect");
		(await Class(observer, other)).Should().Be(ordinaryFirst == commit ? null : "effect");
		// Business row and trigger claim roll back together, including an effect-wins rejection.
		long businessRows = await BusinessRows(ordinaryFirst ? winner : contender);
		businessRows.Should().Be(ordinaryFirst == commit ? 1 : 0);
	}

	private async Task<NpgsqlConnection> Open()
	{
		var connection = new NpgsqlConnection(fixture.ConnectionString);
		await connection.OpenAsync();
		try
		{
			await Execute(connection, """
				CREATE TEMP TABLE ordinary_rows (operation_id uuid, request_id uuid);
				CREATE FUNCTION pg_temp.guard_ordinary_ids() RETURNS trigger LANGUAGE plpgsql AS $$
				DECLARE id uuid;
				BEGIN
				    -- Per-row order only. Multirow/multicommand transactions still need a global policy.
				    FOR id IN SELECT DISTINCT value FROM unnest(ARRAY[NEW.operation_id, NEW.request_id]) value
				        WHERE value IS NOT NULL ORDER BY value LOOP
				        PERFORM taxonomy.claim_ordinary_operation_id(id);
				    END LOOP;
				    RETURN NEW;
				END $$;
				CREATE TRIGGER guard_ordinary_ids BEFORE INSERT OR UPDATE ON pg_temp.ordinary_rows
				FOR EACH ROW EXECUTE FUNCTION pg_temp.guard_ordinary_ids();
				""");
			return connection;
		}
		catch
		{
			await connection.DisposeAsync();
			throw;
		}
	}

	private static Task Write(NpgsqlConnection connection, Guid id) => Execute(connection,
		"INSERT INTO pg_temp.ordinary_rows (operation_id) VALUES (@id)", ("id", id));
	private static Task Effect(NpgsqlConnection connection, Guid manifest, Guid item, Guid apply, Guid undo) => Execute(connection,
		"SELECT taxonomy.claim_effect_operation_ids(@manifest,@item,@apply,@undo)",
		("manifest", manifest), ("item", item), ("apply", apply), ("undo", undo));
	private static async Task Execute(NpgsqlConnection connection, string sql, params (string Name, Guid Value)[] parameters)
	{
		await using var command = new NpgsqlCommand(sql, connection);
		foreach (var parameter in parameters)
		{
			command.Parameters.AddWithValue(parameter.Name, parameter.Value);
		}

		await command.ExecuteNonQueryAsync();
	}
	private static async Task<string?> Class(NpgsqlConnection connection, Guid id)
	{
		await using var command = new NpgsqlCommand("SELECT \"Class\" FROM taxonomy.\"EffectOperationIdRegistry\" WHERE \"OperationId\" = @id", connection);
		command.Parameters.AddWithValue("id", id);
		return (string?)await command.ExecuteScalarAsync();
	}
	private static async Task<long> BusinessRows(NpgsqlConnection connection)
	{
		await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_temp.ordinary_rows", connection);
		return (long)(await command.ExecuteScalarAsync())!;
	}
	private static async Task Reject(Func<Task> action) =>
		(await action.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
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
