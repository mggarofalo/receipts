using System.Data.Common;
using Application.Models.Reports;
using Common;
using FluentAssertions;
using Infrastructure.Configurations;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class CategoryReportReaderTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Fact]
	public async Task PostgreSql_PreservesFactsAcrossViewsAndCorrections_WithBoundedReads()
	{
		Guid old = Guid.NewGuid(), head = Guid.NewGuid();
		Guid root = Guid.NewGuid(), other = Guid.NewGuid(), leaf = Guid.NewGuid();
		Guid receipt = Guid.NewGuid(), trashedReceipt = Guid.NewGuid();
		var items = new List<ReceiptItemEntity>();
		var assignments = new List<PurchaseClassificationEntity>();
		await using (var seed = fixture.CreateDbContext())
		{
			// Nodes are writable only in the transaction creating their revision.
			await using var transaction = await seed.Database.BeginTransactionAsync();
			seed.TaxonomyIdentities.AddRange(new[] { root, other, leaf }.Select(id => new TaxonomyIdentityEntity { Id = id }));
			seed.TaxonomyRevisions.AddRange(
				new TaxonomyRevisionEntity { Id = old, Sequence = 1, PreviousRevisionId = TaxonomyConfiguration.GenesisId },
				new TaxonomyRevisionEntity { Id = head, Sequence = 2, PreviousRevisionId = old });
			foreach (Guid revision in new[] { old, head })
			{
				seed.TaxonomyNodes.AddRange(
					new TaxonomyNodeEntity { RevisionId = revision, CategoryId = root, Name = revision == old ? "Grocery" : "Food" },
					new TaxonomyNodeEntity { RevisionId = revision, CategoryId = other, Name = "Chilled", Archived = revision == head },
					new TaxonomyNodeEntity
					{
						RevisionId = revision,
						CategoryId = leaf,
						ParentId = revision == old ? root : other,
						Name = revision == old ? "Milk" : "Dairy",
						Archived = revision == head
					});
			}
			(await seed.TaxonomyHeads.SingleAsync()).RevisionId = head;
			seed.Receipts.AddRange(new ReceiptEntity { Id = receipt, Date = new(2020, 3, 1), Location = "Authorized" },
				new ReceiptEntity { Id = trashedReceipt, Date = new(2020, 3, 1), Location = "Authorized" });
			// A backdated period with substantially more purchases than taxonomy nodes.
			for (int i = 0; i < 16_008; i++)
			{
				var item = new ReceiptItemEntity
				{
					Id = Guid.NewGuid(),
					ReceiptId = i == 16_007 ? trashedReceipt : receipt,
					Description = i == 16_006 ? "Excluded" : "Included",
					Quantity = 7,
					UnitPrice = 99m,
					TotalAmount = i < 16_000 ? 0.12m : 1m,
					UnitPriceCurrency = Currency.USD,
					// Currency currently exposes only USD; unknown numeric values test partition isolation, not supported currency input.
					TotalAmountCurrency = i == 16_000 ? (Currency)1 : Currency.USD,
					DeletedAt = i == 16_005 ? DateTimeOffset.UtcNow : null
				};
				items.Add(item);
				if (i == 16_003) { continue; } // Absent current pointer.
				var assignment = new PurchaseClassificationEntity
				{
					Id = Guid.NewGuid(),
					ReceiptItemId = item.Id,
					Sequence = 1,
					CategoryId = i is 16_001 or 16_002 or 16_004 ? null : i % 2 == 0 ? root : leaf,
					TaxonomyRevisionId = old,
					RecordedAt = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero),
					Source = i == 0 ? "legacy_import" : "manual",
					LegacyResolution = i == 16_004 ? "unresolved_path" : null
				};
				assignments.Add(assignment);
				seed.CurrentClassifications.Add(new() { ReceiptItemId = item.Id, AssignmentId = assignment.Id });
				if (i == 16_002)
				{
					seed.LegacyMigrationItems.Add(new()
					{
						Actor = "report-fixture",
						OperationId = Guid.NewGuid(),
						ReceiptItemId = item.Id,
						AssignmentId = assignment.Id,
						Resolution = i == 16_002 ? "Unresolved" : "Resolved"
					});
				}
			}
			seed.ReceiptItems.AddRange(items);
			seed.Classifications.AddRange(assignments);
			await seed.SaveChangesAsync();
			await seed.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"Receipts\" SET \"DeletedAt\" = {DateTimeOffset.UtcNow} WHERE \"Id\" = {trashedReceipt}");
			await transaction.CommitAsync();
			await seed.Database.ExecuteSqlRawAsync("ANALYZE");
		}

		var commands = new CaptureCommands();
		var options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(commands).Options;
		await using var db = new ApplicationDbContext(options);
		var scope = db.ReceiptItems.Where(item => item.Description == "Included" && item.Receipt!.Location == "Authorized");
		async Task<CategoryReportResult> Read(CategoryReportView view, Guid? filter = null, IQueryable<ReceiptItemEntity>? scoped = null)
		{
			commands.Commands.Clear();
			var result = await CategoryReportReader.ReadAsync(db, scoped ?? scope, new(2020, 3, 1), new(2020, 3, 1),
				view, CategoryReportRollup.Root, filter);
			commands.Commands.Should().HaveCount(view == CategoryReportView.AsClassified ? 2 : 3);
			return result;
		}
		var historical = await Read(CategoryReportView.AsClassified);
		historical.Buckets.Sum(bucket => bucket.PurchaseCount).Should().Be(16_005);
		historical.Buckets.Single(bucket => bucket.CategoryId == root && bucket.Currency == "USD").Amount.Should().Be(1920m);
		historical.Buckets.Single(bucket => bucket.Currency == "1").Amount.Should().Be(1m);
		historical.Buckets.Single(bucket => bucket.State == CategoryReportState.Unclassified).PurchaseCount.Should().Be(1);
		historical.Buckets.Single(bucket => bucket.State == CategoryReportState.MissingAssignment).PurchaseCount.Should().Be(1);
		historical.Buckets.Single(bucket => bucket.State == CategoryReportState.UnresolvedLegacy).PurchaseCount.Should().Be(2);
		historical.Buckets.Single(bucket => bucket.CategoryId == root && bucket.Currency == "USD").Path.Single().Name.Should().Be("Grocery");
		await SavePlansAsync(commands, "historical");

		var current = await Read(CategoryReportView.CurrentHierarchy);
		current.CurrentRevisionId.Should().Be(head);
		current.Buckets.Single(bucket => bucket.CategoryId == other).Amount.Should().Be(960m);
		current.Buckets.Single(bucket => bucket.CategoryId == other).Path.Single().Archived.Should().BeTrue();
		current.Buckets.Single(bucket => bucket.CategoryId == root && bucket.Currency == "USD").Path.Single().Name.Should().Be("Food");
		current.Buckets.Sum(bucket => bucket.Amount).Should().Be(historical.Buckets.Sum(bucket => bucket.Amount));
		await SavePlansAsync(commands, "current");

		(await Read(CategoryReportView.AsClassified, root)).Buckets.Sum(bucket => bucket.PurchaseCount).Should().Be(16_001);
		(await Read(CategoryReportView.CurrentHierarchy, root)).Buckets.Sum(bucket => bucket.PurchaseCount).Should().Be(8_001);
		var tiny = scope.Where(item => item.Id == items[0].Id);
		(await Read(CategoryReportView.AsClassified, scoped: tiny)).Buckets.Single().Amount.Should().Be(0.12m);
		(await Read(CategoryReportView.CurrentHierarchy, scoped: tiny)).Buckets.Should().ContainSingle();

		var correction = new PurchaseClassificationEntity
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = items[0].Id,
			Sequence = 2,
			PreviousAssignmentId = assignments[0].Id,
			CategoryId = other,
			TaxonomyRevisionId = head,
			RecordedAt = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero)
		};
		db.Classifications.Add(correction);
		(await db.CurrentClassifications.SingleAsync(row => row.ReceiptItemId == items[0].Id)).AssignmentId = correction.Id;
		await db.SaveChangesAsync();
		(await Read(CategoryReportView.AsClassified, scoped: tiny)).Buckets.Single().CategoryId.Should().Be(other);
		(await db.Classifications.CountAsync(row => row.ReceiptItemId == items[0].Id)).Should().Be(2);
		foreach (CategoryReportView view in Enum.GetValues<CategoryReportView>())
		{
			commands.Commands.Clear();
			var cutoff = await CategoryReportReader.ReadAsync(db, scope, new(2020, 3, 1), new(2020, 3, 1), view,
				CategoryReportRollup.Root, knowledgeCutoff: assignments[0].RecordedAt);
			commands.Commands.Should().HaveCount(view == CategoryReportView.AsClassified ? 2 : 3);
			cutoff.Buckets.Single(bucket => bucket.CategoryId == root && bucket.Currency == "USD").Amount.Should().Be(view == CategoryReportView.AsClassified ? 1920m : 960m);
			cutoff.KnowledgeCutoff.Should().Be(assignments[0].RecordedAt);
			cutoff.AssignmentSelection.Should().Be(CategoryReportAssignmentSelection.LatestRecordedAtOrBeforeCutoff);
			await SavePlansAsync(commands, "cutoff-" + view);
			var beforeImport = await CategoryReportReader.ReadAsync(db, tiny, new(2020, 3, 1), new(2020, 3, 1), view,
				knowledgeCutoff: assignments[0].RecordedAt.AddTicks(-10));
			beforeImport.Buckets.Single().State.Should().Be(CategoryReportState.BeforeMigrationHistory);
		}
		var tied = new PurchaseClassificationEntity
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = items[0].Id,
			Sequence = 3,
			PreviousAssignmentId = correction.Id,
			CategoryId = leaf,
			TaxonomyRevisionId = old,
			RecordedAt = correction.RecordedAt
		};
		db.Classifications.Add(tied);
		(await db.CurrentClassifications.SingleAsync(row => row.ReceiptItemId == items[0].Id)).AssignmentId = tied.Id;
		(await db.TaxonomyHeads.SingleAsync()).RevisionId = old;
		await db.SaveChangesAsync();
		foreach (CategoryReportView view in Enum.GetValues<CategoryReportView>())
		{
			commands.Commands.Clear();
			var snapshot = await CategoryReportReader.ReadAsync(db, tiny, new(2020, 3, 1), new(2020, 3, 1), view,
				knowledgeCutoff: correction.RecordedAt,
				capturedCurrentRevisionId: view == CategoryReportView.CurrentHierarchy ? head : null);
			commands.Commands.Should().HaveCount(2);
			snapshot.Buckets.Single().CategoryId.Should().Be(leaf);
			snapshot.Buckets.Single().Path.First().Name.Should().Be(view == CategoryReportView.CurrentHierarchy ? "Chilled" : "Grocery");
			snapshot.CurrentRevisionId.Should().Be(view == CategoryReportView.CurrentHierarchy ? head : null);
		}

		(await CategoryReportReader.ReadAsync(db, tiny, new(2026, 1, 1), new(2026, 12, 31), CategoryReportView.AsClassified)).Buckets.Should().BeEmpty();
	}

	private async Task SavePlansAsync(CaptureCommands capture, string name)
	{
		string? directory = Environment.GetEnvironmentVariable("CATEGORY_REPORT_REVIEW_ARTIFACTS");
		if (directory == null) { return; }
		Directory.CreateDirectory(directory);
		await using var connection = new NpgsqlConnection(fixture.ConnectionString);
		await connection.OpenAsync();
		var evidence = new List<string>();
		foreach (var captured in capture.Commands)
		{
			evidence.Add(captured.CommandText);
			await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + captured.CommandText, connection);
			foreach (NpgsqlParameter parameter in captured.Parameters) { explain.Parameters.Add(parameter.Clone()); }
			await using var reader = await explain.ExecuteReaderAsync();
			while (await reader.ReadAsync()) { evidence.Add(reader.GetString(0)); }
		}
		await File.WriteAllLinesAsync(Path.Combine(directory, name + "-plans.txt"), evidence);
	}

	private sealed class CaptureCommands : DbCommandInterceptor
	{
		public List<NpgsqlCommand> Commands { get; } = [];
		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
			CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			var clone = new NpgsqlCommand(command.CommandText);
			foreach (NpgsqlParameter parameter in command.Parameters) { clone.Parameters.Add(parameter.Clone()); }
			Commands.Add(clone);
			return ValueTask.FromResult(result);
		}
	}
}



