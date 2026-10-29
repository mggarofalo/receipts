using System.Text.Json;
using Application.Classification;
using Application.Normalization;
using Application.Taxonomy;
using Common;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class CategoryAssistanceDecisionStoreTests(PostgresFixture server)
{
	private sealed class Factory(PostgresFixture fixture, IInterceptor? interceptor = null) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => interceptor is null ? fixture.CreateDbContext() :
		new(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(interceptor).Options);
	}

	private sealed class PauseSave : SaveChangesInterceptor
	{
		public TaskCompletionSource<int> Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
			InterceptionResult<int> result, CancellationToken cancellationToken = default)
		{
			Arrived.TrySetResult(((NpgsqlConnection)eventData.Context!.Database.GetDbConnection()).ProcessID);
			await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
			return result;
		}
	}

	private static async Task WaitForBlockedGate(PostgresFixture fixture, int holderPid, Task waiter, CancellationToken ct)
	{
		await using var observer = new NpgsqlConnection(fixture.ConnectionString);
		await observer.OpenAsync(ct);
		await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks l WHERE l.locktype='advisory' AND NOT l.granted AND l.database=(SELECT oid FROM pg_database WHERE datname=current_database()) AND @holder = ANY(pg_blocking_pids(l.pid)))", observer);
		command.Parameters.AddWithValue("holder", holderPid);
		while (!(bool)(await command.ExecuteScalarAsync(ct))!)
		{
			if (waiter.IsCompleted) { await waiter; throw new InvalidOperationException("Expected a waiter on the holder's gate."); }
			await Task.Delay(20, ct);
		}
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task DiscoveryAndPreferenceUpdateSerializeButManifestRemainsAdvisory(bool updateFirst)
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		await using var setup = fixture.CreateDbContext();
		var discovery = new OrganizationDiscoveryStore(new Factory(fixture), TimeProvider.System);
		var run = await discovery.StartAsync((await setup.CategoryAssistanceSettings.SingleAsync()).Revision, default);
		var pause = new PauseSave();
		var preferences = new CategoryAssistanceReviewPreferenceStore(new Factory(fixture, updateFirst ? pause : null), TimeProvider.System);
		var scanner = new OrganizationDiscoveryStore(new Factory(fixture, updateFirst ? null : pause), TimeProvider.System);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var request = new CategoryAssistanceReviewPreferenceRequest(Guid.NewGuid(), Guid.Empty, "later", [seed.Item]);
		Task first = updateFirst ? preferences.UpdateAsync(seed.Actor, seed.Attempt, request, timeout.Token)
			: scanner.DiscoverAsync(run, 100, timeout.Token);
		try
		{
			int holderPid = await pause.Arrived.Task.WaitAsync(timeout.Token);
			Task second = updateFirst ? scanner.DiscoverAsync(run, 100, timeout.Token)
				: preferences.UpdateAsync(seed.Actor, seed.Attempt, request, timeout.Token);
			await WaitForBlockedGate(fixture, holderPid, second, timeout.Token);
			pause.Release.TrySetResult();
			await Task.WhenAll(first, second);
			var manifest = await setup.OrganizationItems.AsNoTracking().SingleAsync(x => x.ItemId == seed.Item, timeout.Token);
			manifest.State.Should().Be(updateFirst ? "protected" : "observed");
			if (updateFirst) { manifest.SourceId.Should().BeNull(); }
			else { manifest.SourceId.Should().NotBeNull(); }
			// A preference saved after discovery need not invalidate the advisory manifest.
			// An executor must acquire the gate and re-read the committed preference itself.
			await using var executor = fixture.CreateDbContext();
			await using var tx = await executor.Database.BeginTransactionAsync(timeout.Token);
			await NormalizationWriteGuard.AcquireWriteGateAsync(executor, timeout.Token);
			var row = await executor.CategoryAssistanceReviewPreferences.AsNoTracking()
				.SingleAsync(x => x.Actor == seed.Actor && x.AttemptId == seed.Attempt, timeout.Token);
			row.State.Should().Be("later");
			CategoryAssistanceReviewPreferenceStore.Excluded(row).Should().Equal(seed.Item);
			await tx.CommitAsync(timeout.Token);
		}
		finally { pause.Release.TrySetResult(); timeout.Cancel(); }
	}

	[Fact]
	public async Task PreferenceUpdateAndHistoricalWriterSerializeBeforeReadingExclusions()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var preferences = new CategoryAssistanceReviewPreferenceStore(new Factory(fixture), TimeProvider.System);
		await using var blocker = new NpgsqlConnection(fixture.ConnectionString);
		await blocker.OpenAsync();
		await using var blockerTx = await blocker.BeginTransactionAsync();
		await using var lockAttempt = new NpgsqlCommand(
			"SELECT \"Id\" FROM category_assistance.\"Attempts\" WHERE \"Id\" = @id FOR UPDATE", blocker, blockerTx);
		lockAttempt.Parameters.AddWithValue("id", seed.Attempt);
		await lockAttempt.ExecuteScalarAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var update = preferences.UpdateAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), Guid.Empty, "later", [seed.Item]), timeout.Token);
		await using var waiting = new NpgsqlCommand(
			"SELECT EXISTS (SELECT 1 FROM pg_locks WHERE NOT granted AND pg_backend_pid() = ANY(pg_blocking_pids(pid)))", blocker, blockerTx);
		while (!(bool)(await waiting.ExecuteScalarAsync(timeout.Token))!)
		{
			if (update.IsCompleted) { await update; throw new InvalidOperationException("Update did not wait for attempt lock."); }
			await Task.Delay(20, timeout.Token);
		}
		await using var executor = fixture.CreateDbContext();
		await using var executorTx = await executor.Database.BeginTransactionAsync(timeout.Token);
		var acquire = NormalizationWriteGuard.AcquireWriteGateAsync(executor, timeout.Token);
		await using var gateWait = new NpgsqlCommand(
			"SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND pid = @pid)", blocker, blockerTx);
		gateWait.Parameters.AddWithValue("pid", ((NpgsqlConnection)executor.Database.GetDbConnection()).ProcessID);
		while (!(bool)(await gateWait.ExecuteScalarAsync(timeout.Token))!)
		{
			if (acquire.IsCompleted) { await acquire; throw new InvalidOperationException("Historical writer did not wait for preference update."); }
			await Task.Delay(20, timeout.Token);
		}
		await blockerTx.CommitAsync(timeout.Token);
		await update;
		await acquire;
		var row = await executor.CategoryAssistanceReviewPreferences.AsNoTracking()
			.SingleAsync(x => x.Actor == seed.Actor && x.AttemptId == seed.Attempt, timeout.Token);
		CategoryAssistanceReviewPreferenceStore.Excluded(row).Should().Equal(seed.Item);
		row.State.Should().Be("later");
		await executorTx.CommitAsync(timeout.Token);
	}

	[Fact]
	public async Task PreferenceUpdateWaitsForHistoricalWriterGate()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var preferences = new CategoryAssistanceReviewPreferenceStore(new Factory(fixture), TimeProvider.System);
		await using var executor = fixture.CreateDbContext();
		await using var executorTx = await executor.Database.BeginTransactionAsync();
		await NormalizationWriteGuard.AcquireWriteGateAsync(executor, CancellationToken.None);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var update = preferences.UpdateAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), Guid.Empty, "later", [seed.Item]), timeout.Token);
		await using var observer = new NpgsqlConnection(fixture.ConnectionString);
		await observer.OpenAsync(timeout.Token);
		await using var waiting = new NpgsqlCommand(
			"SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND @holder = ANY(pg_blocking_pids(pid)))", observer);
		waiting.Parameters.AddWithValue("holder", ((NpgsqlConnection)executor.Database.GetDbConnection()).ProcessID);
		while (!(bool)(await waiting.ExecuteScalarAsync(timeout.Token))!)
		{
			if (update.IsCompleted) { await update; throw new InvalidOperationException("Preference update did not wait for historical writer."); }
			await Task.Delay(20, timeout.Token);
		}
		(await executor.CategoryAssistanceReviewPreferences.AsNoTracking()
			.AnyAsync(x => x.Actor == seed.Actor && x.AttemptId == seed.Attempt, timeout.Token)).Should().BeFalse();
		await executorTx.CommitAsync(timeout.Token);
		var result = await update;
		result.ExcludedItemIds.Should().Equal(seed.Item);
	}

	[Fact]
	public async Task ReviewPreferencesRejectUnrelatedExcludedItemsWithoutRecordingAnOperation()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var preferences = new CategoryAssistanceReviewPreferenceStore(new Factory(fixture), TimeProvider.System);
		var operationId = Guid.NewGuid();
		var unrelatedItemId = Guid.NewGuid();
		var request = new CategoryAssistanceReviewPreferenceRequest(operationId, Guid.Empty,
			"later", [unrelatedItemId]);

		Func<Task> update = () => preferences.UpdateAsync(seed.Actor, seed.Attempt, request, CancellationToken.None);
		await update.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 409);
		(await preferences.GetAsync(seed.Actor, seed.Attempt, CancellationToken.None)).Should().BeEquivalentTo(
			new CategoryAssistanceReviewPreference(seed.Attempt, "active", Guid.Empty, Array.Empty<Guid>()));
		await using var db = fixture.CreateDbContext();
		(await db.CategoryAssistanceReviewOperations.CountAsync(x => x.Actor == seed.Actor &&
			x.OperationId == operationId)).Should().Be(0);
	}

	[Fact]
	public async Task ReviewPreferencesAreActorScopedRevisionedAndReplayDoesNotRevertNewerState()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var other = Guid.NewGuid().ToString();
		await using (var db = fixture.CreateDbContext())
		{
			db.Users.Add(new ApplicationUser
			{
				Id = other,
				UserName = other,
				NormalizedUserName = other.ToUpperInvariant()
			});
			await db.SaveChangesAsync();
		}
		var preferences = new CategoryAssistanceReviewPreferenceStore(new Factory(fixture), TimeProvider.System);
		var read = new CategoryAssistanceReadStore(new Factory(fixture), TimeProvider.System);
		var decisions = CreateStore(fixture);
		var laterRequest = new CategoryAssistanceReviewPreferenceRequest(Guid.NewGuid(), Guid.Empty,
			"later", [seed.Item]);
		var later = await preferences.UpdateAsync(seed.Actor, seed.Attempt, laterRequest, CancellationToken.None);
		later.State.Should().Be("later");
		await using (var verifySource = fixture.CreateDbContext())
		{
			var attempt = await verifySource.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.Attempt);
			var preference = await verifySource.CategoryAssistanceReviewPreferences.AsNoTracking().SingleAsync(x => x.Actor == seed.Actor && x.AttemptId == seed.Attempt);
			(preference.Location, preference.Description, preference.ItemCodeKey).Should().Be((attempt.Location, attempt.Description, attempt.ItemCodeKey));
		}
		(await preferences.GetAsync(other, seed.Attempt, CancellationToken.None)).State.Should().Be("active");
		(await read.ListSuggestionsAsync(seed.Actor, "active", 0, 100, CancellationToken.None)).Items
			.Should().NotContain(x => x.AttemptId == seed.Attempt);
		(await read.ListSuggestionsAsync(seed.Actor, "later", 0, 100, CancellationToken.None)).Items
			.Single(x => x.AttemptId == seed.Attempt).AffectedCount.Should().Be(0);
		(await read.GetSuggestionAsync(seed.Actor, seed.Attempt, CancellationToken.None))!.ReviewState
			.Should().Be("later");
		(await decisions.ListEligibleItemsAsync(seed.Actor, seed.Attempt, 0, 10, CancellationToken.None))
			.ItemIds.Should().BeEmpty();
		(await decisions.ListEligibleItemsAsync(other, seed.Attempt, 0, 10, CancellationToken.None))
			.ItemIds.Should().Equal(seed.Item);
		Func<Task> excludedAccept = () => decisions.AcceptAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), seed.Taxonomy, [seed.Item]), CancellationToken.None);
		await excludedAccept.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 409);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.Classifications.CountAsync(x => x.ReceiptItemId == seed.Item)).Should().Be(1);
		}

		Func<Task> overLimit = () => preferences.UpdateAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), later.Revision, "later", Enumerable.Range(0, 1001)
				.Select(_ => Guid.NewGuid()).ToArray()), CancellationToken.None);
		await overLimit.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 400);

		var retained = await preferences.UpdateAsync(seed.Actor, seed.Attempt,
				new(Guid.NewGuid(), later.Revision, "left_as_is", [seed.Item]), CancellationToken.None);
		retained.ExcludedItemIds.Should().Equal(seed.Item);
		(await decisions.ListEligibleItemsAsync(seed.Actor, seed.Attempt, 0, 10, CancellationToken.None))
				.ItemIds.Should().BeEmpty();

		var reopenRequest = new CategoryAssistanceReviewPreferenceRequest(Guid.NewGuid(), retained.Revision,
	"active", []);
		var reopened = await preferences.UpdateAsync(seed.Actor, seed.Attempt, reopenRequest, CancellationToken.None);
		(await decisions.ListEligibleItemsAsync(seed.Actor, seed.Attempt, 0, 10, CancellationToken.None))
				.ItemIds.Should().Equal(seed.Item);
		(await preferences.UpdateAsync(seed.Actor, seed.Attempt, laterRequest, CancellationToken.None))
			.Should().BeEquivalentTo(later);
		(await preferences.GetAsync(seed.Actor, seed.Attempt, CancellationToken.None)).Revision
			.Should().Be(reopened.Revision);
		Func<Task> stale = () => preferences.UpdateAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), later.Revision, "left_as_is", []), CancellationToken.None);
		await stale.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 409);
		Func<Task> wrongReplay = () => preferences.UpdateAsync(seed.Actor, seed.Attempt,
			laterRequest with { State = "left_as_is" }, CancellationToken.None);
		await wrongReplay.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 409);
		var left = await preferences.UpdateAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), reopened.Revision, "left_as_is", []), CancellationToken.None);
		left.State.Should().Be("left_as_is");
		(await read.ListSuggestionsAsync(other, "active", 0, 100, CancellationToken.None)).Items
			.Should().Contain(x => x.AttemptId == seed.Attempt);
		(await preferences.UpdateAsync(other, seed.Attempt, laterRequest, CancellationToken.None))
			.State.Should().Be("later");

		async Task<bool> CompeteAsync(string state)
		{
			try
			{
				await preferences.UpdateAsync(seed.Actor, seed.Attempt,
					new(Guid.NewGuid(), left.Revision, state, []), CancellationToken.None);
				return true;
			}
			catch (TaxonomyException error) when (error.Status == 409) { return false; }
		}
		(await Task.WhenAll(CompeteAsync("active"), CompeteAsync("later")))
			.Count(x => x).Should().Be(1);
		await decisions.AcceptAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), seed.Taxonomy, [seed.Item]), CancellationToken.None);
		(await preferences.UpdateAsync(seed.Actor, seed.Attempt, laterRequest, CancellationToken.None))
			.Should().BeEquivalentTo(later);
	}

	[Fact]
	public async Task AcceptAssignsSelectedItemAndReplayCannotOverwriteLaterManualChoice()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var store = CreateStore(fixture);
		var request = new CategoryAssistanceDecisionRequest(Guid.NewGuid(), seed.Taxonomy, [seed.Item]);

		Func<Task> mixedSelection = () => store.AcceptAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), seed.Taxonomy, [seed.Item, Guid.NewGuid()]), CancellationToken.None);
		await mixedSelection.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 409);
		await using (var before = fixture.CreateDbContext())
		{
			(await before.Classifications.CountAsync(x => x.ReceiptItemId == seed.Item)).Should().Be(1);
		}

		var accepted = await store.AcceptAsync(seed.Actor, seed.Attempt, request, CancellationToken.None);
		accepted.CategoryId.Should().Be(seed.Category);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.Classifications.CountAsync(x => x.ReceiptItemId == seed.Item)).Should().Be(2);
			var current = await db.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == seed.Item);
			var assignment = await db.Classifications.SingleAsync(x => x.Id == current.AssignmentId);
			assignment.CategoryId.Should().Be(seed.Category);
			assignment.Source.Should().Be("manual");
			var second = new PurchaseClassificationEntity
			{
				Id = Guid.NewGuid(),
				ReceiptItemId = seed.Item,
				Sequence = 3,
				PreviousAssignmentId = assignment.Id,
				CategoryId = null,
				TaxonomyRevisionId = seed.Taxonomy,
				RecordedAt = DateTimeOffset.UtcNow,
				Actor = seed.Actor,
				Source = "manual",
				Reason = "Later manual clear",
				OperationId = Guid.NewGuid()
			};
			db.Classifications.Add(second);
			current.AssignmentId = second.Id;
			await db.SaveChangesAsync();
		}

		(await store.AcceptAsync(seed.Actor, seed.Attempt, request, CancellationToken.None))
			.Should().BeEquivalentTo(accepted);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.Classifications.CountAsync(x => x.ReceiptItemId == seed.Item)).Should().Be(3);
			var current = await db.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == seed.Item);
			(await db.Classifications.SingleAsync(x => x.Id == current.AssignmentId)).Reason
				.Should().Be("Later manual clear");
		}
	}

	[Fact]
	public async Task LatestObservationControlsPageCountAndRejectsStaleAcceptance()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var store = CreateStore(fixture);
		var initialPage = await store.ListEligibleItemsAsync(seed.Attempt, 0, 10, CancellationToken.None);
		initialPage.ItemIds.Should().Equal(seed.Item);
		initialPage.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new
		{
			Id = seed.Item,
			Description = "Bread",
			Store = "Store",
			Date = new DateOnly(2026, 10, 26),
			Currency = "USD"
		}, options => options.ExcludingMissingMembers());
		await using (var db = fixture.CreateDbContext())
		{
			var first = await db.CategoryAssistanceObservations.SingleAsync();
			db.CategoryAssistanceObservations.Add(new CategoryAssistanceObservationEntity
			{
				AttemptId = seed.Attempt,
				SaveId = Guid.NewGuid(),
				ReceiptItemId = seed.Item,
				ReceiptId = first.ReceiptId,
				Actor = seed.Actor,
				Location = first.Location,
				Description = first.Description,
				ItemCode = first.ItemCode,
				SourceHash = first.SourceHash,
				ItemRevision = first.ItemRevision,
				ReceiptRevision = first.ReceiptRevision,
				AssignmentId = first.AssignmentId,
				SettingsRevision = first.SettingsRevision,
				RestoreGeneration = first.RestoreGeneration,
				TaxonomyRevision = first.TaxonomyRevision,
				Evidence = "excluded",
				RecordedAt = first.RecordedAt.AddSeconds(1)
			});
			await db.SaveChangesAsync();
		}
		var page = await store.ListEligibleItemsAsync(seed.Attempt, 0, 10, CancellationToken.None);
		page.Total.Should().Be(0);
		page.Items.Should().BeEmpty();
		var read = new CategoryAssistanceReadStore(new Factory(fixture), TimeProvider.System);
		(await read.ListSuggestionsAsync(0, 100, CancellationToken.None)).Items
			.Single(x => x.AttemptId == seed.Attempt).AffectedCount.Should().Be(0);
		Func<Task> accept = () => store.AcceptAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), seed.Taxonomy, [seed.Item]), CancellationToken.None);
		await accept.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 409);
		await using var check = fixture.CreateDbContext();
		(await check.Classifications.CountAsync(x => x.ReceiptItemId == seed.Item)).Should().Be(1);
	}

	[Theory]
	[InlineData("manual")]
	[InlineData("source")]
	[InlineData("mapping")]
	[InlineData("consent")]
	[InlineData("generation")]
	public async Task ChangedEvidenceRejectsAcceptanceWithoutAssignment(string change)
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		await using (var db = fixture.CreateDbContext())
		{
			switch (change)
			{
				case "manual":
					var current = await db.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == seed.Item);
					var manual = new PurchaseClassificationEntity
					{
						Id = Guid.NewGuid(),
						ReceiptItemId = seed.Item,
						Sequence = 2,
						PreviousAssignmentId = current.AssignmentId,
						CategoryId = seed.Category,
						TaxonomyRevisionId = seed.Taxonomy,
						RecordedAt = DateTimeOffset.UtcNow,
						Actor = seed.Actor,
						Source = "manual",
						Reason = "Manual override",
						OperationId = Guid.NewGuid()
					};
					db.Classifications.Add(manual); current.AssignmentId = manual.Id;
					break;
				case "source":
					(await db.ReceiptItems.SingleAsync(x => x.Id == seed.Item)).Description = "Changed";
					break;
				case "mapping":
					var source = NormalizationCatalogPolicy.Normalize(new("Store", "Bread", null));
					var fingerprint = NormalizationCatalogPolicy.Hash(source);
					var mapping = new CategoryMappingRevisionEntity
					{
						Id = Guid.NewGuid(),
						SourceFingerprint = fingerprint,
						Location = source.Location,
						Description = source.Description,
						CategoryId = seed.Category,
						TaxonomyRevisionId = seed.Taxonomy,
						Actor = seed.Actor,
						OperationId = Guid.NewGuid(),
						RequestHash = new string('C', 64),
						Reason = "Test mapping",
						RecordedAt = DateTimeOffset.UtcNow
					};
					db.CategoryMappingRevisions.Add(mapping);
					db.CategoryMappingHeads.Add(new CategoryMappingHeadEntity
					{ SourceFingerprint = fingerprint, RevisionId = mapping.Id });
					break;
				case "consent":
					(await db.CategoryAssistanceSettings.SingleAsync()).Enabled = false;
					break;
				case "generation":
					(await db.CategoryAssistanceConnection.SingleAsync()).Generation = Guid.NewGuid();
					break;
			}
			await db.SaveChangesAsync();
		}
		Func<Task> accept = () => CreateStore(fixture).AcceptAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), seed.Taxonomy, [seed.Item]), CancellationToken.None);
		await accept.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 409);
		await using var check = fixture.CreateDbContext();
		(await check.Classifications.CountAsync(x => x.ReceiptItemId == seed.Item))
			.Should().Be(change == "manual" ? 2 : 1);
		(await check.CategoryAssistanceAttempts.SingleAsync()).DecisionOperationId.Should().BeNull();
	}

	[Fact]
	public async Task RevokedCurrentMappingKeepsItemEligibleForListingCountAndAcceptance()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var source = NormalizationCatalogPolicy.Normalize(new("Store", "Bread", null));
		var fingerprint = NormalizationCatalogPolicy.Hash(source);
		var approvalId = Guid.NewGuid();
		var revocationId = Guid.NewGuid();
		await using (var db = fixture.CreateDbContext())
		{
			db.CategoryMappingRevisions.AddRange(
				new CategoryMappingRevisionEntity
				{
					Id = approvalId,
					SourceFingerprint = fingerprint,
					Location = source.Location,
					Description = source.Description,
					CategoryId = seed.Category,
					TaxonomyRevisionId = seed.Taxonomy,
					Actor = seed.Actor,
					OperationId = Guid.NewGuid(),
					RequestHash = new string('C', 64),
					Reason = "Approve",
					RecordedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
				},
				new CategoryMappingRevisionEntity
				{
					Id = revocationId,
					PreviousRevisionId = approvalId,
					SourceFingerprint = fingerprint,
					Location = source.Location,
					Description = source.Description,
					CategoryId = null,
					TaxonomyRevisionId = seed.Taxonomy,
					Actor = seed.Actor,
					OperationId = Guid.NewGuid(),
					RequestHash = new string('D', 64),
					Reason = "Revoke",
					RecordedAt = DateTimeOffset.UtcNow
				});
			db.CategoryMappingHeads.Add(new CategoryMappingHeadEntity
			{ SourceFingerprint = fingerprint, RevisionId = revocationId });
			await db.SaveChangesAsync();
		}

		var store = CreateStore(fixture);
		var page = await store.ListEligibleItemsAsync(seed.Attempt, 0, 10, CancellationToken.None);
		page.Total.Should().Be(1);
		page.ItemIds.Should().Equal(seed.Item);
		var read = new CategoryAssistanceReadStore(new Factory(fixture), TimeProvider.System);
		(await read.ListSuggestionsAsync(0, 100, CancellationToken.None)).Items
			.Single(x => x.AttemptId == seed.Attempt).AffectedCount.Should().Be(1);
		(await store.AcceptAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), seed.Taxonomy, [seed.Item]), CancellationToken.None))
			.CategoryId.Should().Be(seed.Category);
	}

	[Fact]
	public async Task DecisionMigrationDowngradeRefusesAndPreservesAcceptedEvidence()
	{
		await using var fixture = await server.CreateIsolatedAsync("20261027110503_RaiseCategoryAssistanceDailyLimit");
		var seed = await SeedAsync(fixture);
		await CreateStore(fixture).AcceptAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), seed.Taxonomy, [seed.Item]), CancellationToken.None);
		await using var db = fixture.CreateDbContext();
		var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
		const string migration = "20261026154337_AddCategoryAssistanceDecisions";
		var index = Array.IndexOf(applied, migration);
		index.Should().BeGreaterThan(0);
		var decision = await db.CategoryAssistanceAttempts.AsNoTracking()
			.SingleAsync(x => x.Id == seed.Attempt);
		try
		{
			Func<Task> downgrade = () => db.GetService<IMigrator>().MigrateAsync(applied[index - 1]);
			var refusal = (await downgrade.Should().ThrowAsync<PostgresException>()).Which;
			refusal.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
			refusal.MessageText.Should().Be(
				"Cannot downgrade: category assistance decision history must be retained.");
			// Later reversible migrations can be removed before this guarded downgrade fails.
			(await db.Database.GetAppliedMigrationsAsync()).Should().Contain(migration);
		}
		finally
		{
			await db.GetService<IMigrator>().MigrateAsync(applied[^1]);
		}
		var preserved = await db.CategoryAssistanceAttempts.AsNoTracking()
				.SingleAsync(x => x.Id == seed.Attempt);
		preserved.DecisionActor.Should().Be(decision.DecisionActor);
		preserved.DecisionOperationId.Should().Be(decision.DecisionOperationId);
		preserved.DecisionJson.Should().Be(decision.DecisionJson);
		preserved.State.Should().Be("accepted");
	}

	[Fact]
	public async Task InvalidProposedPathRejectsDefaultTargetButManualTargetCanBeChosen()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		await using (var db = fixture.CreateDbContext())
		{
			var attempt = await db.CategoryAssistanceAttempts.SingleAsync();
			attempt.ResultJson = JsonSerializer.Serialize(new CategoryAssistanceCompletion(
				CategoryAssistanceAttemptOutcome.Suggested,
				new CategorySuggestion(seed.Category.ToString(), [Guid.NewGuid().ToString(), seed.Category.ToString()],
					null, 0.9, 0.9, 0.9, new SuggestionProvenance("jev", CategorySuggestionPolicy.Model,
						CategorySuggestionPolicy.Version, seed.Taxonomy.ToString(), 1, 1))));
			await db.SaveChangesAsync();
		}
		var store = CreateStore(fixture);
		Func<Task> defaultAccept = () => store.AcceptAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), seed.Taxonomy, [seed.Item]), CancellationToken.None);
		await defaultAccept.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 409);
		(await store.AcceptAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), seed.Taxonomy, [seed.Item], seed.Category), CancellationToken.None))
			.CategoryId.Should().Be(seed.Category);
	}

	[Fact]
	public async Task DismissIsTerminalAndReplaysWithoutClassificationWrites()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var store = CreateStore(fixture);
		var operation = Guid.NewGuid();
		var dismissed = await store.DismissAsync(seed.Actor, seed.Attempt, operation, CancellationToken.None);
		(await store.DismissAsync(seed.Actor, seed.Attempt, operation, CancellationToken.None))
			.Should().BeEquivalentTo(dismissed);
		Func<Task> accept = () => store.AcceptAsync(seed.Actor, seed.Attempt,
			new(Guid.NewGuid(), seed.Taxonomy, [seed.Item]), CancellationToken.None);
		await accept.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 409);
		await using var db = fixture.CreateDbContext();
		(await db.Classifications.CountAsync(x => x.ReceiptItemId == seed.Item)).Should().Be(1);
	}

	private static CategoryAssistanceDecisionStore CreateStore(PostgresFixture fixture)
	{
		var capability = new Mock<ICategoryAssistanceCapabilitySource>();
		capability.Setup(x => x.GetAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CategoryAssistanceCapability("jev", CategoryAssistanceCapabilityState.Ready,
				[CategorySuggestionPolicy.Model]));
		return new(new Factory(fixture), capability.Object,
			Options.Create(new ClassificationOptions { WritesEnabled = true }), TimeProvider.System);
	}

	private static async Task<(string Actor, Guid Taxonomy, Guid Category, Guid Attempt, Guid Item)> SeedAsync(
		PostgresFixture fixture)
	{
		var actor = $"test-{Guid.NewGuid():N}";
		var taxonomy = Guid.NewGuid();
		var category = Guid.NewGuid();
		var receiptId = Guid.NewGuid();
		var itemId = Guid.NewGuid();
		var assignmentId = Guid.NewGuid();
		var attemptId = Guid.NewGuid();
		var source = NormalizationCatalogPolicy.Normalize(new("Store", "Bread", null));
		await using var db = fixture.CreateDbContext();
		db.Users.Add(new ApplicationUser { Id = actor, UserName = actor, NormalizedUserName = actor.ToUpperInvariant() });
		var revision = new TaxonomyRevisionEntity
		{
			Id = taxonomy,
			Sequence = 1,
			PreviousRevisionId = (await db.TaxonomyHeads.SingleAsync()).RevisionId,
			Actor = actor,
			Reason = "Test taxonomy",
			OperationId = Guid.NewGuid(),
			RecordedAt = DateTimeOffset.UtcNow
		};
		db.TaxonomyRevisions.Add(revision);
		db.TaxonomyIdentities.Add(new TaxonomyIdentityEntity { Id = category });
		db.TaxonomyNodes.Add(new TaxonomyNodeEntity { RevisionId = taxonomy, CategoryId = category, Name = "Food" });
		(await db.TaxonomyHeads.SingleAsync()).RevisionId = taxonomy;
		var settings = await db.CategoryAssistanceSettings.SingleAsync();
		settings.Enabled = true; settings.Provider = "jev"; settings.Model = CategorySuggestionPolicy.Model;
		var connection = await db.CategoryAssistanceConnection.SingleAsync();
		connection.Provider = "jev"; connection.Model = CategorySuggestionPolicy.Model;
		connection.ConfigurationFingerprint = new string('A', 64);
		connection.Generation = Guid.NewGuid(); connection.Changing = false;
		db.Receipts.Add(new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2026, 10, 26), TaxAmountCurrency = Currency.USD });
		db.ReceiptItems.Add(new ReceiptItemEntity
		{
			Id = itemId,
			ReceiptId = receiptId,
			Description = "Bread",
			UnitPriceCurrency = Currency.USD,
			TotalAmountCurrency = Currency.USD
		});
		await db.SaveChangesAsync();
		var itemRevision = await db.ReceiptItems.Where(x => x.Id == itemId).Select(x => x.NormalizationRevision).SingleAsync();
		var receiptRevision = await db.Receipts.Where(x => x.Id == receiptId).Select(x => x.NormalizationRevision).SingleAsync();
		db.Classifications.Add(new PurchaseClassificationEntity
		{
			Id = assignmentId,
			ReceiptItemId = itemId,
			Sequence = 1,
			TaxonomyRevisionId = taxonomy,
			RecordedAt = DateTimeOffset.UtcNow,
			Actor = actor,
			Source = "legacy_import",
			Reason = "Unresolved legacy observation",
			OperationId = Guid.NewGuid()
		});
		db.CurrentClassifications.Add(new CurrentClassificationEntity { ReceiptItemId = itemId, AssignmentId = assignmentId });
		db.LegacyMigrations.Add(new LegacyMigrationEntity
		{
			Actor = actor,
			OperationId = Guid.NewGuid(),
			OperationKind = LegacyTaxonomyMigrationService.InitialOperationKind,
			SourceDataset = "test",
			RequestHash = new string('B', 64),
			RevisionId = taxonomy,
			ObservedAt = DateTimeOffset.UtcNow
		});
		db.CategoryAssistanceAttempts.Add(new CategoryAssistanceAttemptEntity
		{
			Id = attemptId,
			RequestId = Guid.NewGuid(),
			RestoreGeneration = settings.RestoreGeneration,
			SourceHash = NormalizationCatalogPolicy.Hash(source),
			Location = source.Location,
			Description = source.Description,
			ItemCodeKey = "",
			SourceRevision = itemRevision,
			TaxonomyRevision = taxonomy,
			Provider = "jev",
			Model = CategorySuggestionPolicy.Model,
			QuestionPolicyVersion = CategorySuggestionPolicy.Version,
			SettingsRevision = settings.Revision,
			ConnectionGeneration = connection.Generation,
			ReceiptId = receiptId,
			ReceiptRevision = receiptRevision,
			ItemId = itemId,
			ItemRevision = itemRevision,
			AssignmentId = assignmentId,
			State = "suggested",
			CreatedAt = DateTimeOffset.UtcNow,
			CompletedAt = DateTimeOffset.UtcNow,
			ResultJson = JsonSerializer.Serialize(new CategoryAssistanceCompletion(
				CategoryAssistanceAttemptOutcome.Suggested,
				new CategorySuggestion(category.ToString(), [category.ToString()], null, 0.9, 0.9, 0.9,
					new SuggestionProvenance("jev", CategorySuggestionPolicy.Model,
						CategorySuggestionPolicy.Version, taxonomy.ToString(), 1, 1))))
		});
		db.CategoryAssistanceObservations.Add(new CategoryAssistanceObservationEntity
		{
			AttemptId = attemptId,
			SaveId = Guid.NewGuid(),
			ReceiptItemId = itemId,
			ReceiptId = receiptId,
			Actor = actor,
			Location = source.Location,
			Description = source.Description,
			SourceHash = NormalizationCatalogPolicy.Hash(source),
			ItemRevision = itemRevision,
			ReceiptRevision = receiptRevision,
			AssignmentId = assignmentId,
			SettingsRevision = settings.Revision,
			RestoreGeneration = settings.RestoreGeneration,
			TaxonomyRevision = taxonomy,
			RecordedAt = DateTimeOffset.UtcNow
		});
		await db.SaveChangesAsync();
		return (actor, taxonomy, category, attemptId, itemId);
	}
}
