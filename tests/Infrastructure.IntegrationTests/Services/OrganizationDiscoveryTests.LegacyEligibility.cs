using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class OrganizationDiscoveryTests
{
	private static PurchaseClassificationEntity LegacyAssignment(Guid item, Guid revision,
			string source = "legacy_import", string? resolution = null) => new()
			{
				Id = Guid.NewGuid(),
				ReceiptItemId = item,
				TaxonomyRevisionId = revision,
				Sequence = 1,
				Source = source,
				LegacyResolution = resolution,
				Actor = "synthetic",
				OperationId = Guid.NewGuid(),
				RecordedAt = DateTimeOffset.UtcNow
			};

	[Fact]
	public async Task LegacyNullProvenanceSurvivesPagingAndRestartWhileChoicesLabelsAndSuppressionStayProtected()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			Guid receipt = Guid.NewGuid();
			var items = Enumerable.Range(0, 11).Select(n => Item(receipt,
					Guid.Parse($"00000000-0000-0000-0000-{n + 1:D12}"), $"Legacy source {n}")).ToArray();
			items[5].Category = "Retained raw category";
			items[6].Subcategory = "Retained raw subcategory";
			await Seed(fixture, items);
			await using var db = fixture.CreateDbContext();
			var head = await db.TaxonomyHeads.SingleAsync();
			Guid revision = Guid.NewGuid(), category = Guid.NewGuid();
			db.TaxonomyRevisions.Add(new TaxonomyRevisionEntity
			{
				Id = revision,
				Sequence = 1,
				PreviousRevisionId = head.RevisionId,
				Actor = "synthetic",
				Reason = "Eligibility fixture",
				OperationId = Guid.NewGuid(),
				RecordedAt = DateTimeOffset.UtcNow
			});
			db.TaxonomyIdentities.Add(new TaxonomyIdentityEntity { Id = category });
			db.TaxonomyNodes.Add(new TaxonomyNodeEntity { RevisionId = revision, CategoryId = category, Name = "Selected" });
			head.RevisionId = revision;
			await db.SaveChangesAsync();
			var assignments = items.Select(x => LegacyAssignment(x.Id, revision)).ToArray();
			assignments[1].Source = "legacy_write"; assignments[1].LegacyResolution = "unresolved_path";
			assignments[2].Source = "manual";
			assignments[3].Source = "unknown";
			assignments[4].Source = "legacy_write"; // Missing resolution cannot prove an unresolved observation.
			assignments[10].CategoryId = category;
			db.Classifications.AddRange(assignments);
			db.CurrentClassifications.AddRange(assignments.Select(x => new CurrentClassificationEntity
			{ ReceiptItemId = x.ReceiptItemId, AssignmentId = x.Id }));
			db.HistoricalGroupDispositions.Add(new HistoricalGroupDispositionEntity
			{
				Id = Guid.NewGuid(),
				Actor = "synthetic",
				Store = "Test store",
				RawDescription = items[7].Description,
				SourceHash = "synthetic",
				Disposition = HistoricalGroupDisposition.LeaveAsIs,
				Version = 1,
				OperationId = Guid.NewGuid(),
				UpdatedAt = DateTimeOffset.UtcNow
			});
			foreach (int n in new[] { 8, 9 })
			{
				var attempt = new CategoryAssistanceAttemptEntity
				{
					Id = Guid.NewGuid(),
					RequestId = Guid.NewGuid(),
					SourceHash = $"synthetic-{n}",
					Location = "Test store",
					Description = items[n].Description,
					RequestScope = Guid.NewGuid(),
					State = "dismissed"
				};
				db.CategoryAssistanceAttempts.Add(attempt);
				db.CategoryAssistanceReviewPreferences.Add(new CategoryAssistanceReviewPreferenceEntity
				{
					AttemptId = attempt.Id,
					Actor = "synthetic",
					State = n == 8 ? "left_as_is" : "active",
					ExcludedItemIdsJson = n == 9 ? $"[\"{items[n].Id}\"]" : "[]",
					Revision = Guid.NewGuid()
				});
			}
			await db.SaveChangesAsync();
			var before = await db.ReceiptItems.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
			var history = await db.Classifications.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
			await db.OrganizationDirtyItems.ExecuteDeleteAsync(); // Require the GUID sweep, not only inbox capture.
			Guid run = await Start(fixture);
			await Store(fixture).DiscoverAsync(run, 1, default);
			Func<Task> interrupt = () => Store(fixture, new FailSave()).DiscoverAsync(run, 1, default);
			await interrupt.Should().ThrowAsync<InvalidOperationException>();
			(await Start(fixture)).Should().Be(run);
			await Drain(fixture, run, 2);
			var manifest = await db.OrganizationItems.AsNoTracking().ToDictionaryAsync(x => x.ItemId);
			foreach (int n in Enumerable.Range(0, items.Length))
			{
				manifest[items[n].Id].State.Should().Be(n < 2 ? "observed" : "protected", $"case {n}");
				manifest[items[n].Id].ExpectedAssignmentId.Should().Be(assignments[n].Id);
				manifest[items[n].Id].PreviousCategoryId.Should().Be(assignments[n].CategoryId);
				if (n < 2) { manifest[items[n].Id].SourceId.Should().NotBeNull(); }
				else { manifest[items[n].Id].SourceId.Should().BeNull(); }
			}
			(await db.OrganizationSources.CountAsync()).Should().Be(2);
			(await db.ReceiptItems.AsNoTracking().OrderBy(x => x.Id).ToListAsync()).Should().BeEquivalentTo(before);
			(await db.Classifications.AsNoTracking().OrderBy(x => x.Id).ToListAsync()).Should().BeEquivalentTo(history);
		}
		finally { await fixture.DisposeAsync(); }
	}

	[Fact]
	public async Task ConcurrentManualClearOfObservedLegacyNullIsRecapturedWithoutLosingDirtyAssignment()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			var item = Item(Guid.NewGuid(), Guid.NewGuid()); await Seed(fixture, item);
			await using var db = fixture.CreateDbContext();
			Guid revision = (await db.TaxonomyHeads.SingleAsync()).RevisionId;
			var legacy = LegacyAssignment(item.Id, revision, "legacy_write", "unresolved_path");
			db.Classifications.Add(legacy);
			db.CurrentClassifications.Add(new CurrentClassificationEntity { ReceiptItemId = item.Id, AssignmentId = legacy.Id });
			await db.SaveChangesAsync();
			Guid run = await Start(fixture);
			var pause = new PauseSave();
			Task capture = Store(fixture, pause).DiscoverAsync(run, 1, default);
			await pause.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await using var producer = fixture.CreateDbContext();
			await producer.Database.OpenConnectionAsync();
			int pid = ((NpgsqlConnection)producer.Database.GetDbConnection()).ProcessID;
			var manual = LegacyAssignment(item.Id, revision, "manual");
			manual.Sequence = 2; manual.PreviousAssignmentId = legacy.Id;
			producer.Classifications.Add(manual);
			var current = await producer.CurrentClassifications.SingleAsync(); current.AssignmentId = manual.Id;
			Task update = producer.SaveChangesAsync();
			try
			{
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				while (!await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE pid = {pid} AND wait_event_type = 'Lock'")
						.AnyAsync(x => x == 1, timeout.Token)) { await Task.Delay(20, timeout.Token); }
			}
			finally { pause.Release.TrySetResult(); }
			await Task.WhenAll(capture, update);
			var observed = await db.OrganizationItems.AsNoTracking().SingleAsync();
			observed.State.Should().Be("observed"); observed.ExpectedAssignmentId.Should().Be(legacy.Id);
			(await db.OrganizationDirtyItems.CountAsync()).Should().Be(1);
			await Drain(fixture, run, 1);
			var refreshed = await db.OrganizationItems.AsNoTracking().SingleAsync();
			refreshed.State.Should().Be("protected"); refreshed.SourceId.Should().BeNull();
			refreshed.ExpectedAssignmentId.Should().Be(manual.Id);
			(await db.Classifications.CountAsync()).Should().Be(2);
			(await db.CurrentClassifications.AsNoTracking().SingleAsync()).AssignmentId.Should().Be(manual.Id);
		}
		finally { await fixture.DisposeAsync(); }
	}
}
