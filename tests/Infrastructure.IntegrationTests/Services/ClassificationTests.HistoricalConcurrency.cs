using System.Data.Common;
using Application.Taxonomy;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	// Pause after PostgreSQL grants the transaction gate. The competing service
	// uses another physical connection; pg_blocking_pids proves actual contention.
	private sealed class HistoricalRaceGate(bool pause, bool writeGateOnly = false) : DbCommandInterceptor
	{
		public TaskCompletionSource<int> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
			CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("pg_advisory_xact_lock") && (!writeGateOnly || command.Parameters.Count == 2))
			{ Entered.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID); }
			return ValueTask.FromResult(result);
		}
		public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command,
			CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("pg_advisory_xact_lock") && (!writeGateOnly || command.Parameters.Count == 2))
			{
				Acquired.TrySetResult();
				if (pause) { await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
			}
			return result;
		}
	}

	private sealed class HistoricalRaceFactory(PostgresFixture fixture, HistoricalRaceGate gate)
		: IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
			.AddInterceptors(gate).Options);
	}

	private async Task AssertHistoricalDatabaseWait(HistoricalRaceGate winner, HistoricalRaceGate loser)
	{
		int holdingPid = await winner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		int waitingPid = await loser.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		waitingPid.Should().NotBe(holdingPid);
		await using var connection = new NpgsqlConnection(fixture.ConnectionString);
		await connection.OpenAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		bool blocked = false;
		while (!blocked)
		{
			await using var command = new NpgsqlCommand("SELECT @holder = ANY(pg_blocking_pids(@waiter))", connection);
			command.Parameters.AddWithValue("holder", holdingPid);
			command.Parameters.AddWithValue("waiter", waitingPid);
			blocked = (bool)(await command.ExecuteScalarAsync(timeout.Token))!;
			if (!blocked) { await Task.Delay(10, timeout.Token); }
		}
		loser.Acquired.Task.IsCompleted.Should().BeFalse("the competing database transaction must wait for the gate");
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task HistoricalCancelAndChunkSerializeAcrossIndependentConnections(bool cancelFirst)
	{
		var seed = await HistoricalSeedAsync();
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			var source = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
			var sibling = SampleData.Entities.ReceiptItemEntityGenerator.Generate(source.ReceiptId);
			sibling.Description = source.Description;
			sibling.ReceiptItemCode = source.ReceiptItemCode;
			await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync([sibling], default);
		}
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		(await Historical().SavePreviewAsync(request)).Total.Should().Be(2);
		var winner = new HistoricalRaceGate(true);
		var loser = new HistoricalRaceGate(false);
		var first = Historical(new HistoricalRaceFactory(fixture, winner));
		var second = Historical(new HistoricalRaceFactory(fixture, loser));
		Task<HistoricalProgress> leading = cancelFirst
			? first.CancelAsync(Actor, request.OperationId)
			: first.ApplyChunkAsync(Actor, request.OperationId, 1);
		Task<HistoricalProgress>? trailing = null;
		try
		{
			await winner.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
			trailing = cancelFirst
				? second.ApplyChunkAsync(Actor, request.OperationId, 1)
				: second.CancelAsync(Actor, request.OperationId);
			await AssertHistoricalDatabaseWait(winner, loser);
		}
		finally { winner.Release.TrySetResult(); }
		await leading;
		await trailing!;
		var final = await Historical().ApplyChunkAsync(Actor, request.OperationId, 100);
		final.State.Should().Be(HistoricalOperationState.Cancelled);
		final.Applied.Should().Be(cancelFirst ? 0 : 1);
		final.Pending.Should().Be(cancelFirst ? 2 : 1);
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await (from assignment in verify.Classifications
				join row in verify.HistoricalOperationItems on assignment.ReceiptItemId equals row.ItemId
				where row.OperationId == request.OperationId
				select assignment.Id).CountAsync())
			.Should().Be(cancelFirst ? 2 : 3);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task HistoricalUndoAndManualEditSerializeWithoutOverwritingLaterAssignment(bool manualFirst)
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		await Historical().ApplyChunkAsync(Actor, request.OperationId, 1);
		var applied = (await Service().HistoryAsync(seed.Item, null, 1)).Items.Single();
		var winner = new HistoricalRaceGate(true);
		var loser = new HistoricalRaceGate(false);
		var historical = Historical(new HistoricalRaceFactory(fixture, manualFirst ? loser : winner));
		var manual = new ClassificationService(new HistoricalRaceFactory(fixture, manualFirst ? winner : loser),
			TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = true }));
		var command = new ClassificationCommand(seed.Child, seed.Revision, applied.Id, Guid.NewGuid(), "Concurrent manual choice");
		var undo = new HistoricalUndoCommand(seed.Revision, "Conditional concurrent undo");
		Task<PurchaseClassification>? edit = null;
		Task<HistoricalProgress>? reverting = null;
		if (manualFirst) { edit = manual.AssignAsync(Actor, seed.Item, command); }
		else { reverting = historical.UndoChunkAsync(Actor, request.OperationId, undo, 1); }
		try
		{
			await winner.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
			if (manualFirst) { reverting = historical.UndoChunkAsync(Actor, request.OperationId, undo, 1); }
			else { edit = manual.AssignAsync(Actor, seed.Item, command); }
			await AssertHistoricalDatabaseWait(winner, loser);
		}
		finally { winner.Release.TrySetResult(); }
		if (manualFirst)
		{
			var later = await edit!;
			(await reverting!).UndoConflicted.Should().Be(1);
			(await Service().HistoryAsync(seed.Item, null, 1)).Items.Single().Id.Should().Be(later.Id);
			var row = (await Historical().ReadItemsAsync(Actor, request.OperationId, 0, 1)).UndoItems.Single();
			row.Reason.Should().Be("assignment-no-longer-owned");
		}
		else
		{
			(await reverting!).Undone.Should().Be(1);
			Func<Task> staleManual = async () => { await edit!; };
			(await staleManual.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
			var restored = (await Service().HistoryAsync(seed.Item, null, 1)).Items.Single();
			var later = await Service().AssignAsync(Actor, seed.Item, command with { ExpectedAssignmentId = restored.Id });
			(await Historical().UndoChunkAsync(Actor, request.OperationId, undo, 1)).Undone.Should().Be(1);
			(await Service().HistoryAsync(seed.Item, null, 1)).Items.Single().Id.Should().Be(later.Id);
		}
		(await Service().HistoryAsync(seed.Item, null, 100)).Items.Should().HaveCount(manualFirst ? 3 : 4);
	}
}
