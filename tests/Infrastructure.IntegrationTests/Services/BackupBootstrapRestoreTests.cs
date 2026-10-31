using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class BackupBootstrapRestoreTests : IClassFixture<PostgresFixture>
{
	private readonly PostgresFixture source;
	public BackupBootstrapRestoreTests(PostgresFixture source) => this.source = source;
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }

	[Fact]
	public async Task V10_ExactFreshRestoreRollsBackBootstrapCleanupAndRetainsPopulatedTargetRows()
	{
		await using (var db = source.CreateDbContext())
		{
			await db.ItemTemplates.ExecuteDeleteAsync();
			await db.Subcategories.ExecuteDeleteAsync();
			await db.Categories.ExecuteDeleteAsync();
		}
		string path = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		var target = new PostgresFixture();
		await target.InitializeAsync();
		try
		{
			var importer = new BackupImportService(new Factory(target), NullLogger<BackupImportService>.Instance);
			async Task Restore() { await using var input = File.OpenRead(path); await importer.ImportFromSqliteAsync(input, default); }
			await using (var db = target.CreateDbContext())
			{
				(await BackupImportService.HasPristineBootstrapAsync(db, default)).Should().BeTrue();
			}
			// Valid preflight, late catalog failure: cleanup must be rolled back too.
			await using (var sqlite = new SqliteConnection($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using var command = sqlite.CreateCommand();
				command.CommandText = "INSERT INTO normalization_catalog(kind,id,payload) VALUES ('invalid','b516a17c-104b-47c5-a61e-f5729d0941e3','{}')";
				await command.ExecuteNonQueryAsync();
			}
			await FluentActions.Invoking(Restore).Should().ThrowAsync<InvalidOperationException>();
			await using (var db = target.CreateDbContext())
			{
				(await BackupImportService.HasPristineBootstrapAsync(db, default)).Should().BeTrue();
			}
			await using (var sqlite = new SqliteConnection($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using var command = sqlite.CreateCommand();
				command.CommandText = "DELETE FROM normalization_catalog WHERE kind='invalid'";
				await command.ExecuteNonQueryAsync();
			}
			await Restore();
			await Restore();
			await using (var db = target.CreateDbContext())
			{
				(await db.Categories.IgnoreQueryFilters().CountAsync()).Should().Be(0);
				(await db.Subcategories.IgnoreQueryFilters().CountAsync()).Should().Be(0);
				(await db.ItemTemplates.IgnoreQueryFilters().CountAsync()).Should().Be(0);
				db.Categories.Add(new() { Id = Guid.NewGuid(), Name = "User category" });
				await db.SaveChangesAsync();
			}
			await Restore();
			await using (var db = target.CreateDbContext())
			{
				(await db.Categories.SingleAsync()).Name.Should().Be("User category");
			}
		}
		finally { await target.DisposeAsync(); File.Delete(path); }
	}
}
