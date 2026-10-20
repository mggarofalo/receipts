using System.Data.Common;
using Application.Normalization;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task ConflictingApprovals_CommitOneMappingAndNeverOverwriteTheWinner()
	{
		await ResetAsync();
		var firstTarget = await CanonicalAsync("Beef franks");
		var otherTarget = await CanonicalAsync("Chicken franks");
		var ids = await ItemsAsync(Source, 2);
		var firstService = Service();
		var otherService = Service();
		var first = await firstService.PreviewAsync(Actor, Request(firstTarget), CancellationToken.None);
		var other = await otherService.PreviewAsync(Actor, Request(otherTarget), CancellationToken.None);
		int audits = await AuditCountAsync(ids);
		var results = await Task.WhenAll(
			AttemptAsync(firstService, first), AttemptAsync(otherService, other));
		var winner = results.Should().ContainSingle(x => x.Operation != null).Subject.Operation!;
		results.Should().ContainSingle(x => x.Error != null).Which.Error!.StatusCode.Should().Be(409);
		var mapping = (await firstService.ListMappingsAsync(Actor, Source, CancellationToken.None))
			.Should().ContainSingle().Subject;
		mapping.TargetId.Should().Be(winner.TargetId);
		(await ReadAsync(ids)).Should().OnlyContain(x => x.NormalizedDescriptionId == winner.TargetId);
		(await AuditCountAsync(ids)).Should().Be(audits + ids.Length);
	}

	[Fact]
	public async Task Approval_WaitsForIndependentDatabaseWriterAndCancelledWaitDoesNotPersistMapping()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks");
		var ids = await ItemsAsync(Source, 1);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		await using var external = new NpgsqlConnection(fixture.ConnectionString);
		await external.OpenAsync();
		await using var transaction = await external.BeginTransactionAsync();
		await using var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1380143188, 1313821261)", external, transaction);
		await acquire.ExecuteNonQueryAsync();
		using var cancellation = new CancellationTokenSource();
		var approval = service.ApproveAsync(Actor, preview.Id, preview.Revision, cancellation.Token);
		await using var waiting = new NpgsqlCommand(
			"SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype='advisory' AND NOT granted AND pg_backend_pid() = ANY(pg_blocking_pids(pid)))",
			external, transaction);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		bool observedWait = false;
		try
		{
			while (!approval.IsCompleted && !observedWait)
			{
				observedWait = (bool)(await waiting.ExecuteScalarAsync(timeout.Token))!;
				if (!observedWait) { await Task.Delay(20, timeout.Token); }
			}
			observedWait.Should().BeTrue("the PostgreSQL writer gate must protect replicas, not only local service instances");
		}
		finally
		{
			cancellation.Cancel();
		}
		await FluentActions.Awaiting(async () => await approval).Should().ThrowAsync<OperationCanceledException>();
		await transaction.RollbackAsync();
		(await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Should().BeEmpty();
		(await ReadAsync(ids)).Single().NormalizedDescriptionId.Should().BeNull();
		var recovered = await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		recovered.Applied.Should().Be(1);
	}

	[Fact]
	public async Task HistoryCommitFailure_RollsBackOnlyCurrentChunkAndResumeDoesNotReplayPriorAudits()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks");
		var ids = await ItemsAsync(Source, 105);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		var first = await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		first.Applied.Should().Be(100);
		int audits = await AuditCountAsync(ids);
		var options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
			.AddInterceptors(new FailCommit()).Options;
		var failing = new NormalizationCatalogService(new OptionsFactory(options), TimeProvider.System, publisher.Object);
		await FluentActions.Awaiting(() => failing.ResumeAsync(Actor, first.Id, CancellationToken.None))
			.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected history commit failure");
		(await ReadAsync(ids)).Count(x => x.NormalizedDescriptionId == target.Id).Should().Be(100);
		(await AuditCountAsync(ids)).Should().Be(audits);
		var durable = await service.GetOperationAsync(Actor, first.Id, CancellationToken.None);
		durable!.Applied.Should().Be(100);
		durable.Remaining.Should().Be(5);
		var resumed = await service.ResumeAsync(Actor, first.Id, CancellationToken.None);
		resumed.State.Should().Be("completed");
		resumed.Applied.Should().Be(105);
		(await AuditCountAsync(ids)).Should().Be(audits + 5);
	}

	private static async Task<(NormalizationCatalogOperation? Operation, NormalizationCatalogException? Error)> AttemptAsync(
		NormalizationCatalogService service, NormalizationCatalogOperation preview)
	{
		try { return (await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None), null); }
		catch (NormalizationCatalogException exception) { return (null, exception); }
	}

	private sealed class FailCommit : DbTransactionInterceptor
	{
		public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
			TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
			=> throw new InvalidOperationException("Injected history commit failure");
	}

	private sealed class OptionsFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
	}
}
