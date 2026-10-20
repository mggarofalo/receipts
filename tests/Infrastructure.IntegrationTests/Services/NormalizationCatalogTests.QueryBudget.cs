using System.Data.Common;
using Application.Normalization;
using FluentAssertions;
using Infrastructure;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task HistoryPreview_ClassificationQueriesGrowByBatchRatherThanPerPurchase()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks");
		await ItemsAsync(Source, 1);
		var commands = new CatalogQueryCounter();
		var service = new NormalizationCatalogService(new OptionsFactory(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(commands).Options), TimeProvider.System, publisher.Object);
		var small = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		small.Eligible.Should().Be(1);
		int smallCount = commands.Count;
		await ItemsAsync(Source, 99);
		commands.Count = 0;
		var large = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		large.Total.Should().Be(100);
		large.Eligible.Should().Be(100);
		large.Protected.Should().Be(0);
		commands.Count.Should().BeLessThanOrEqualTo(smallCount + 30,
			"one batch of classification facts must not add several SELECT round trips for every purchase");
		(await ReadAsync((await ReadIdsAsync()))).Should().OnlyContain(item => item.NormalizedDescriptionId == null,
			"a preview never applies its historical memberships");
	}

	private async Task<Guid[]> ReadIdsAsync()
	{
		await using var context = fixture.CreateDbContext();
		return await context.ReceiptItems.Select(item => item.Id).ToArrayAsync();
	}
	private sealed class CatalogQueryCounter : DbCommandInterceptor
	{
		public int Count { get; set; }
		private static bool IsReadQuery(DbCommand command)
			=> command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
				|| command.CommandText.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
		public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
		{
			if (IsReadQuery(command))
			{
				Count++;
			}

			return result;
		}
		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
			InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			if (IsReadQuery(command))
			{
				Count++;
			}

			return ValueTask.FromResult(result);
		}
	}
}


