using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class PortableTaxonomyRestoreTests : IClassFixture<PostgresFixture>
{
	private readonly PostgresFixture source;
	public PortableTaxonomyRestoreTests(PostgresFixture source) => this.source = source;
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }

	[Fact]
	public async Task V10_RestoresSealedHistoryTrashAndProvenance_ReplaysAndRejectsDivergenceAtomically()
	{
		Guid category = Guid.NewGuid(), receipt = Guid.NewGuid(), item = Guid.NewGuid();
		DateTimeOffset observed = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
		Guid historicalKey = Guid.NewGuid();
		await using (var db = source.CreateDbContext())
		{
			db.Categories.Add(new CategoryEntity { Id = category, Name = "Grocery", IsActive = false });
			db.Receipts.Add(new ReceiptEntity { Id = receipt, Location = "Original store", Date = new(2020, 1, 1) });
			db.ReceiptItems.Add(new ReceiptItemEntity
			{
				Id = item,
				ReceiptId = receipt,
				Category = "Grocery",
				Description = "Milk",
				Quantity = 2,
				UnitPrice = 3,
				TotalAmount = 6
			});
			await db.SaveChangesAsync();
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"Receipts\" SET \"DeletedAt\"={observed}, \"DeletedByUserId\"='historical-user' WHERE \"Id\"={receipt}");
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"DeletedAt\"={observed}, \"DeletedByApiKeyId\"={historicalKey}, \"CascadeDeletedByParentId\"={receipt} WHERE \"Id\"={item}");
		}
		var migration = new LegacyTaxonomyMigrationService(new Factory(source));
		var plan = await migration.PreflightAsync(new("portable-roundtrip", Guid.NewGuid(), "maintenance-admin", observed));
		await migration.ApplyAsync(LegacyMigrationFreeze.Source(plan));
		Guid identity = plan.Items.Single().CategoryId!.Value;
		string prefixPath = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		Guid next = Guid.NewGuid(), operation = Guid.NewGuid();
		await using (var db = source.CreateDbContext())
		{
			await using var tx = await db.Database.BeginTransactionAsync();
			db.TaxonomyRevisions.Add(new()
			{
				Id = next,
				Sequence = 2,
				PreviousRevisionId = plan.RevisionId,
				Actor = "admin",
				OperationId = operation,
				Reason = "rename archived identity",
				RecordedAt = observed.AddSeconds(1),
				AffectedCount = 1
			});
			await db.SaveChangesAsync();
			foreach (var node in plan.Nodes)
			{
				db.TaxonomyNodes.Add(new()
				{
					RevisionId = next,
					CategoryId = node.Node.Id,
					ParentId = node.Node.ParentId,
					Name = node.Node.Id == identity ? "Food" : node.Node.Name,
					Definition = node.Node.Definition,
					Archived = node.Node.Archived
				});
			}
			db.TaxonomyOperations.Add(new()
			{
				Actor = "admin",
				OperationId = operation,
				RequestHash = new string('A', 64),
				RevisionId = next,
				Reason = "rename archived identity",
				RecordedAt = observed.AddSeconds(1),
				AffectedCount = 1
			});
			(await db.TaxonomyHeads.SingleAsync()).RevisionId = next;
			await db.SaveChangesAsync();
			await tx.CommitAsync();
		}
		string path = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		var target = new PostgresFixture();
		await target.InitializeAsync();
		try
		{
			var importer = new BackupImportService(new Factory(target), NullLogger<BackupImportService>.Instance);
			async Task Restore()
			{
				await using var input = File.OpenRead(path);
				await importer.ImportFromSqliteAsync(input, CancellationToken.None);
			}
			await using (var prefix = File.OpenRead(prefixPath))
			{ await importer.ImportFromSqliteAsync(prefix, CancellationToken.None); }
			await Restore();
			await Restore();
			await using (var db = target.CreateDbContext())
			{
				(await db.TaxonomyHeads.SingleAsync()).RevisionId.Should().Be(next);
				(await db.TaxonomyRevisions.CountAsync()).Should().Be(3);
				(await db.Classifications.SingleAsync()).TaxonomyRevisionId.Should().Be(plan.RevisionId);
				(await db.CurrentClassifications.SingleAsync()).AssignmentId.Should().Be(plan.Items.Single().AssignmentId);
				(await db.TaxonomyNodes.SingleAsync(n => n.RevisionId == plan.RevisionId && n.CategoryId == identity)).Name.Should().Be("Grocery");
				(await db.TaxonomyNodes.SingleAsync(n => n.RevisionId == next && n.CategoryId == identity)).Name.Should().Be("Food");
				(await db.LegacyMigrationItems.CountAsync()).Should().Be(1);
				var restored = await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().SingleAsync(r => r.Id == item);
				restored.DeletedAt.Should().Be(observed);
				restored.CascadeDeletedByParentId.Should().Be(receipt);
				restored.DeletedByApiKeyId.Should().Be(historicalKey);
				restored.TotalAmount.Should().Be(6);
				(await db.Receipts.IgnoreQueryFilters().SingleAsync(r => r.Id == receipt)).DeletedByUserId.Should().Be("historical-user");
			}
			// Still-valid incoming operation metadata collides with sealed destination history.
			await using (var sqlite = new SqliteConnection($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using var command = sqlite.CreateCommand();
				command.CommandText = "UPDATE taxonomy_archive SET payload=replace(payload, $old, $new) WHERE kind='operation'; UPDATE receipts SET location='must not commit'";
				command.Parameters.AddWithValue("$old", new string('A', 64));
				command.Parameters.AddWithValue("$new", new string('B', 64));
				await command.ExecuteNonQueryAsync();
			}
			await FluentActions.Invoking(Restore).Should().ThrowAsync<InvalidOperationException>().WithMessage("*immutable*");
			await using (var db = target.CreateDbContext())
			{
				(await db.Receipts.IgnoreQueryFilters().SingleAsync(r => r.Id == receipt)).Location.Should().Be("Original store");
				(await db.Classifications.CountAsync()).Should().Be(1);
				(await db.TaxonomyHeads.SingleAsync()).RevisionId.Should().Be(next);
			}
			await using (var sqlite = new SqliteConnection($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using var command = sqlite.CreateCommand();
				command.CommandText = "DELETE FROM portable_deletion_state WHERE kind='receipt_items'";
				await command.ExecuteNonQueryAsync();
			}
			await FluentActions.Invoking(Restore).Should().ThrowAsync<InvalidOperationException>().WithMessage("*Missing*deletion*");
		}
		finally { await target.DisposeAsync(); File.Delete(path); File.Delete(prefixPath); }
	}
}
