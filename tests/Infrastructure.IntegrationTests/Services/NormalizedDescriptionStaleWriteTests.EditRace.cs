using System.Data.Common;
using System.Diagnostics;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.IntegrationTests.Services;

public partial class NormalizedDescriptionStaleWriteTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task EditReadBeforeWorkerCommit_CannotPreserveAStaleLinkUnderNewText(bool changeDescription)
	{
		Seed seed = await SeedAsync();
		ReceiptItemEntity requested;
		await using (ApplicationDbContext before = fixture.CreateDbContext())
		{
			requested = await before.ReceiptItems.AsNoTracking().SingleAsync(item => item.Id == seed.Item);
		}
		if (changeDescription)
		{
			requested.Description = "Bread";
		}
		else { requested.UnitPrice = 7; requested.TotalAmount = requested.Quantity * 7; }
		ItemReadGate editGate = new();
		TargetAttempt workerAttempt = new();
		ReceiptItemRepository editingRepository = new(new OptionsFactory(Options(editGate)));
		using ServiceProvider provider = BuildProvider(CanonicalService(fixture.CreateOptions()), new OptionsFactory(Options(workerAttempt)));
		using NormalizedDescriptionResolutionService worker = CreateResolver(provider);
		Task editing = editingRepository.UpdateAsync([requested], CancellationToken.None);
		Task<NormalizedDescriptionResolutionService.ResolutionSummary>? resolving = null;
		try
		{
			await editGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			resolving = worker.ProcessPendingResolutionsAsync(CancellationToken.None);
			int pid = await workerAttempt.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
			// Old code allows the worker to commit while the editor retains stale originals.
			// A protected editor instead makes the worker wait for its row lock. Both serial
			// outcomes are valid; observe the database boundary before releasing the edit.
			await using ApplicationDbContext monitor = fixture.CreateDbContext();
			Stopwatch deadline = Stopwatch.StartNew();
			while (!resolving.IsCompleted)
			{
				if (await monitor.Database.SqlQuery<string?>($"SELECT wait_event_type AS \"Value\" FROM pg_stat_activity WHERE pid = {pid}").SingleAsync() == "Lock")
				{
					break;
				}

				if (deadline.Elapsed > TimeSpan.FromSeconds(10))
				{
					throw new TimeoutException("Worker neither completed nor reached a PostgreSQL row-lock wait.");
				}

				await Task.Delay(10);
			}
		}
		finally { editGate.Release.TrySetResult(); }
		await Task.WhenAll(editing, resolving!).WaitAsync(TimeSpan.FromSeconds(15));
		await using (ApplicationDbContext verify = fixture.CreateDbContext())
		{
			ReceiptItemEntity edited = await verify.ReceiptItems.SingleAsync(item => item.Id == seed.Item);
			edited.Description.Should().Be(changeDescription ? "Bread" : "Milk");
			if (changeDescription)
			{
				edited.NormalizedDescriptionId.Should().BeNull("an earlier null original must not hide a Milk assignment committed after that read");
				edited.NormalizedDescriptionMatchScore.Should().BeNull();
			}
			else
			{
				edited.UnitPrice.Should().Be(7);
				if ((await resolving!).Linked == 1)
				{
					edited.NormalizedDescriptionId.Should().Be(seed.Milk, "price-only edits preserve a concurrent valid assignment");
				}
			}
		}
		// If locking made this cycle skip the edited revision, the next one resolves the
		// current text; an existing valid classification remains untouched.
		await worker.ProcessPendingResolutionsAsync(CancellationToken.None);
		await using ApplicationDbContext final = fixture.CreateDbContext();
		(await final.ReceiptItems.SingleAsync(item => item.Id == seed.Item)).NormalizedDescriptionId.Should().Be(changeDescription ? seed.Bread : seed.Milk);
	}

	private sealed class ItemReadGate : DbCommandInterceptor
	{
		private bool _entered;
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
		{
			if (!_entered && command.CommandText.Contains("FROM receipts.\"ReceiptItems\"", StringComparison.Ordinal) && !command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
			{
				_entered = true;
				Entered.TrySetResult();
				await Release.Task.WaitAsync(cancellationToken);
			}
			return result;
		}
	}
}
