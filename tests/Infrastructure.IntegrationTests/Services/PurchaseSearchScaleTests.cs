using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Models.Reports;
using Application.Models.Search;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class PurchaseSearchScaleTests(PostgresFixture fixture, ITestOutputHelper output)
{
	[Theory]
	[InlineData(64, 16_509)]
	[InlineData(128, 16_509)]
	[InlineData(64, 256)]
	[InlineData(128, 256)]
	public async Task HistoricalCategoryFilter_HasBoundedSqlShape(int revisionCount, int assignedCount)
	{
		const int purchaseCount = 16_509;
		Guid root = Guid.NewGuid(), child = Guid.NewGuid();
		Guid[] items = Enumerable.Range(0, purchaseCount).Select(_ => Guid.NewGuid()).ToArray();
		await using (var db = fixture.CreateDbContext())
		{
			await using var tx = await db.Database.BeginTransactionAsync();
			await db.Database.ExecuteSqlRawAsync("TRUNCATE \"ReceiptItems\", \"Receipts\" RESTART IDENTITY CASCADE");
			db.TaxonomyIdentities.AddRange(new TaxonomyIdentityEntity { Id = root }, new TaxonomyIdentityEntity { Id = child });
			Guid previous = (await db.TaxonomyHeads.SingleAsync()).RevisionId;
			long sequence = await db.TaxonomyRevisions.MaxAsync(x => x.Sequence) + 1;
			Guid[] revisions = Enumerable.Range(0, revisionCount).Select(_ => Guid.NewGuid()).ToArray();
			for (int i = 0; i < revisionCount; i++)
			{
				Guid revision = revisions[i];
				db.TaxonomyRevisions.Add(new TaxonomyRevisionEntity { Id = revision, Sequence = sequence + i, PreviousRevisionId = previous });
				db.TaxonomyNodes.AddRange(new TaxonomyNodeEntity { RevisionId = revision, CategoryId = root, Name = "Root" },
					new TaxonomyNodeEntity { RevisionId = revision, CategoryId = child, ParentId = root, Name = "Child" });
				previous = revision;
			}
			(await db.TaxonomyHeads.SingleAsync()).RevisionId = previous;
			Guid receiptId = Guid.NewGuid();
			db.Receipts.Add(new ReceiptEntity { Id = receiptId, Date = new DateOnly(2025, 1, 1), Location = "Review store", TaxAmountCurrency = Currency.USD });
			foreach (Guid id in items)
			{
				db.ReceiptItems.Add(new ReceiptItemEntity
				{
					Id = id,
					ReceiptId = receiptId,
					Description = "Review purchase",
					Category = "Legacy",
					Quantity = 1,
					UnitPrice = 1,
					TotalAmount = 1,
					UnitPriceCurrency = Currency.USD,
					TotalAmountCurrency = Currency.USD
				});
			}

			for (int i = 0; i < assignedCount; i++)
			{
				Guid assignmentId = Guid.NewGuid();
				db.Classifications.Add(new PurchaseClassificationEntity
				{
					Id = assignmentId,
					ReceiptItemId = items[i],
					Sequence = 1,
					TaxonomyRevisionId = revisions[i % revisionCount],
					CategoryId = child,
					RecordedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
				});
				db.CurrentClassifications.Add(new CurrentClassificationEntity { ReceiptItemId = items[i], AssignmentId = assignmentId });
			}
			await db.SaveChangesAsync();
			await tx.CommitAsync();
		}

		var capture = new Capture();
		var factory = new ContextFactory(fixture, capture);
		var watch = Stopwatch.StartNew();
		PurchaseSearchResult result = await new PurchaseSearchReader(factory).SearchAsync("", 0, 25,
			new PurchaseSearchOptions(CategoryView: CategoryReportView.AsClassified, CategoryId: root), CancellationToken.None);
		watch.Stop();
		result.Total.Should().Be(assignedCount);
		result.Items.Should().HaveCount(25);
		using var evidence = new StreamWriter(Path.Combine(Path.GetTempPath(),
			$"purchase-search-scale-{revisionCount}-{assignedCount}.txt"));
		evidence.WriteLine($"purchases={purchaseCount}, revisions={revisionCount}, assignments={assignedCount}, elapsedMs={watch.ElapsedMilliseconds}, commands={capture.Commands.Count}");
		var filteredCommands = capture.Commands.Where(x => x.Sql.Contains("WITH RECURSIVE", StringComparison.Ordinal)).ToArray();
		filteredCommands.Should().HaveCount(2);
		foreach (var command in filteredCommands)
		{
			int unionArms = Regex.Matches(command.Sql, @"\bUNION(?:\s+ALL)?\b").Count;
			// Two recursive branches, independently of the number of taxonomy revisions.
			unionArms.Should().Be(2);
			command.Parameters.Count.Should().BeLessThan(20);
			evidence.WriteLine($"sqlChars={command.Sql.Length}, parameters={command.Parameters.Count}, unions={unionArms}");
		}
		await using var connection = new NpgsqlConnection(fixture.ConnectionString);
		await connection.OpenAsync();
		foreach (var command in filteredCommands)
		{
			await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + command.Sql, connection);
			foreach (var parameter in command.Parameters)
			{
				explain.Parameters.Add(new NpgsqlParameter(parameter.Name, parameter.Value));
			}

			string json = (string)(await explain.ExecuteScalarAsync())!;
			using JsonDocument plan = JsonDocument.Parse(json);
			JsonElement rootPlan = plan.RootElement[0];
			string kind = command.Sql.Contains("count(", StringComparison.OrdinalIgnoreCase) ? "count" : "page";
			int planNodes = Regex.Matches(json, "\"Node Type\"").Count;
			int pointerNodes = Regex.Matches(json, "\"Relation Name\": \"CurrentAssignments\"").Count;
			planNodes.Should().BeLessThan(50);
			pointerNodes.Should().Be(1);
			evidence.WriteLine($"{kind}ExecutionMs={rootPlan.GetProperty("Execution Time").GetDouble()}");
			evidence.WriteLine($"{kind}PlanNodes={planNodes}, currentAssignmentNodes={pointerNodes}, sharedHits={rootPlan.GetProperty("Plan").GetProperty("Shared Hit Blocks").GetInt32()}, rows={rootPlan.GetProperty("Plan").GetProperty("Actual Rows").GetDouble()}");
			evidence.WriteLine($"{kind}Plan={json}");
		}
		output.WriteLine($"Purchase search scale evidence: {Path.Combine(Path.GetTempPath(), $"purchase-search-scale-{revisionCount}-{assignedCount}.txt")}");
	}

	private sealed class ContextFactory(PostgresFixture fixture, Capture capture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(capture).Options);
	}
	private sealed class Capture : DbCommandInterceptor
	{
		public List<(string Sql, List<(string Name, object Value)> Parameters)> Commands { get; } = [];
		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
			InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			Commands.Add((command.CommandText, command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value ?? DBNull.Value)).ToList()));
			return ValueTask.FromResult(result);
		}
	}
}
