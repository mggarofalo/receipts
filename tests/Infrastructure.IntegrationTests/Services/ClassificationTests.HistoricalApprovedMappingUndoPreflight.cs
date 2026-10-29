using System.Net;
using Application.Taxonomy;
using Domain.Taxonomy;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ApprovedUndoRejectsStaleInitialTaxonomyWithoutConsumingIntentOrRows(bool http)
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedUndoPreflightCore(http);
	}

	private async Task ApprovedUndoPreflightCore(bool http)
	{
		var captured = await CaptureExecutableAsync();
		var executor = ApprovedExecutor();
		await executor.ApplyChunkAsync(captured.Request.ExpectedRestoreGeneration, Actor, captured.Request.OperationId, 1);
		var fresh = await Tree().ChangeAsync(Actor, new(captured.Request.TaxonomyRevisionId, Guid.NewGuid(),
				"Rename category", [new TaxonomyEdit("rename", captured.Category, Name: Guid.NewGuid().ToString())]));
		var stale = new HistoricalUndoCommand(captured.Request.TaxonomyRevisionId, "Restore prior choice");
		await using var app = await HistoricalHttpHostAsync();
		using var client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("X-Test-Actor", Actor);
		string route = $"/api/historical-reclassifications/{captured.Request.OperationId}/undo";
		if (http)
		{
			var error = await HistoricalJson(await client.PostAsync(route,
					HistoricalBody(new { stale.TaxonomyRevisionId, stale.Reason, limit = 1 })), HttpStatusCode.Conflict);
			error.GetProperty("code").GetString().Should().Be("stale-undo-taxonomy");
		}
		else
		{
			var error = await Assert.ThrowsAsync<TaxonomyException>(() => executor.UndoChunkAsync(
					CurrentRestoreGeneration(), Actor, captured.Request.OperationId, stale, 1));
			error.Status.Should().Be(409);
			error.Extensions["code"].Should().Be("stale-undo-taxonomy");
		}
		await using (var db = fixture.CreateDbContext())
		{
			var operation = await db.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == captured.Request.OperationId);
			operation.State.Should().Be(HistoricalOperationState.Completed);
			operation.UndoTaxonomyRevisionId.Should().BeNull();
			operation.UndoReason.Should().BeNull();
			var row = await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == operation.Id);
			row.UndoState.Should().Be(HistoricalItemState.Pending);
			row.UndoAssignmentId.Should().BeNull();
			row.UndoReason.Should().BeNull();
		}
		(await Service().HistoryAsync(captured.Item, null, 10)).Items.Should().HaveCount(2);
		var retry = stale with { TaxonomyRevisionId = fresh.Id };
		if (http)
		{
			var result = await HistoricalJson(await client.PostAsync(route,
					HistoricalBody(new { retry.TaxonomyRevisionId, retry.Reason, limit = 1 })));
			result.GetProperty("undone").GetInt32().Should().Be(1);
			var changedIntent = await HistoricalJson(await client.PostAsync(route,
					HistoricalBody(new { stale.TaxonomyRevisionId, stale.Reason, limit = 1 })), HttpStatusCode.Conflict);
			changedIntent.TryGetProperty("code", out _).Should().BeFalse();
		}
		else
		{
			(await executor.UndoChunkAsync(CurrentRestoreGeneration(), Actor, captured.Request.OperationId, retry, 1))
					.Undone.Should().Be(1);
			var error = await Assert.ThrowsAsync<TaxonomyException>(() => executor.UndoChunkAsync(
					CurrentRestoreGeneration(), Actor, captured.Request.OperationId, stale, 1));
			error.Extensions.Should().NotContainKey("code");
		}
		var history = await Service().HistoryAsync(captured.Item, null, 10);
		history.Items.Should().HaveCount(3);
		history.Items.First().CategoryId.Should().BeNull();
		history.Items.First().Source.Should().Be("manual");
	}
}
