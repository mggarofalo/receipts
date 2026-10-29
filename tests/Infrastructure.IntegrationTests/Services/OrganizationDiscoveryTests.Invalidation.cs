using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class OrganizationDiscoveryTests
{
	[Fact]
	public async Task DetachedPreferenceInvalidatesBothSourcesAndRechecksHashCollisions()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			Guid receipt = Guid.NewGuid();
			var first = Item(receipt, Guid.NewGuid(), "First");
			var second = Item(receipt, Guid.NewGuid(), "Second");
			second.ReceiptItemCode = "";
			var collision = Item(receipt, Guid.NewGuid(), "A\u001fB");
			await Seed(fixture, first, second, collision);
			Guid run = await Start(fixture); await Drain(fixture, run);
			await using var db = fixture.CreateDbContext();
			var preference = new CategoryAssistanceReviewPreferenceEntity
			{
				Actor = "other-actor",
				AttemptId = Guid.NewGuid(),
				Location = "Test store",
				Description = first.Description,
				ItemCodeKey = "",
				State = "left_as_is",
				Revision = Guid.NewGuid()
			};
			db.CategoryAssistanceReviewPreferences.Add(preference);
			db.CategoryAssistanceReviewPreferences.Add(new CategoryAssistanceReviewPreferenceEntity
			{
				Actor = "other-actor",
				AttemptId = Guid.NewGuid(),
				Location = "Test store\u001fA",
				Description = "B",
				ItemCodeKey = "",
				State = "later",
				Revision = Guid.NewGuid()
			});
			await db.SaveChangesAsync(); await Drain(fixture, run);
			(await db.OrganizationItems.AsNoTracking().SingleAsync(x => x.ItemId == first.Id)).State.Should().Be("protected");
			(await db.OrganizationItems.AsNoTracking().SingleAsync(x => x.ItemId == collision.Id)).State.Should().Be("observed");
			preference.Description = second.Description;
			await db.SaveChangesAsync(); await Drain(fixture, run);
			(await db.OrganizationItems.AsNoTracking().SingleAsync(x => x.ItemId == first.Id)).State.Should().Be("observed");
			(await db.OrganizationItems.AsNoTracking().SingleAsync(x => x.ItemId == second.Id)).State.Should().Be("protected");
			db.CategoryAssistanceReviewPreferences.Remove(preference);
			await db.SaveChangesAsync(); await Drain(fixture, run);
			(await db.OrganizationItems.AsNoTracking().SingleAsync(x => x.ItemId == second.Id)).State.Should().Be("observed");
			(await db.CategoryAssistanceAttempts.CountAsync()).Should().Be(0);
		}
		finally { await fixture.DisposeAsync(); }
	}

	private sealed class PauseSave : SaveChangesInterceptor
	{
		public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
		{
			Arrived.TrySetResult();
			await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
			return result;
		}
	}

	[Fact]
	public async Task SourceInvalidationBoundsNonmatchingCandidatesAndConcurrentProducerResetsCursorWithoutLostAck()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			Guid receipt = Guid.NewGuid();
			await Seed(fixture, Enumerable.Range(1, 7).Select(n => Item(receipt, Guid.Parse($"00000000-0000-0000-0000-{n:D12}"))).ToArray());
			Guid run = await Start(fixture); await Drain(fixture, run);
			await using var db = fixture.CreateDbContext();
			await db.Database.ExecuteSqlRawAsync("SELECT organization.enqueue_source('raw', 'Different store', 'Repeated purchase', NULL)");
			(await db.OrganizationDirtyItems.CountAsync()).Should().Be(0);
			(await Store(fixture).DiscoverAsync(run, 2, default)).Should().Be(2, "examined nonmatching candidates still represent progress");
			(await db.OrganizationDirtySources.AsNoTracking().SingleAsync()).Cursor.Should().NotBeNull();
			var pause = new PauseSave();
			Task capture = Store(fixture, pause).DiscoverAsync(run, 2, default);
			await pause.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await using var producer = new NpgsqlConnection(fixture.ConnectionString);
			await producer.OpenAsync();
			int pid = producer.ProcessID;
			await using var command = new NpgsqlCommand("SELECT organization.enqueue_source('raw', 'Different store', 'Repeated purchase', NULL)", producer);
			Task update = command.ExecuteNonQueryAsync();
			try
			{
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				while (!await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE pid = {pid} AND wait_event_type = 'Lock'").AnyAsync(x => x == 1, timeout.Token))
				{
					await Task.Delay(20, timeout.Token);
				}
			}
			finally { pause.Release.TrySetResult(); }
			await Task.WhenAll(capture, update);
			(await db.OrganizationDirtySources.AsNoTracking().SingleAsync()).Cursor.Should().BeNull("producer invalidation after captured cursor must restart the source scan");
			await Drain(fixture, run, 2);
			(await db.OrganizationDirtySources.CountAsync()).Should().Be(0);
			(await db.OrganizationItems.CountAsync()).Should().Be(7);
		}
		finally { await fixture.DisposeAsync(); }
	}

	[Fact]
	public async Task ManualNullLegacyDispositionPreferenceAndDeletedSourcesRemainProtectedAndUnchanged()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			Guid receipt = Guid.NewGuid();
			var items = Enumerable.Range(0, 6).Select(n => Item(receipt, Guid.NewGuid(), $"Source {n}")).ToArray();
			items[1].Category = "Legacy category";
			await Seed(fixture, items);
			await using var db = fixture.CreateDbContext();
			var head = await db.TaxonomyHeads.SingleAsync();
			Guid manualNull = Guid.NewGuid();
			db.Classifications.Add(new PurchaseClassificationEntity { Id = manualNull, ReceiptItemId = items[0].Id, TaxonomyRevisionId = head.RevisionId, Sequence = 1, Source = "manual", Actor = "synthetic", OperationId = Guid.NewGuid(), RecordedAt = DateTimeOffset.UtcNow });
			db.CurrentClassifications.Add(new CurrentClassificationEntity { ReceiptItemId = items[0].Id, AssignmentId = manualNull });
			db.HistoricalGroupDispositions.Add(new HistoricalGroupDispositionEntity { Id = Guid.NewGuid(), Actor = "synthetic", Store = "Test store", RawDescription = items[2].Description, SourceHash = "synthetic", Disposition = HistoricalGroupDisposition.LeaveAsIs, Version = 1, OperationId = Guid.NewGuid(), UpdatedAt = DateTimeOffset.UtcNow });
			var attempt = new CategoryAssistanceAttemptEntity { Id = Guid.NewGuid(), RequestId = Guid.NewGuid(), SourceHash = "synthetic", Location = "Test store", Description = items[3].Description, RequestScope = Guid.NewGuid(), State = "dismissed" };
			db.CategoryAssistanceAttempts.Add(attempt);
			db.CategoryAssistanceReviewPreferences.Add(new CategoryAssistanceReviewPreferenceEntity { AttemptId = attempt.Id, Location = attempt.Location, Description = attempt.Description, ItemCodeKey = attempt.ItemCodeKey, Actor = "synthetic", State = "left_as_is", Revision = Guid.NewGuid() });
			await db.SaveChangesAsync();
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"DeletedAt\" = now() WHERE \"Id\" = {items[4].Id}");
			var before = await db.ReceiptItems.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToListAsync();
			Guid run = await Start(fixture); await Drain(fixture, run, 3);
			var manifest = await db.OrganizationItems.ToDictionaryAsync(x => x.ItemId);
			foreach (var item in items.Take(4)) { manifest[item.Id].State.Should().Be("protected"); manifest[item.Id].SourceId.Should().BeNull(); }
			manifest[items[4].Id].State.Should().Be("unavailable");
			manifest[items[5].Id].State.Should().Be("observed");
			manifest[items[0].Id].ExpectedAssignmentId.Should().Be(manualNull);
			manifest[items[0].Id].PreviousCategoryId.Should().BeNull();
			(await db.ReceiptItems.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToListAsync()).Should().BeEquivalentTo(before);
			(await db.Classifications.AsNoTracking().SingleAsync()).CategoryId.Should().BeNull();
			(await db.CurrentClassifications.SingleAsync()).AssignmentId.Should().Be(manualNull);
			(await db.HistoricalGroupDispositions.AsNoTracking().SingleAsync()).Disposition.Should().Be(HistoricalGroupDisposition.LeaveAsIs);
			(await db.CategoryAssistanceReviewPreferences.AsNoTracking().SingleAsync()).State.Should().Be("left_as_is");
		}
		finally { await fixture.DisposeAsync(); }
	}

	[Fact]
	public async Task AssignmentToArchivedTaxonomyNodeRemainsProtectedWithImmutableCategoryAndHistory()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			var item = Item(Guid.NewGuid(), Guid.NewGuid()); await Seed(fixture, item);
			await using var db = fixture.CreateDbContext();
			var head = await db.TaxonomyHeads.SingleAsync();
			Guid category = Guid.NewGuid(), revision = Guid.NewGuid(), assignment = Guid.NewGuid();
			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				db.TaxonomyRevisions.Add(new TaxonomyRevisionEntity { Id = revision, Sequence = 1, PreviousRevisionId = head.RevisionId, Actor = "synthetic", Reason = "Archived history fixture", OperationId = Guid.NewGuid(), RecordedAt = DateTimeOffset.UtcNow });
				db.TaxonomyIdentities.Add(new TaxonomyIdentityEntity { Id = category });
				db.TaxonomyNodes.Add(new TaxonomyNodeEntity { RevisionId = revision, CategoryId = category, Name = "Archived fixture", Archived = true });
				head.RevisionId = revision;
				await db.SaveChangesAsync(); await tx.CommitAsync();
			}
			db.Classifications.Add(new PurchaseClassificationEntity { Id = assignment, ReceiptItemId = item.Id, TaxonomyRevisionId = revision, CategoryId = category, Sequence = 1, Source = "manual", Actor = "synthetic", Reason = "Retained historical assignment", OperationId = Guid.NewGuid(), RecordedAt = DateTimeOffset.UtcNow });
			db.CurrentClassifications.Add(new CurrentClassificationEntity { ReceiptItemId = item.Id, AssignmentId = assignment }); await db.SaveChangesAsync();
			var before = await db.Classifications.AsNoTracking().SingleAsync();
			await Drain(fixture, await Start(fixture));
			var captured = await db.OrganizationItems.AsNoTracking().SingleAsync();
			captured.State.Should().Be("protected"); captured.PreviousCategoryId.Should().Be(category); captured.ExpectedAssignmentId.Should().Be(assignment);
			(await db.Classifications.AsNoTracking().SingleAsync()).Should().BeEquivalentTo(before);
			(await db.TaxonomyNodes.AsNoTracking().SingleAsync()).Archived.Should().BeTrue();
		}
		finally { await fixture.DisposeAsync(); }
	}

	[Fact]
	public async Task TaxonomyHeadChangeStalesRunBeforeAcknowledgingQueuedFacts()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			await Seed(fixture, Item(Guid.NewGuid(), Guid.NewGuid())); Guid run = await Start(fixture);
			await using var db = fixture.CreateDbContext();
			var head = await db.TaxonomyHeads.SingleAsync();
			var revision = new TaxonomyRevisionEntity { Id = Guid.NewGuid(), Sequence = 1, PreviousRevisionId = head.RevisionId, Actor = "synthetic", Reason = "Fence test", OperationId = Guid.NewGuid(), RecordedAt = DateTimeOffset.UtcNow };
			db.TaxonomyRevisions.Add(revision); head.RevisionId = revision.Id; await db.SaveChangesAsync();
			(await Store(fixture).DiscoverAsync(run, 2, default)).Should().Be(0);
			(await db.OrganizationRuns.AsNoTracking().SingleAsync()).State.Should().Be("stale");
			(await db.OrganizationDirtyItems.CountAsync()).Should().Be(1);
			(await db.OrganizationItems.CountAsync()).Should().Be(0);
		}
		finally { await fixture.DisposeAsync(); }
	}
}

