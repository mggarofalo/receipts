using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationAiWorkflowTests
{
	[Fact]
	public async Task Recovery_WaitsForIndependentPostgresGateAndCannotClearNewerGeneration()
	{
		await SeedAsync();
		var service = Service();
		Guid stale = await service.BeginConnectionChangeAsync(default);
		await using var other = new NpgsqlConnection(fixture.ConnectionString);
		await other.OpenAsync();
		await using var transaction = await other.BeginTransactionAsync();
		await using var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1380143188, 1313821261)", other, transaction);
		await acquire.ExecuteNonQueryAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		Task<bool> recovery = service.RecoverConnectionChangeAsync(timeout.Token);
		try
		{
			await using var waiting = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype='advisory' AND NOT granted AND pg_backend_pid() = ANY(pg_blocking_pids(pid)))", other, transaction);
			while (!(bool)(await waiting.ExecuteScalarAsync(timeout.Token))!)
			{
				recovery.IsCompleted.Should().BeFalse("recovery must wait on the database gate");
				await Task.Delay(20, timeout.Token);
			}
			Guid newer = Guid.NewGuid();
			await using var replace = new NpgsqlCommand("UPDATE matching.\"NormalizationAiConnection\" SET \"Generation\" = @generation", other, transaction);
			replace.Parameters.AddWithValue("generation", newer);
			(await replace.ExecuteNonQueryAsync(timeout.Token)).Should().Be(1);
			await transaction.CommitAsync(timeout.Token);
			(await recovery).Should().BeFalse("the observed stale generation was superseded while waiting");
			await using var check = fixture.CreateDbContext();
			var current = await check.NormalizationAiConnection.SingleAsync(timeout.Token);
			current.Changing.Should().BeTrue();
			current.Generation.Should().Be(newer).And.NotBe(stale);
			(await service.RecoverConnectionChangeAsync(timeout.Token)).Should().BeTrue();
			check.ChangeTracker.Clear();
			current = await check.NormalizationAiConnection.SingleAsync(timeout.Token);
			current.Changing.Should().BeFalse();
			current.Generation.Should().NotBe(newer);
		}
		finally
		{
			timeout.Cancel();
			await transaction.DisposeAsync();
			try { await recovery; } catch (OperationCanceledException) { }
		}
	}
}
