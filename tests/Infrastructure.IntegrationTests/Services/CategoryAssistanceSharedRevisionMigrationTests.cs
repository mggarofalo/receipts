using FluentAssertions;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class CategoryAssistanceSharedRevisionMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	private const string Predecessor = "20261031213140_AddPendingNamePromotions";
	private const string SharedRevisionMigration = "20261101105137_AllowSharedSettingsAuditRevision";

	private static async Task<bool> RevisionIndexIsUniqueAsync(ApplicationDbContext db) =>
		await db.Database.SqlQueryRaw<bool>("""
			SELECT i.indisunique AS "Value"
			FROM pg_index i
			JOIN pg_class c ON c.oid = i.indexrelid
			JOIN pg_namespace n ON n.oid = c.relnamespace
			WHERE n.nspname = 'category_assistance' AND c.relname = 'IX_SettingsAudit_Revision'
			""").SingleAsync();

	private static async Task AuditAsync(ApplicationDbContext db, params Guid[] revisions)
	{
		Guid previous = Guid.NewGuid();
		foreach (Guid revision in revisions)
		{
			db.CategoryAssistanceSettingsAudits.Add(new CategoryAssistanceSettingsAuditEntity
			{
				Id = Guid.NewGuid(),
				Actor = "migration-test",
				PreviousRevision = previous,
				Revision = revision,
				Before = "{}",
				After = "{}",
				RecordedAt = DateTimeOffset.UtcNow
			});
			previous = revision;
		}
		await db.SaveChangesAsync();
		db.ChangeTracker.Clear();
	}

	[Fact]
	public async Task UpgradeKeepsTheRevisionIndexWithoutUniqueness()
	{
		await using var database = await MigrationDatabase.CreateAsync(fixture, Predecessor);
		await using var db = database.CreateDbContext();
		(await RevisionIndexIsUniqueAsync(db)).Should().BeTrue();

		await db.GetService<IMigrator>().MigrateAsync(SharedRevisionMigration);

		(await RevisionIndexIsUniqueAsync(db)).Should().BeFalse();
		Guid shared = Guid.NewGuid();
		await AuditAsync(db, shared, shared);
		(await db.CategoryAssistanceSettingsAudits.CountAsync(x => x.Revision == shared)).Should().Be(2);
	}

	[Fact]
	public async Task DowngradeWithDistinctRevisionsRestoresUniqueness()
	{
		await using var database = await MigrationDatabase.CreateAsync(fixture, SharedRevisionMigration);
		await using var db = database.CreateDbContext();
		Guid first = Guid.NewGuid(), second = Guid.NewGuid();
		await AuditAsync(db, first, second);

		await db.GetService<IMigrator>().MigrateAsync(Predecessor);

		(await db.Database.GetAppliedMigrationsAsync()).Last().Should().Be(Predecessor);
		(await RevisionIndexIsUniqueAsync(db)).Should().BeTrue();
		(await db.CategoryAssistanceSettingsAudits.CountAsync()).Should().Be(2);
		Func<Task> duplicate = () => AuditAsync(db, first);
		(await duplicate.Should().ThrowAsync<DbUpdateException>()).WithInnerException<PostgresException>()
			.Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
	}

	[Fact]
	public async Task DowngradeRefusesSharedRevisionsAndLeavesSchemaAndAuditUnchanged()
	{
		await using var database = await MigrationDatabase.CreateAsync(fixture, SharedRevisionMigration);
		await using var db = database.CreateDbContext();
		Guid shared = Guid.NewGuid();
		await AuditAsync(db, shared, shared);
		string[] applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();

		Func<Task> downgrade = () => db.GetService<IMigrator>().MigrateAsync(Predecessor);

		var refusal = (await downgrade.Should().ThrowAsync<PostgresException>()).Which;
		refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
		refusal.MessageText.Should().Be("Limits-only settings saves share a revision in the audit");
		(await db.Database.GetAppliedMigrationsAsync()).Should().Equal(applied);
		(await RevisionIndexIsUniqueAsync(db)).Should().BeFalse();
		(await db.CategoryAssistanceSettingsAudits.CountAsync(x => x.Revision == shared)).Should().Be(2);
	}
}
