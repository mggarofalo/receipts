using System.Data.Common;
using System.Diagnostics;
using Application.Commands.ReceiptItem.Create;
using Application.Interfaces.Services;
using Domain.Core;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Npgsql;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public partial class TemplateNormalizationOwnershipTests
{
	[Fact]
	public async Task MixedRepeatedHints_StampOnlyCurrentUsableTemplates()
	{
		Seed seed = await SeedAsync();
		using ServiceProvider provider = BuildProvider(Mock.Of<INormalizedDescriptionService>());
		List<ReceiptItem> items = Enumerable.Range(0, 4).Select(_ => NewItem(seed.Bread)).ToList();
		var created = await provider.GetRequiredService<CreateReceiptItemCommandHandler>().Handle(new(items, seed.Receipt, [seed.Template, null, seed.Template, Guid.NewGuid()]), CancellationToken.None);
		created.Select(item => item.NormalizedDescriptionId).Should().Equal(seed.Milk, null, seed.Milk, null);
		created.Should().OnlyContain(item => item.NormalizedDescriptionMatchScore == null);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DirectHintBoundaries_RejectMisalignmentBeforePersisting(bool repositoryBoundary)
	{
		Seed seed = await SeedAsync();
		using ServiceProvider provider = BuildProvider(Mock.Of<INormalizedDescriptionService>());
		Func<Task> create = repositoryBoundary
			? async () => await new ReceiptItemRepository(new FixtureFactory(fixture)).CreateAsync([ReceiptItemEntityGenerator.Generate(seed.Receipt)], new Guid?[] { seed.Template, seed.Template }, CancellationToken.None)
			: async () => await provider.GetRequiredService<IReceiptItemService>().CreateAsync([NewItem(seed.Bread)], seed.Receipt, new Guid?[] { seed.Template, seed.Template }, CancellationToken.None);
		await create.Should().ThrowAsync<ArgumentException>();
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await verify.ReceiptItems.CountAsync(item => item.ReceiptId == seed.Receipt)).Should().Be(0);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task HintReloadAfterTemplateLockWait_UsesNewLinkOrDeletedFallback(bool deleting)
	{
		Seed seed = await SeedAsync();
		await using ApplicationDbContext manual = fixture.CreateDbContext();
		await using var transaction = await manual.Database.BeginTransactionAsync();
		await manual.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM library.\"ItemTemplates\" WHERE \"Id\" = {seed.Template} FOR UPDATE");
		ItemTemplateEntity template = await manual.ItemTemplates.SingleAsync(row => row.Id == seed.Template);
		if (deleting)
		{
			template.DeletedAt = DateTimeOffset.UtcNow;
		}
		else
		{
			template.NormalizedDescriptionId = seed.Bread;
		}

		await manual.SaveChangesAsync();
		TemplateAttempt attempt = new();
		using ServiceProvider provider = BuildProvider(Mock.Of<INormalizedDescriptionService>(), new OptionsFactory(Options(attempt)));
		Task<List<ReceiptItem>> creating = provider.GetRequiredService<CreateReceiptItemCommandHandler>().Handle(new([NewItem(seed.Milk)], seed.Receipt, [seed.Template]), CancellationToken.None).AsTask();
		int pid = await attempt.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
		await WaitForLockAsync(pid);
		await transaction.CommitAsync();
		var created = await creating.WaitAsync(TimeSpan.FromSeconds(10));
		created.Should().ContainSingle().Which.NormalizedDescriptionId.Should().Be(deleting ? null : seed.Bread);
		created[0].NormalizedDescriptionMatchScore.Should().BeNull();
	}

	[Fact]
	public async Task RepeatedHintRelinking_FallsBackAfterThreeAttemptsWithoutLosingReceiptItems()
	{
		Seed seed = await SeedAsync();
		int changes = 0;
		PreliminaryTargetsHook hook = new(async () =>
		{
			changes++;
			await using ApplicationDbContext other = fixture.CreateDbContext();
			ItemTemplateEntity template = await other.ItemTemplates.SingleAsync(row => row.Id == seed.Template);
			template.NormalizedDescriptionId = changes % 2 == 1 ? seed.Bread : seed.Milk;
			await other.SaveChangesAsync();
		});
		using ServiceProvider provider = BuildProvider(Mock.Of<INormalizedDescriptionService>(), new OptionsFactory(Options(hook)));
		var created = await provider.GetRequiredService<CreateReceiptItemCommandHandler>().Handle(new([NewItem(seed.Milk)], seed.Receipt, [seed.Template]), CancellationToken.None);
		changes.Should().Be(3);
		created.Should().ContainSingle().Which.NormalizedDescriptionId.Should().BeNull();
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await verify.ReceiptItems.CountAsync(item => item.ReceiptId == seed.Receipt)).Should().Be(1);
	}

	[Fact]
	public async Task HintedCreateRejectsATombstonedTarget_WithoutRejectingTheReceipt()
	{
		Seed seed = await SeedAsync();
		await RealRegistry().UpdateStatusAsync(seed.Milk, Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Rejected, CancellationToken.None);
		using ServiceProvider provider = BuildProvider(Mock.Of<INormalizedDescriptionService>());
		var created = await provider.GetRequiredService<CreateReceiptItemCommandHandler>().Handle(new([NewItem(seed.Bread)], seed.Receipt, [seed.Template]), CancellationToken.None);
		created.Should().ContainSingle().Which.NormalizedDescriptionId.Should().BeNull();
	}

	[Fact]
	public async Task DefaultSearchPath_SupportsHintedCreateAndRevisionGuardedUpdate()
	{
		Seed seed = await SeedAsync();
		NpgsqlConnectionStringBuilder connection = new(fixture.ConnectionString) { SearchPath = "public" };
		NpgsqlDataSourceBuilder builder = new(connection.ConnectionString); builder.UseVector();
		await using NpgsqlDataSource source = builder.Build();
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(source, postgres => { postgres.UseVector(); postgres.UsePublicMigrationsHistory(); }).Options;
		using ServiceProvider provider = BuildProvider(RealRegistry(), new OptionsFactory(options));
		await provider.GetRequiredService<IItemTemplateService>().UpdateAsync([new(seed.Template, "Milk")], CancellationToken.None);
		var created = await provider.GetRequiredService<CreateReceiptItemCommandHandler>().Handle(new([NewItem(seed.Bread)], seed.Receipt, [seed.Template]), CancellationToken.None);
		created.Should().ContainSingle().Which.NormalizedDescriptionId.Should().Be(seed.Milk);
	}

	private DbContextOptions<ApplicationDbContext> Options(IInterceptor interceptor) => new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(interceptor).Options;
	private sealed class OptionsFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
	}
	private async Task WaitForLockAsync(int pid)
	{
		await using ApplicationDbContext monitor = fixture.CreateDbContext();
		Stopwatch deadline = Stopwatch.StartNew();
		while (deadline.Elapsed < TimeSpan.FromSeconds(10))
		{
			if (await monitor.Database.SqlQuery<string?>($"SELECT wait_event_type AS \"Value\" FROM pg_stat_activity WHERE pid = {pid}").SingleAsync() == "Lock")
			{
				return;
			}

			await Task.Delay(10);
		}
		throw new TimeoutException("Expected PostgreSQL row-lock wait did not occur.");
	}
	private sealed class TemplateAttempt : DbCommandInterceptor
	{
		public TaskCompletionSource<int> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("library.\"ItemTemplates\"", StringComparison.Ordinal) && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
			{
				Started.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
			}

			return ValueTask.FromResult(result);
		}
	}
	private sealed class PreliminaryTargetsHook(Func<Task> action) : DbCommandInterceptor
	{
		public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("SELECT DISTINCT", StringComparison.Ordinal) && command.CommandText.Contains("library.\"ItemTemplates\"", StringComparison.Ordinal))
			{
				await action();
			}

			return result;
		}
	}
}
