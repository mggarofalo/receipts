using System.Data.Common;
using Common;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.IntegrationTests;

// RECEIPTS-1111: a backup restore saves every imported item in one SaveChanges. Review-work
// reconciliation must run in statements bounded by ReviewWorkChunkSize, never by the save's size.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class ReviewWorkChunkingTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	private const int Chunk = ApplicationDbContext.ReviewWorkChunkSize;
	private const int ItemCount = (2 * Chunk) + 500;

	[Fact]
	public async Task LargeSave_ReconcilesReviewWorkInStatementsBoundedByChunkSize()
	{
		ReviewWorkCommands commands = new();
		(Guid receiptId, Guid[] itemIds) = await SaveReceiptAsync(ItemCount, commands);

		commands.ArrayLengths.Should().NotBeEmpty();
		commands.ArrayLengths.Max().Should().BeLessThanOrEqualTo(Chunk,
			"no review-work statement may scale with the total number of saved items");
		commands.Inserts.Should().Be((ItemCount + Chunk - 1) / Chunk);
		commands.Deletes.Should().Be((ItemCount + Chunk - 1) / Chunk);

		await using ApplicationDbContext db = fixture.CreateDbContext();
		List<ReceiptItemEntity> items = await db.ReceiptItems.AsNoTracking().Where(item => item.ReceiptId == receiptId).ToListAsync();
		items.Should().HaveCount(ItemCount);
		items.Should().OnlyContain(item => item.NormalizationReviewState == NormalizationReviewState.NeedsReview && item.NormalizationSourceHash != null);
		List<NormalizationReviewWorkEntity> work = await db.NormalizationReviewWork.AsNoTracking().ToListAsync();
		work.Select(row => new { row.ItemId, row.ReceiptId, row.SourceHash })
			.Should().BeEquivalentTo(items.Select(item => new { ItemId = item.Id, item.ReceiptId, SourceHash = item.NormalizationSourceHash }));
		work.Select(row => row.ItemId).Should().BeEquivalentTo(itemIds);
	}

	[Fact]
	public async Task LargeSoftDelete_RemovesReviewWorkAcrossChunks()
	{
		(_, Guid[] itemIds) = await SaveReceiptAsync(ItemCount, new ReviewWorkCommands());
		Guid[] deleted = itemIds.Take(Chunk + 500).ToArray();
		ReviewWorkCommands commands = new();

		await using (ApplicationDbContext db = new(Options(commands)))
		{
			db.ReceiptItems.RemoveRange(await db.ReceiptItems.Where(item => deleted.Contains(item.Id)).ToListAsync());
			await db.SaveChangesAsync();
		}

		commands.ArrayLengths.Max().Should().BeLessThanOrEqualTo(Chunk);
		commands.Deletes.Should().Be((deleted.Length + Chunk - 1) / Chunk);
		await using ApplicationDbContext after = fixture.CreateDbContext();
		(await after.ReceiptItems.IgnoreQueryFilters().CountAsync(item => deleted.Contains(item.Id) && item.DeletedAt != null))
			.Should().Be(deleted.Length);
		(await after.NormalizationReviewWork.Select(row => row.ItemId).ToListAsync())
			.Should().BeEquivalentTo(itemIds.Except(deleted));
	}

	private async Task<(Guid ReceiptId, Guid[] ItemIds)> SaveReceiptAsync(int count, ReviewWorkCommands commands)
	{
		await using ApplicationDbContext db = new(Options(commands));
		ReceiptEntity receipt = new() { Id = Guid.NewGuid(), Location = "Synthetic Import Store", Date = new(2025, 1, 15), TaxAmountCurrency = Currency.USD };
		db.Receipts.Add(receipt);
		ReceiptItemEntity[] items = Enumerable.Range(0, count).Select(i => new ReceiptItemEntity
		{
			Id = Guid.NewGuid(),
			ReceiptId = receipt.Id,
			Description = $"SYNTHETIC ITEM {i}",
			ReceiptItemCode = $"{i}",
			Quantity = 1m,
			UnitPrice = 1.25m,
			UnitPriceCurrency = Currency.USD,
			TotalAmount = 1.25m,
			TotalAmountCurrency = Currency.USD,
			Category = "Food",
			NormalizationOrigin = "unassigned",
		}).ToArray();
		db.ReceiptItems.AddRange(items);
		await db.SaveChangesAsync();
		return (receipt.Id, items.Select(item => item.Id).ToArray());
	}

	private DbContextOptions<ApplicationDbContext> Options(ReviewWorkCommands commands)
		=> new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(commands).Options;

	// Records every command that touches the review-work table and the size of each array parameter it binds.
	private sealed class ReviewWorkCommands : DbCommandInterceptor
	{
		private const string Table = "\"NormalizationReviewWork\"";
		public List<int> ArrayLengths { get; } = [];
		public int Inserts { get; private set; }
		public int Deletes { get; private set; }

		public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
		{
			Record(command);
			return result;
		}

		public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
		{
			Record(command);
			return ValueTask.FromResult(result);
		}

		public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
		{
			Record(command);
			return result;
		}

		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			Record(command);
			return ValueTask.FromResult(result);
		}

		private void Record(DbCommand command)
		{
			string text = command.CommandText;
			if (!text.Contains(Table, StringComparison.Ordinal)) { return; }
			if (text.Contains($"INSERT INTO matching.{Table}", StringComparison.Ordinal)) { Inserts++; }
			if (text.Contains($"DELETE FROM matching.{Table}", StringComparison.Ordinal)) { Deletes++; }
			ArrayLengths.AddRange(command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).OfType<Array>().Select(array => array.Length));
		}
	}
}
