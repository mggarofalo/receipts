using Application.Normalization;
using Application.Taxonomy;
using Common;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class PortableMappingRestoreTests(PostgresFixture source) : IClassFixture<PostgresFixture>
{
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }

	[Fact]
	public async Task V11_RestoresApproveRevokeRenew_Replays_PreservesV10_AndRejectsDivergenceAtomically()
	{
		string actor = "portable-reviewer"; Guid category = Guid.NewGuid(); Guid taxonomy;
		await using (var db = source.CreateDbContext())
		{
			db.Users.Add(new ApplicationUser { Id = actor, UserName = actor });
			var role = new IdentityRole(AppRoles.Admin); db.Roles.Add(role);
			db.UserRoles.Add(new() { UserId = actor, RoleId = role.Id }); await db.SaveChangesAsync();
			taxonomy = (await db.TaxonomyHeads.SingleAsync()).RevisionId;
		}
		taxonomy = (await new TaxonomyService(new Factory(source), TimeProvider.System).ChangeAsync(actor,
			new(taxonomy, Guid.NewGuid(), "create", new[] { new TaxonomyEdit("create", category, Name: "Food") }))).Id;
		NormalizationSourceKey key = new("Store", "Milk", null);
		var command = new CategoryMappingCommand(key, NormalizationCatalogPolicy.Hash(key), null, taxonomy, category, Guid.NewGuid(), "reviewed");
		var service = new CategoryMappingService(new Factory(source), TimeProvider.System);
		var approved = await service.ChangeAsync(actor, command);
		var revoked = await service.ChangeAsync(actor, command with { ExpectedMappingRevisionId = approved.Id, CategoryId = null, OperationId = Guid.NewGuid() });
		var renewed = await service.ChangeAsync(actor, command with { ExpectedMappingRevisionId = revoked.Id, OperationId = Guid.NewGuid() });
		string path = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		var inspection = await new OfflinePortableBackupInspection().InspectAsync(path);
		inspection.FormatVersion.Should().Be(13);
		var target = new PostgresFixture(); await target.InitializeAsync();
		try
		{
			var importer = new BackupImportService(new Factory(target), NullLogger<BackupImportService>.Instance);
			async Task Restore() { await using var input = File.OpenRead(path); await importer.ImportFromSqliteAsync(input, CancellationToken.None); }
			await Restore(); await Restore();
			await using (var db = target.CreateDbContext())
			{
				(await db.CategoryMappingRevisions.AsNoTracking().OrderBy(r => r.RecordedAt).Select(r => r.Id).ToListAsync())
					.Should().Equal(approved.Id, revoked.Id, renewed.Id);
				(await db.CategoryMappingHeads.SingleAsync()).RevisionId.Should().Be(renewed.Id);
				db.Users.Add(new ApplicationUser { Id = actor, UserName = actor }); await db.SaveChangesAsync();
			}
			var restoredService = new CategoryMappingService(new Factory(target), TimeProvider.System);
			(await restoredService.ChangeAsync(actor, command)).Should().Be(approved);
			await using (var sqlite = new SqliteConnection($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync(); await using var sql = sqlite.CreateCommand();
				sql.CommandText = "UPDATE backup_metadata SET value='10' WHERE key='export_version'; DROP TABLE category_mapping_archive";
				await sql.ExecuteNonQueryAsync();
			}
			await Restore();
			await using (var db = target.CreateDbContext()) { (await db.CategoryMappingRevisions.CountAsync()).Should().Be(3); }
			var later = await restoredService.ChangeAsync(actor, command with { ExpectedMappingRevisionId = renewed.Id, CategoryId = null, OperationId = Guid.NewGuid() });
			File.Delete(path); path = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
			await FluentActions.Awaiting(Restore).Should().ThrowAsync<InvalidOperationException>().WithMessage("*immutable destination history*");
			await using (var db = target.CreateDbContext()) { (await db.CategoryMappingHeads.SingleAsync()).RevisionId.Should().Be(later.Id); }
			await using (var sqlite = new SqliteConnection($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync(); await using var sql = sqlite.CreateCommand();
				sql.CommandText = "DELETE FROM category_mapping_archive WHERE kind='revision' AND json_extract(payload,'$.Id')=$id";
				sql.Parameters.AddWithValue("$id", revoked.Id.ToString()); await sql.ExecuteNonQueryAsync();
			}
			await FluentActions.Awaiting(Restore).Should().ThrowAsync<InvalidOperationException>().WithMessage("*mapping predecessor*");
			await using (var db = target.CreateDbContext()) { (await db.CategoryMappingRevisions.CountAsync()).Should().Be(4); }
		}
		finally { File.Delete(path); await target.DisposeAsync(); }
	}
}
