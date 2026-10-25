using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class PortableTaxonomyTrashRestoreTests : IClassFixture<PostgresFixture>
{
	private readonly PostgresFixture source;
	public PortableTaxonomyTrashRestoreTests(PostgresFixture source) => this.source = source;
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }

	[Fact]
	public async Task V10_PreservesDuplicateNamesDecisionTombstonesAndRejectsIncompleteDeletionAtomically()
	{
		Guid active = Guid.NewGuid(), deleted = Guid.NewGuid(), child = Guid.NewGuid(), oldChild = Guid.NewGuid();
		Guid first = Guid.NewGuid(), second = Guid.NewGuid(), oldPair = Guid.NewGuid(), pair = Guid.NewGuid();
		if (first.CompareTo(second) > 0) { (first, second) = (second, first); }
		var stamp = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
		await using (var db = source.CreateDbContext())
		{
			db.Categories.AddRange(new CategoryEntity { Id = active, Name = "Repeated" },
				new CategoryEntity { Id = deleted, Name = "Repeated", DeletedAt = stamp, DeletedByUserId = "archive-actor" });
			db.Subcategories.AddRange(new SubcategoryEntity { Id = child, CategoryId = active, Name = "Repeated child" },
				new SubcategoryEntity { Id = oldChild, CategoryId = active, Name = "Repeated child", DeletedAt = stamp });
			db.Receipts.AddRange(new ReceiptEntity { Id = first, Location = "Store", Date = new(2026, 1, 1) },
				new ReceiptEntity { Id = second, Location = "Store", Date = new(2026, 1, 1) });
			await db.SaveChangesAsync();
			db.AcceptedDuplicatePairs.AddRange(new AcceptedDuplicatePairEntity
			{
				Id = oldPair,
				ReceiptIdA = first,
				ReceiptIdB = second,
				AcceptedAt = stamp,
				DeletedAt = stamp,
				DeletedByUserId = "archive-actor"
			},
				new AcceptedDuplicatePairEntity { Id = pair, ReceiptIdA = first, ReceiptIdB = second, AcceptedAt = stamp.AddSeconds(1) });
			await db.SaveChangesAsync();
		}
		string path = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		var target = new PostgresFixture();
		await target.InitializeAsync();
		try
		{
			var importer = new BackupImportService(new Factory(target), NullLogger<BackupImportService>.Instance);
			async Task Restore() { await using var input = File.OpenRead(path); await importer.ImportFromSqliteAsync(input, default); }
			await Restore();
			await Restore();
			await using (var db = target.CreateDbContext())
			{
				(await db.Categories.IgnoreQueryFilters().Where(c => c.Name == "Repeated").CountAsync()).Should().Be(2);
				(await db.Categories.SingleAsync(c => c.Id == active)).DeletedAt.Should().BeNull();
				(await db.Categories.IgnoreQueryFilters().SingleAsync(c => c.Id == deleted)).DeletedByUserId.Should().Be("archive-actor");
				(await db.Subcategories.IgnoreQueryFilters().Where(c => c.CategoryId == active).CountAsync()).Should().Be(2);
				(await db.Subcategories.IgnoreQueryFilters().SingleAsync(c => c.Id == oldChild)).DeletedAt.Should().Be(stamp);
				var decisions = await db.AcceptedDuplicatePairs.IgnoreQueryFilters().OrderBy(p => p.Id).ToListAsync();
				decisions.Select(p => p.Id).Should().BeEquivalentTo([oldPair, pair]);
				decisions.Single(p => p.Id == oldPair).DeletedAt.Should().Be(stamp);
				decisions.Single(p => p.Id == oldPair).DeletedByUserId.Should().Be("archive-actor");
				decisions.Single(p => p.Id == pair).DeletedAt.Should().BeNull();
			}
			await using (var sqlite = new SqliteConnection($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using var command = sqlite.CreateCommand();
				command.CommandText = "UPDATE portable_deletion_state SET payload=json_remove(payload, '$.DeletedAt') WHERE kind='categories'; UPDATE receipts SET location='must not commit'";
				await command.ExecuteNonQueryAsync();
			}
			await FluentActions.Invoking(Restore).Should().ThrowAsync<InvalidOperationException>().WithMessage("*Missing JSON field*DeletedAt*");
			await using (var db = target.CreateDbContext())
			{
				(await db.Receipts.SingleAsync(r => r.Id == first)).Location.Should().Be("Store");
				(await db.Categories.IgnoreQueryFilters().SingleAsync(c => c.Id == deleted)).DeletedAt.Should().Be(stamp);
				(await db.AcceptedDuplicatePairs.IgnoreQueryFilters().CountAsync()).Should().Be(2);
			}
		}
		finally { await target.DisposeAsync(); File.Delete(path); }
	}
}
