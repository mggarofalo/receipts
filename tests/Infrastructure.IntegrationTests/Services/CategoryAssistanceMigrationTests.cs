using Common;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class CategoryAssistanceMigrationTests(PostgresFixture server) : IClassFixture<PostgresFixture>
{
	private sealed record SettingsRow(bool Enabled, string? Provider, string? Model, int DailyLimit, int PendingLimit, Guid Revision);

	// These schemas predate later Settings columns, so the current store cannot read them: project the known columns.
	private static Task<SettingsRow> ReadSettingsAsync(ApplicationDbContext db) =>
		db.CategoryAssistanceSettings.AsNoTracking()
			.Select(x => new SettingsRow(x.Enabled, x.Provider, x.Model, x.DailyLimit, x.PendingLimit, x.Revision)).SingleAsync();

	[Fact]
	public async Task DowngradeAllowsEmptyDefaultsButRetainsSettingsAndAuditAfterARealChange()
	{
		await using var fixture = await server.CreateIsolatedAsync("20261027110503_RaiseCategoryAssistanceDailyLimit");
		await using var db = fixture.CreateDbContext();
		string[] applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
		int foundationIndex = Array.IndexOf(applied, "20261026114208_AddCategoryAssistanceFoundation");
		foundationIndex.Should().BeGreaterThan(0);
		var migrator = db.GetService<IMigrator>();
		var defaults = await ReadSettingsAsync(db);
		defaults.Enabled.Should().BeFalse();
		(await db.CategoryAssistanceSettingsAudits.CountAsync()).Should().Be(0);

		// An untouched installation can still downgrade and replay the foundation migration.
		await migrator.MigrateAsync(applied[foundationIndex - 1]);
		(await db.Database.GetAppliedMigrationsAsync()).Should().Equal(applied.Take(foundationIndex));
		await migrator.MigrateAsync(applied[^1]);
		(await ReadSettingsAsync(db)).Should().Be(defaults);

		string actor = "assistance-migration-" + Guid.NewGuid();
		db.Users.Add(new ApplicationUser { Id = actor, UserName = actor });
		var role = new IdentityRole(AppRoles.Admin);
		db.Roles.Add(role);
		db.UserRoles.Add(new IdentityUserRole<string> { UserId = actor, RoleId = role.Id });
		await db.SaveChangesAsync();
		// Keep this change valid in earlier schemas so downgrade reaches the audit-retention guard. The audited write
		// mirrors the settings store, which reads columns this schema does not have yet.
		var changed = defaults with { PendingLimit = defaults.PendingLimit + 1, Revision = Guid.NewGuid() };
		await db.CategoryAssistanceSettings.ExecuteUpdateAsync(s => s
			.SetProperty(x => x.PendingLimit, changed.PendingLimit).SetProperty(x => x.Revision, changed.Revision));
		db.CategoryAssistanceSettingsAudits.Add(new()
		{
			Id = Guid.NewGuid(),
			Actor = actor,
			PreviousRevision = defaults.Revision,
			Revision = changed.Revision,
			Before = System.Text.Json.JsonSerializer.Serialize(defaults),
			After = System.Text.Json.JsonSerializer.Serialize(changed),
			RecordedAt = DateTimeOffset.UtcNow
		});
		await db.SaveChangesAsync();
		var originalAudit = await db.CategoryAssistanceSettingsAudits.AsNoTracking().SingleAsync();
		originalAudit.Actor.Should().Be(actor);
		originalAudit.PreviousRevision.Should().Be(defaults.Revision);
		originalAudit.Revision.Should().Be(changed.Revision);

		try
		{
			Func<Task> downgrade = () => migrator.MigrateAsync(applied[foundationIndex - 1]);
			var refusal = (await downgrade.Should().ThrowAsync<PostgresException>()).Which;
			refusal.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
			refusal.MessageText.Should().Be("Category assistance settings audit must be retained; downgrade refused");
			(await db.Database.GetAppliedMigrationsAsync()).Should().Equal(applied.Take(foundationIndex + 1));
			(await ReadSettingsAsync(db)).Should().Be(changed);
			(await db.CategoryAssistanceSettingsAudits.AsNoTracking().SingleAsync()).Should().BeEquivalentTo(originalAudit);

			// The refusal must also leave the audit's database immutability guard installed.
			Func<Task> delete = () => db.Database.ExecuteSqlInterpolatedAsync(
				$"DELETE FROM category_assistance.\"SettingsAudit\" WHERE \"Revision\" = {changed.Revision}");
			(await delete.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
		}
		finally
		{
			await migrator.MigrateAsync(applied[^1]);
		}
	}
}
