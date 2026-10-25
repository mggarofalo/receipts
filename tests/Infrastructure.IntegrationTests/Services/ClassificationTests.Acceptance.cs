using System.Data.Common;
using Application.Normalization;
using Application.Taxonomy;
using Domain.NormalizedDescriptions;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task DeepAssignmentSurvivesUnchangedLegacyEchoAfterTaxonomyRename()
	{
		var seed = await SeedAsync();
		Guid leaf = Guid.NewGuid();
		var deep = await Tree().ChangeAsync(Actor, new(seed.Revision, Guid.NewGuid(), "Add deep node",
				[new TaxonomyEdit("create", leaf, seed.Child, "Deep leaf")]));
		await using var db = fixture.CreateDbContext();
		var item = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
		string rootName = deep.Nodes.Single(x => x.Id == seed.Root).Name;
		item.Category = rootName; item.Subcategory = "Child";
		item.Classification = new(leaf, deep.Id, seed.Assignment, Guid.NewGuid(), "Explicit deep choice");
		var repository = new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture),
				Options.Create(new ClassificationOptions { WritesEnabled = true }));
		await repository.UpdateAsync([item], Guid.NewGuid(), default);
		var original = (await Service().HistoryAsync(item.Id, null, 1)).Items.Single();
		original.Path.Select(x => x.Id).Should().Equal(seed.Root, seed.Child, leaf);
		var renamed = await Tree().ChangeAsync(Actor, new(deep.Id, Guid.NewGuid(), "Rename current root",
				[new TaxonomyEdit("rename", seed.Root, Name: "Renamed " + Guid.NewGuid())]));
		item.Classification = null; item.Description = "Fact edit retaining old labels";
		item.UnitPrice += 1; item.TotalAmount = decimal.Round(item.Quantity * item.UnitPrice, 2, MidpointRounding.AwayFromZero);
		await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).UpdateAsync([item], default);
		var history = await Service().HistoryAsync(item.Id, null, 10);
		history.Items.Should().HaveCount(2);
		history.Items.First().Should().BeEquivalentTo(original);
		var stored = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == item.Id);
		stored.Category.Should().Be(rootName); stored.Subcategory.Should().Be("Child");
		stored.Description.Should().Be(item.Description); stored.TotalAmount.Should().Be(item.TotalAmount);
		(await db.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == item.Id)).AssignmentId.Should().Be(original.Id);

		// A changed lossy path must not infer the third-level target from the leaf's name.
		item.Category = renamed.Nodes.Single(x => x.Id == seed.Root).Name;
		item.Subcategory = "Deep leaf"; item.Description = "Rejected lossy edit";
		Func<Task> rejected = () => repository.UpdateAsync([item], default);
		(await rejected.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		(await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == item.Id)).Description.Should().Be(stored.Description);
		(await Service().HistoryAsync(item.Id, null, 10)).Items.Should().BeEquivalentTo(history.Items);
	}

	[Fact]
	public async Task LegacyCommitWinsAgainstExplicitAssignmentWaitingOnHeadWithoutLosingEitherHistory()
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		var item = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
		item.Category = await db.TaxonomyNodes.Where(x => x.RevisionId == seed.Revision && x.CategoryId == seed.Root).Select(x => x.Name).SingleAsync();
		item.Subcategory = "Child"; item.Description = "Committed legacy edit";
		var barrier = new CommitBarrier();
		var legacy = new Infrastructure.Repositories.ReceiptItemRepository(new BarrierFactory(fixture, barrier)).UpdateAsync([item], default);
		int blocker = await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
		var explicitWrite = Service().AssignAsync(Actor, item.Id,
				new(seed.Root, seed.Revision, seed.Assignment, Guid.NewGuid(), "Competing explicit choice"));
		try { await WaitForDatabaseBlockAsync(blocker); explicitWrite.IsCompleted.Should().BeFalse(); }
		finally { barrier.Release.TrySetResult(); }
		await legacy;
		Func<Task> rejected = async () => { await explicitWrite; };
		(await rejected.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		var history = await Service().HistoryAsync(item.Id, null, 10);
		history.Items.Should().HaveCount(2);
		history.Items.First().Source.Should().Be("legacy_write");
		history.Items.First().CategoryId.Should().Be(seed.Child);
		history.Items.First().PreviousAssignmentId.Should().Be(seed.Assignment);
		(await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == item.Id)).Description.Should().Be(item.Description);
		(await db.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == item.Id)).AssignmentId.Should().Be(history.Items.First().Id);
	}

	private sealed class CommitBarrier : DbTransactionInterceptor
	{
		public TaskCompletionSource<int> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
			TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
		{
			Entered.TrySetResult(((NpgsqlConnection)transaction.Connection!).ProcessID);
			await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
			return result;
		}
	}
	private sealed class BarrierFactory(PostgresFixture fixture, CommitBarrier barrier) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
			.AddInterceptors(barrier).Options, new ActorAccessor());
	}
	private async Task WaitForDatabaseBlockAsync(int blocker)
	{
		await using var connection = new NpgsqlConnection(fixture.ConnectionString);
		await connection.OpenAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (true)
		{
			await using var query = new NpgsqlCommand("SELECT EXISTS (SELECT FROM pg_stat_activity WHERE @pid = ANY(pg_blocking_pids(pid)))", connection);
			query.Parameters.AddWithValue("pid", blocker);
			if ((bool)(await query.ExecuteScalarAsync(timeout.Token))!) { return; }
			await Task.Delay(20, timeout.Token);
		}
	}
	[Fact]
	public async Task TaxonomyCommitWinsAgainstAssignmentAlreadyWaitingOnItsLock()
	{
		var seed = await SeedAsync();
		var barrier = new CommitBarrier();
		var edit = new TaxonomyService(new BarrierFactory(fixture, barrier), TimeProvider.System).ChangeAsync(Actor,
			new(seed.Revision, Guid.NewGuid(), "Controlled archive", [new TaxonomyEdit("archive", seed.Root)]));
		int blocker = await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
		Task<PurchaseClassification> assignment = Service().AssignAsync(Actor, seed.Item,
						new(seed.Child, seed.Revision, seed.Assignment, Guid.NewGuid(), "Blocked stale choice"));
		try { await WaitForDatabaseBlockAsync(blocker); assignment.IsCompleted.Should().BeFalse(); }
		finally { barrier.Release.TrySetResult(); }
		var revision = await edit;
		Func<Task> rejected = async () => { await assignment; };
		(await rejected.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		await using var db = fixture.CreateDbContext();
		(await db.Classifications.Where(x => x.ReceiptItemId == seed.Item).Select(x => x.Id).ToListAsync()).Should().Equal(seed.Assignment);
		(await db.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == seed.Item)).AssignmentId.Should().Be(seed.Assignment);
		(await Tree().ReadAsync(null))!.Id.Should().Be(revision.Id);
	}
	[Fact]
	public async Task ReceiptDeleteCommitWinsAgainstAssignmentAlreadyWaitingOnItsGate()
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		Guid receiptId = await db.ReceiptItems.Where(x => x.Id == seed.Item).Select(x => x.ReceiptId).SingleAsync();
		var barrier = new CommitBarrier();
		var deletion = new Infrastructure.Repositories.ReceiptRepository(new BarrierFactory(fixture, barrier)).DeleteAsync([receiptId], default);
		int blocker = await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
		Task<PurchaseClassification> assignment = Service().AssignAsync(Actor, seed.Item,
						new(seed.Child, seed.Revision, seed.Assignment, Guid.NewGuid(), "Blocked deleted choice"));
		try { await WaitForDatabaseBlockAsync(blocker); assignment.IsCompleted.Should().BeFalse(); }
		finally { barrier.Release.TrySetResult(); }
		await deletion;
		Func<Task> rejected = async () => { await assignment; };
		(await rejected.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(404);
		(await db.Classifications.Where(x => x.ReceiptItemId == seed.Item).Select(x => x.Id).ToListAsync()).Should().Equal(seed.Assignment);
		(await db.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == seed.Item)).AssignmentId.Should().Be(seed.Assignment);
		(await db.Receipts.AnyAsync(x => x.Id == receiptId)).Should().BeFalse();
	}
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CompleteReceiptCombinesClassificationAndNormalizationWithReplayOrCommitRollback(bool failCommit)
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		var receiptEntity = ReceiptEntityGenerator.Generate();
		var itemEntity = ReceiptItemEntityGenerator.Generate(receiptEntity.Id);
		itemEntity.Description = "Combined source " + Guid.NewGuid();
		itemEntity.Category = await db.TaxonomyNodes.Where(x => x.RevisionId == seed.Revision && x.CategoryId == seed.Root).Select(x => x.Name).SingleAsync();
		itemEntity.Subcategory = null;
		var receiptMapper = new Infrastructure.Mapping.ReceiptMapper();
		var itemMapper = new Infrastructure.Mapping.ReceiptItemMapper();
		var receipt = receiptMapper.ToDomain(receiptEntity);
		var item = itemMapper.ToDomain(itemEntity);
		item.Classification = new(seed.Root, seed.Revision, null, Guid.NewGuid(), "Combined explicit choice");
		string name = "Combined product " + Guid.NewGuid();
		item.NormalizationIntent = new(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(),
			NormalizationCatalogPolicy.Hash(new(receipt.Location, item.Description, item.ReceiptItemCode)), NewName: name, RememberSource: true);
		IDbContextFactory<ApplicationDbContext> factory = failCommit
			? new FailureFactory(fixture) : new ActorFactory(fixture);
		var writer = new CompleteReceiptWriter(factory, receiptMapper, new Infrastructure.Mapping.TransactionMapper(), itemMapper,
			new Infrastructure.Mapping.AdjustmentMapper(), Options.Create(new ClassificationOptions { WritesEnabled = true }));
		Guid requestId = Guid.NewGuid();
		int receiptCount = await db.Receipts.CountAsync();
		int assignmentCount = await db.Classifications.CountAsync();
		int mappingCount = await db.NormalizationSourceMappings.CountAsync();
		int auditCount = await db.AuditLogs.CountAsync();
		if (failCommit)
		{
			Func<Task> save = async () => { await writer.CreateAsync(receipt, [], [item], [], requestId, default); };
			await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected classification commit failure");
			(await db.Receipts.CountAsync()).Should().Be(receiptCount);
			(await db.Classifications.CountAsync()).Should().Be(assignmentCount);
			(await db.NormalizationSourceMappings.CountAsync()).Should().Be(mappingCount);
			(await db.NormalizedDescriptions.AnyAsync(x => x.CanonicalName == name)).Should().BeFalse();
			(await db.AuditLogs.CountAsync()).Should().Be(auditCount);
			return;
		}
		var saved = await writer.CreateAsync(receipt, [], [item], [], requestId, default);
		var replay = await writer.CreateAsync(receipt, [], [item], [], requestId, default);
		replay.Receipt.Id.Should().Be(saved.Receipt.Id);
		replay.Items.Single().Id.Should().Be(saved.Items.Single().Id);
		var stored = await db.ReceiptItems.SingleAsync(x => x.Id == saved.Items.Single().Id);
		stored.Description.Should().Be(item.Description);
		stored.TotalAmount.Should().Be(item.TotalAmount.Amount);
		stored.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		var identity = await db.NormalizedDescriptions.SingleAsync(x => x.CanonicalName == name);
		stored.NormalizedDescriptionId.Should().Be(identity.Id);
		(await db.NormalizationSourceMappings.CountAsync()).Should().Be(mappingCount + 1);
		var history = await Service().HistoryAsync(stored.Id, null, 10);
		history.Items.Should().ContainSingle().Which.CategoryId.Should().Be(seed.Root);
		(await db.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == stored.Id)).AssignmentId.Should().Be(history.Items.Single().Id);
		(await db.Receipts.CountAsync()).Should().Be(receiptCount + 1);
		(await db.Classifications.CountAsync()).Should().Be(assignmentCount + 1);
	}
	private sealed class FailCommit : DbTransactionInterceptor
	{
		public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
			InterceptionResult result, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Injected classification commit failure");
	}
	private sealed class FailureFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
			.AddInterceptors(new FailCommit()).Options, new ActorAccessor());
	}
}


