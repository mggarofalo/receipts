using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.IntegrationTests;

// RECEIPTS-1110. The ChatGPT proposal's MatchScore was written and never read. plpgsql resolves
// columns only when a function runs, so a function still naming it would fail at run time.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class DropProposalMatchScoreMigrationTests(PostgresFixture fixture)
{
	private const string Migration = "20261101110308_DropNormalizationAiProposalMatchScore";

	[Fact]
	public async Task NoDatabaseFunctionReferencesTheProposalScore()
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();

		List<string> offenders = await db.Database.SqlQuery<string>($"""
			SELECT p.proname AS "Value" FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
			WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND p.prosrc LIKE '%"MatchScore"%'
			""").ToListAsync();

		offenders.Should().BeEmpty();
	}

	[Fact]
	public async Task DownRestoresTheColumnAndUpRemovesItAgain()
	{
		await using MigrationDatabase database = await MigrationDatabase.CreateAsync(fixture, Migration);
		await using ApplicationDbContext db = database.CreateDbContext();
		IMigrator migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
		string[] applied = [.. await db.Database.GetAppliedMigrationsAsync()];
		string previous = applied[Array.IndexOf(applied, Migration) - 1];
		(await ColumnCountAsync(db)).Should().Be(0);

		await migrator.MigrateAsync(previous);
		(await ColumnCountAsync(db)).Should().Be(1);

		await migrator.MigrateAsync(Migration);
		(await ColumnCountAsync(db)).Should().Be(0);
	}

	private static Task<int> ColumnCountAsync(ApplicationDbContext db) =>
		db.Database.SqlQuery<int>($"""
			SELECT count(*)::int AS "Value" FROM information_schema.columns
			WHERE table_schema = 'matching' AND table_name = 'NormalizationAiProposals' AND column_name = 'MatchScore'
			""").SingleAsync();
}
