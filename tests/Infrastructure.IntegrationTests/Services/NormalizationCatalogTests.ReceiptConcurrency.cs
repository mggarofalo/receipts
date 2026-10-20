using Application.Commands.Receipt.CreateComplete;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task ReceiptConcurrency_VectorOnlyPersistenceDoesNotAcquireReviewGateAfterRowLock()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Reviewed vector target");
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting,
			Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision))], [], CancellationToken.None);
		var id = saved.Items.Single().Id;
		var before = (await ReadAsync([id])).Single();
		await using var external = new NpgsqlConnection(fixture.ConnectionString);
		await external.OpenAsync();
		await using var gateTransaction = await external.BeginTransactionAsync();
		await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1380143188, 1313821261)", external, gateTransaction);
		await gate.ExecuteNonQueryAsync();
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await using (var writer = fixture.CreateDbContext())
		{
			await using var writeTransaction = await writer.Database.BeginTransactionAsync(deadline.Token);
			await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM receipts.\"ReceiptItems\" WHERE \"Id\"={id} FOR UPDATE", deadline.Token);
			var canonical = await writer.NormalizedDescriptions.SingleAsync(x => x.Id == target.Id, deadline.Token);
			var values = new float[Infrastructure.Services.OnnxEmbeddingService.EmbeddingDimension];
			values[0] = 1;
			canonical.Embedding = new Pgvector.Vector(values);
			canonical.EmbeddingModelVersion = Infrastructure.Services.OnnxEmbeddingService.EmbeddingSpaceFingerprint;
			writer.ItemEmbeddings.Add(new Infrastructure.Entities.Core.ItemEmbeddingEntity
			{
				Id = Guid.NewGuid(),
				EntityId = id,
				EntityType = "ReceiptItem",
				EntityText = Source.Description,
				Embedding = new Pgvector.Vector(values),
				ModelVersion = Infrastructure.Services.OnnxEmbeddingService.EmbeddingSpaceFingerprint,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await writer.SaveChangesAsync(deadline.Token);
			await writeTransaction.CommitAsync(deadline.Token);
		}
		await gateTransaction.RollbackAsync();
		var after = (await ReadAsync([id])).Single();
		after.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		after.NormalizationReviewDecisionId.Should().Be(before.NormalizationReviewDecisionId);
		after.NormalizationRevision.Should().Be(before.NormalizationRevision);
		await using var verify = fixture.CreateDbContext();
		(await verify.ItemEmbeddings.SingleAsync(x => x.EntityId == id)).EntityText.Should().Be(Source.Description);
		(await verify.NormalizationReviewWork.AnyAsync()).Should().BeFalse();
	}

	[Fact]
	public async Task ReceiptConcurrency_IndependentDatabaseGateBlocksSaveAndCancellationLeavesRequestRetryable()
	{
		await ResetAsync();
		await using var external = new NpgsqlConnection(fixture.ConnectionString);
		await external.OpenAsync();
		await using var transaction = await external.BeginTransactionAsync();
		await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1380143188, 1313821261)", external, transaction);
		await gate.ExecuteNonQueryAsync();
		var requestId = Guid.NewGuid();
		var intent = new ReceiptNormalizationIntent(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), NewName: "Serialized new product", RememberSource: true);
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
		var save = EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(intent)], [], requestId, cancellation.Token);
		bool observed = false;
		try
		{
			await using var waiting = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pg_backend_pid() = ANY(pg_blocking_pids(pid)))", external, transaction);
			while (!save.IsCompleted && !observed)
			{
				observed = (bool)(await waiting.ExecuteScalarAsync(deadline.Token))!;
				if (!observed) { await Task.Delay(20, deadline.Token); }
			}
			observed.Should().BeTrue("receipt entry must coordinate across database connections, not just an in-process lock");
			cancellation.Cancel();
			Func<Task> cancelled = async () => await save;
			await cancelled.Should().ThrowAsync<OperationCanceledException>();
		}
		finally
		{
			cancellation.Cancel();
			await transaction.RollbackAsync();
			try { await save; } catch (OperationCanceledException) { }
		}
		await using (var beforeRetry = fixture.CreateDbContext())
		{
			(await beforeRetry.Receipts.CountAsync()).Should().Be(0);
			(await beforeRetry.NormalizedDescriptions.CountAsync()).Should().Be(0);
			(await beforeRetry.NormalizationSourceMappings.CountAsync()).Should().Be(0);
		}

		var retried = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(intent)], [], requestId, CancellationToken.None);
		retried.Items.Should().ContainSingle();
		await using var db = fixture.CreateDbContext();
		(await db.Receipts.CountAsync()).Should().Be(1);
		(await db.NormalizedDescriptions.CountAsync()).Should().Be(1);
	}

	[Fact]
	public async Task ReceiptConcurrency_SimultaneousReplayUsesOneReceiptAndOneReviewedIdentity()
	{
		await ResetAsync();
		var requestId = Guid.NewGuid();
		var intent = new ReceiptNormalizationIntent(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), NewName: "One reviewed product", RememberSource: true);
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var first = EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(intent)], [], requestId, deadline.Token);
		var second = EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(intent)], [], requestId, deadline.Token);

		var results = await Task.WhenAll(first, second);

		results[0].Receipt.Id.Should().Be(results[1].Receipt.Id);
		results[0].Items.Single().Id.Should().Be(results[1].Items.Single().Id);
		await using var db = fixture.CreateDbContext();
		(await db.Receipts.CountAsync()).Should().Be(1);
		(await db.ReceiptItems.CountAsync()).Should().Be(1);
		(await db.NormalizedDescriptions.CountAsync()).Should().Be(1);
		(await db.NormalizationSourceMappings.CountAsync()).Should().Be(1);
		(await db.AuditLogs.CountAsync(x => x.EntityType == "ReceiptItem" && x.EntityId == results[0].Items.Single().Id.ToString())).Should().Be(1);
	}

	[Fact]
	public async Task ReceiptConcurrency_IncompatibleRememberedDecisionsCannotBothCommit()
	{
		await ResetAsync();
		var beef = await CanonicalAsync("Beef franks");
		var chicken = await CanonicalAsync("Chicken franks");
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var outcomes = await Task.WhenAll(new[] { beef, chicken }.Select(async target =>
		{
			try
			{
				var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting,
					Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision, RememberSource: true))], [], deadline.Token);
				return (Saved: (CreateCompleteReceiptResult?)saved, Error: (NormalizationCatalogException?)null);
			}
			catch (NormalizationCatalogException error) { return (Saved: (CreateCompleteReceiptResult?)null, Error: error); }
		}));

		var winner = outcomes.Should().ContainSingle(x => x.Saved != null).Subject.Saved!;
		outcomes.Should().ContainSingle(x => x.Error != null).Subject.Error!.Code.Should().Be("normalization_mapping_conflict");
		await using var db = fixture.CreateDbContext();
		(await db.Receipts.CountAsync()).Should().Be(1);
		(await db.ReceiptItems.SingleAsync()).NormalizedDescriptionId.Should().Be(winner.Items.Single().NormalizedDescriptionId);
		(await db.NormalizationSourceMappings.SingleAsync()).TargetId.Should().Be(winner.Items.Single().NormalizedDescriptionId);
	}
}
