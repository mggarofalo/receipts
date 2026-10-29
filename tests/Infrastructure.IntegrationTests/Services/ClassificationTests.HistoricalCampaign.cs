using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task HistoricalDispositionWaitsForRestoreWriteGate()
	{
		var group = new HistoricalGroupKey("gate test", Guid.NewGuid().ToString(), null);
		await using var db = fixture.CreateDbContext();
		await using var transaction = await db.Database.BeginTransactionAsync();
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, default);
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
		Func<Task> write = () => Historical().SetDispositionAsync(Actor,
				new(group, HistoricalGroupDisposition.Later, 0, Guid.NewGuid()), cancellation.Token);
		await write.Should().ThrowAsync<OperationCanceledException>();
		await transaction.CommitAsync();
		var saved = await Historical().SetDispositionAsync(Actor,
				new(group, HistoricalGroupDisposition.Later, 0, Guid.NewGuid()));
		saved.Version.Should().Be(1);
	}

	[Fact]
	public async Task HistoricalDispositionOlderReplayKeepsOriginalResultAndLatestState()
	{
		var seed = await HistoricalSeedAsync();
		HistoricalGroupKey group = (await HistoricalRequest(seed.Item, seed.Revision, seed.Root))
				.Groups.Single().Group;
		Guid firstId = Guid.NewGuid();
		HistoricalDispositionRecord first = await Historical().SetDispositionAsync(Actor,
				new(group, HistoricalGroupDisposition.Later, 0, firstId));
		HistoricalDispositionRecord latest = await Historical().SetDispositionAsync(Actor,
				new(group, HistoricalGroupDisposition.LeaveAsIs, 1, Guid.NewGuid()));

		HistoricalDispositionRecord replay = await Historical().SetDispositionAsync(Actor,
				new(group, HistoricalGroupDisposition.Later, 0, firstId));
		replay.Should().Be(first);
		Func<Task> changedIntent = () => Historical().SetDispositionAsync(Actor,
				new(group, HistoricalGroupDisposition.Later, 1, firstId));
		(await changedIntent.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		Func<Task> changedActor = () => Historical().SetDispositionAsync("another actor",
				new(group, HistoricalGroupDisposition.Later, 0, firstId));
		(await changedActor.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);

		HistoricalGroupPage current = await new HistoricalGroupReader(new Factory(fixture))
				.BrowseAsync(new(Search: group.RawDescription, Actor: Actor,
						DispositionFilter: HistoricalDispositionFilter.All));
		current.Items.Single(x => x.Group == group).Disposition.Should().Be(latest.Disposition);
		current.Items.Single(x => x.Group == group).DispositionVersion.Should().Be(latest.Version);
		(await Historical().SetDispositionAsync(Actor,
				new(group, null, latest.Version, Guid.NewGuid()))).Version.Should().Be(3);
	}

	[Fact]
	public async Task HistoricalDispositionConcurrentCrossSourceOperationIdHasSingleWinner()
	{
		var seed = await HistoricalSeedAsync();
		HistoricalGroupKey firstGroup = (await HistoricalRequest(seed.Item, seed.Revision, seed.Root))
				.Groups.Single().Group;
		HistoricalGroupKey secondGroup = firstGroup with { ItemCode = "another exact source" };
		Guid operationId = Guid.NewGuid();
		Task<HistoricalDispositionRecord> first = Historical().SetDispositionAsync(Actor,
				new(firstGroup, HistoricalGroupDisposition.Later, 0, operationId));
		Task<HistoricalDispositionRecord> second = Historical().SetDispositionAsync(Actor,
				new(secondGroup, HistoricalGroupDisposition.LeaveAsIs, 0, operationId));
		await Task.WhenAll(first.ContinueWith(_ => { }), second.ContinueWith(_ => { }));
		Task<HistoricalDispositionRecord>[] writes = [first, second];
		writes.Count(x => x.IsCompletedSuccessfully).Should().Be(1);
		writes.Count(x => x.Exception?.GetBaseException() is TaxonomyException { Status: 409 })
				.Should().Be(1);
		HistoricalDispositionRecord winner = await writes.Single(x => x.IsCompletedSuccessfully);
		(await Historical().SetDispositionAsync(Actor,
				new(winner.Group, winner.Disposition, 0, operationId))).Should().Be(winner);
	}

	[Fact]
	public async Task HistoricalCampaignResumesOwnOperationsAndKeepsReviewDispositionsActorPrivate()
	{
		var seed = await HistoricalSeedAsync();
		HistoricalPreviewRequest preview = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(preview);
		HistoricalOperationPage mine = await Historical().ListAsync(Actor, 0, 25);
		HistoricalOperationSummary summary = mine.Items.Single(x => x.OperationId == preview.OperationId);
		summary.Groups.Should().ContainSingle().Which.Group.Should().Be(preview.Groups.Single().Group);
		summary.Progress.Pending.Should().Be(1);
		HistoricalOperationSummary byId = await Historical().ReadSummaryAsync(Actor, preview.OperationId);
		byId.Groups.Should().BeEquivalentTo(preview.Groups);
		byId.Reason.Should().Be(preview.Reason);
		byId.Progress.Should().BeEquivalentTo(summary.Progress);
		Func<Task> readOtherActor = () => Historical().ReadSummaryAsync("another actor", preview.OperationId);
		(await readOtherActor.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(404);
		(await Historical().ListAsync("another actor", 0, 25)).Items
				.Should().NotContain(x => x.OperationId == preview.OperationId);

		HistoricalGroupKey group = preview.Groups.Single().Group;
		var browse = new HistoricalGroupReader(new Factory(fixture));
		Guid operationId = Guid.NewGuid();
		var later = await Historical().SetDispositionAsync(Actor,
				new(group, HistoricalGroupDisposition.Later, 0, operationId));
		later.Version.Should().Be(1);
		var replay = await Historical().SetDispositionAsync(Actor,
				new(group, HistoricalGroupDisposition.Later, 0, operationId));
		replay.Version.Should().Be(later.Version);
		replay.OperationId.Should().Be(later.OperationId);
		(await browse.BrowseAsync(new(Search: group.RawDescription, Actor: Actor))).Items
				.Should().NotContain(x => x.Group == group);
		(await browse.BrowseAsync(new(Search: group.RawDescription, Actor: "another actor"))).Items
				.Should().Contain(x => x.Group == group);
		(await browse.BrowseAsync(new(Search: group.RawDescription, Actor: Actor,
				DispositionFilter: HistoricalDispositionFilter.Later))).Items
				.Should().ContainSingle(x => x.Group == group && x.DispositionVersion == 1);

		Func<Task> stale = () => Historical().SetDispositionAsync(Actor,
				new(group, HistoricalGroupDisposition.LeaveAsIs, 0, Guid.NewGuid()));
		(await stale.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		var left = await Historical().SetDispositionAsync(Actor,
				new(group, HistoricalGroupDisposition.LeaveAsIs, 1, Guid.NewGuid()));
		left.Version.Should().Be(2);
		var reopened = await Historical().SetDispositionAsync(Actor,
				new(group, null, 2, Guid.NewGuid()));
		reopened.Disposition.Should().BeNull();
		(await browse.BrowseAsync(new(Search: group.RawDescription, Actor: Actor))).Items
				.Should().Contain(x => x.Group == group && x.DispositionVersion == 3);
		(await Historical().ReadAsync(Actor, preview.OperationId)).Pending.Should().Be(1);
	}

	[Fact]
	public async Task HistoricalDispositionConcurrentFirstWritesRespectCasAndNullableExactSource()
	{
		var seed = await HistoricalSeedAsync();
		await using (var db = fixture.CreateDbContext())
		{
			ReceiptItemEntity original = await db.ReceiptItems.AsNoTracking()
					.SingleAsync(x => x.Id == seed.Item);
			original.ReceiptItemCode = null;
			await new ReceiptItemRepository(new ActorFactory(fixture)).UpdateAsync([original], default);
		}
		HistoricalGroupKey nullCode = (await HistoricalRequest(seed.Item, seed.Revision, seed.Root))
				.Groups.Single().Group;
		Guid receiptId;
		await using (var db = fixture.CreateDbContext())
		{ receiptId = (await db.ReceiptItems.SingleAsync(x => x.Id == seed.Item)).ReceiptId; }
		ReceiptItemEntity emptyCodeItem = ReceiptItemEntityGenerator.Generate(receiptId);
		emptyCodeItem.Description = nullCode.RawDescription;
		emptyCodeItem.ReceiptItemCode = "";
		await new ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync([emptyCodeItem], default);
		HistoricalGroupKey emptyCode = nullCode with { ItemCode = "" };

		var firstId = Guid.NewGuid();
		var secondId = Guid.NewGuid();
		Task<HistoricalDispositionRecord> first = Historical().SetDispositionAsync(Actor,
				new(nullCode, HistoricalGroupDisposition.Later, 0, firstId));
		Task<HistoricalDispositionRecord> second = Historical().SetDispositionAsync(Actor,
				new(nullCode, HistoricalGroupDisposition.LeaveAsIs, 0, secondId));
		await Task.WhenAll(first.ContinueWith(_ => { }), second.ContinueWith(_ => { }));
		Task<HistoricalDispositionRecord>[] writes = [first, second];
		writes.Count(x => x.IsCompletedSuccessfully).Should().Be(1);
		writes.Count(x => x.Exception?.GetBaseException() is TaxonomyException { Status: 409 })
				.Should().Be(1);
		HistoricalDispositionRecord winner = await writes.Single(x => x.IsCompletedSuccessfully);
		(await Historical().SetDispositionAsync(Actor,
				new(nullCode, winner.Disposition, 0, winner.OperationId))).Version.Should().Be(1);

		var browse = new HistoricalGroupReader(new Factory(fixture));
		HistoricalGroupPage mine = await browse.BrowseAsync(new(Search: nullCode.RawDescription,
				Actor: Actor, DispositionFilter: HistoricalDispositionFilter.All));
		mine.Items.Single(x => x.Group == nullCode).Disposition.Should().Be(winner.Disposition);
		mine.Items.Single(x => x.Group == emptyCode).Disposition.Should().BeNull();
		HistoricalGroupPage other = await browse.BrowseAsync(new(Search: nullCode.RawDescription,
				Actor: "another actor", DispositionFilter: HistoricalDispositionFilter.All));
		other.Items.Single(x => x.Group == nullCode).Disposition.Should().BeNull();
		other.Items.Single(x => x.Group == emptyCode).Disposition.Should().BeNull();
	}
}
