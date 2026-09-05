using System.Data.Common;
using System.Diagnostics;
using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public partial class NormalizedDescriptionStaleWriteTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ConcurrentMergeOrRequeueAndRejection_UseCompatibleLockOrder(bool requeue)
	{
		Seed seed = await SeedAsync();
		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			ReceiptItemEntity item = await setup.ReceiptItems.SingleAsync(item => item.Id == seed.Item);
			item.NormalizedDescriptionId = seed.Milk;
			if (requeue)
			{
				(await setup.NormalizedDescriptions.SingleAsync(row => row.Id == seed.Milk)).Status = NormalizedDescriptionStatus.PendingReview;
			}

			await setup.SaveChangesAsync();
		}
		DeleteGate mergeGate = new();
		TargetAttempt rejectionAttempt = new();
		NormalizedDescriptionService mergeService = CanonicalService(Options(mergeGate));
		NormalizedDescriptionService rejectService = CanonicalService(Options(rejectionAttempt));
		string fingerprint = (await mergeService.PreviewRequeuePendingAsync(CancellationToken.None)).PendingFingerprint;
		Task merge = requeue ? mergeService.RequeuePendingAsync(fingerprint, CancellationToken.None) : mergeService.MergeAsync(seed.Bread, seed.Milk, CancellationToken.None);
		Task<bool>? reject = null;
		try
		{
			await mergeGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			reject = rejectService.UpdateStatusAsync(seed.Milk, NormalizedDescriptionStatus.Rejected, CancellationToken.None);
			int pid = await rejectionAttempt.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await WaitForPostgresLockAsync(pid);
		}
		finally
		{
			mergeGate.Release.TrySetResult();
		}
		await Task.WhenAll(merge, reject!).WaitAsync(TimeSpan.FromSeconds(15));
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await verify.ReceiptItems.SingleAsync(item => item.Id == seed.Item)).NormalizedDescriptionId.Should().Be(requeue ? null : seed.Bread);
		(await verify.NormalizedDescriptions.AnyAsync(row => row.Id == seed.Milk)).Should().BeFalse();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ConcurrentWorkerAndRejection_AreConsistentInEitherCommitOrder(bool rejectionWins)
	{
		Seed seed = await SeedAsync();
		TargetAcquiredGate winnerGate = new();
		TargetAttempt loserAttempt = new();
		DbContextOptions<ApplicationDbContext> workerOptions = Options(rejectionWins ? loserAttempt : winnerGate);
		DbContextOptions<ApplicationDbContext> rejectOptions = Options(rejectionWins ? winnerGate : loserAttempt);
		Mock<INormalizedDescriptionService> canonical = new();
		canonical.Setup(service => service.GetOrCreateAsync("Milk", It.IsAny<CancellationToken>())).ReturnsAsync(new GetOrCreateResult(new(seed.Milk, "Milk", NormalizedDescriptionStatus.Active, DateTimeOffset.UtcNow), 0.94));
		using ServiceProvider provider = BuildProvider(canonical.Object, new OptionsFactory(workerOptions));
		using NormalizedDescriptionResolutionService worker = CreateResolver(provider);
		NormalizedDescriptionService reviewer = CanonicalService(rejectOptions);
		Task<NormalizedDescriptionResolutionService.ResolutionSummary>? resolution = null;
		Task<bool>? rejection = null;
		try
		{
			if (rejectionWins)
			{
				rejection = reviewer.UpdateStatusAsync(seed.Milk, NormalizedDescriptionStatus.Rejected, CancellationToken.None);
			}
			else
			{
				resolution = worker.ProcessPendingResolutionsAsync(CancellationToken.None);
			}

			await winnerGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			if (rejectionWins)
			{
				resolution = worker.ProcessPendingResolutionsAsync(CancellationToken.None);
			}
			else
			{
				rejection = reviewer.UpdateStatusAsync(seed.Milk, NormalizedDescriptionStatus.Rejected, CancellationToken.None);
			}

			int pid = await loserAttempt.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await WaitForPostgresLockAsync(pid);
		}
		finally { winnerGate.Release.TrySetResult(); }
		await Task.WhenAll(resolution!, rejection!).WaitAsync(TimeSpan.FromSeconds(15));
		var summary = await resolution!;
		summary.Linked.Should().Be(rejectionWins ? 0 : 1);
		summary.Skipped.Should().Be(rejectionWins ? 1 : 0);
		(await rejection!).Should().BeTrue();
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		ReceiptItemEntity item = await verify.ReceiptItems.SingleAsync(item => item.Id == seed.Item);
		item.NormalizedDescriptionId.Should().BeNull();
		item.NormalizedDescriptionMatchScore.Should().BeNull();
		(await verify.NormalizedDescriptions.SingleAsync(row => row.Id == seed.Milk)).Status.Should().Be(NormalizedDescriptionStatus.Rejected);
		(await AuditCountAsync(seed.Item)).Should().Be(rejectionWins ? 1 : 3, "only committed insert, attachment and rejection detachment belong in the item history");
	}

	private sealed class TargetAcquiredGate : DbCommandInterceptor
	{
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal))
			{
				Entered.TrySetResult();
				await Release.Task.WaitAsync(cancellationToken);
			}
			return result;
		}
	}

	[Fact]
	public async Task FinalCommitFailure_RollsBackAcceptedLinkAndAudit_AndAllowsNextCycleRetry()
	{
		Seed seed = await SeedAsync();
		CommitFailureOnce failure = new();
		Mock<INormalizedDescriptionService> canonical = new();
		canonical.Setup(service => service.GetOrCreateAsync("Milk", It.IsAny<CancellationToken>())).ReturnsAsync(new GetOrCreateResult(new(seed.Milk, "Milk", NormalizedDescriptionStatus.Active, DateTimeOffset.UtcNow), 0.94));
		using ServiceProvider provider = BuildProvider(canonical.Object, new OptionsFactory(Options(failure)));
		using NormalizedDescriptionResolutionService worker = CreateResolver(provider);
		Func<Task> run = async () => await worker.ProcessPendingResolutionsAsync(CancellationToken.None);
		await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("Synthetic final commit rejection");
		await using (ApplicationDbContext verify = fixture.CreateDbContext())
		{
			ReceiptItemEntity unchanged = await verify.ReceiptItems.SingleAsync(item => item.Id == seed.Item);
			unchanged.NormalizedDescriptionId.Should().BeNull();
			unchanged.NormalizedDescriptionMatchScore.Should().BeNull();
		}
		(await AuditCountAsync(seed.Item)).Should().Be(1, "the failed transaction cannot leave a normalization audit without its business change");
		(await worker.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(1);
		(await AuditCountAsync(seed.Item)).Should().Be(2);
	}

	private sealed class CommitFailureOnce : DbTransactionInterceptor
	{
		private bool _failed;
		public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
		{
			if (!_failed)
			{
				_failed = true;
				throw new InvalidOperationException("Synthetic final commit rejection");
			}
			return ValueTask.FromResult(result);
		}
	}

	private DbContextOptions<ApplicationDbContext> Options(IInterceptor interceptor) => new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
		.UseNpgsql(builder => { builder.MaxBatchSize(1); builder.UsePublicMigrationsHistory(); }).AddInterceptors(interceptor).Options;

	private static NormalizedDescriptionService CanonicalService(DbContextOptions<ApplicationDbContext> options) => new(new OptionsFactory(options), Embedding(), new NormalizedDescriptionMapper(), new NormalizedDescriptionSettingsMapper());

	private async Task WaitForPostgresLockAsync(int pid)
	{
		await using ApplicationDbContext monitor = fixture.CreateDbContext();
		Stopwatch deadline = Stopwatch.StartNew();
		while (deadline.Elapsed < TimeSpan.FromSeconds(10))
		{
			string? wait = await monitor.Database.SqlQuery<string?>($"SELECT wait_event_type AS \"Value\" FROM pg_stat_activity WHERE pid = {pid}").SingleAsync();
			if (wait == "Lock")
			{
				return;
			}

			await Task.Delay(10);
		}
		throw new TimeoutException("The competing PostgreSQL operation never reached its row-lock wait.");
	}

	private sealed class OptionsFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
	}

	private sealed class DeleteGate : DbCommandInterceptor
	{
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("DELETE FROM", StringComparison.Ordinal) && command.CommandText.Contains("\"NormalizedDescriptions\"", StringComparison.Ordinal))
			{
				Entered.TrySetResult();
				await Release.Task.WaitAsync(cancellationToken);
			}
			return result;
		}
	}

	private sealed class TargetAttempt : DbCommandInterceptor
	{
		public TaskCompletionSource<int> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private void Observe(DbCommand command)
		{
			if (command.CommandText.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal) || command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
			{
				Started.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
			}
		}
		public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
		{
			Observe(command);
			return ValueTask.FromResult(result);
		}
		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			Observe(command);
			return ValueTask.FromResult(result);
		}
	}
}
