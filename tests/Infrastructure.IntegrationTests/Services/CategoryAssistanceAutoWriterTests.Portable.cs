using System.Text.Json;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class CategoryAssistanceAutoWriterTests
{
	private const string JevMigration = "20261030220732_AddJevAutomaticWrites";

	[Fact]
	public async Task PortableExportOmitsJevActivityButKeepsTheJevAssignment()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95, portable: true);
		(await Writer().ApplyNextAsync()).Should().BeTrue();
		var assignment = await CurrentAsync(seed.Items[0]);

		string path = await ExportAsync();
		try
		{
			(await ArchiveScalarAsync(path, "SELECT count(*) FROM historical_operation_archive")).Should().Be(0L);
			var payload = (string)(await ArchiveScalarAsync(path,
				$"SELECT payload FROM taxonomy_archive WHERE kind = 'assignment' AND id = '{JsonSerializer.Serialize(assignment.Id)}'"))!;
			var archived = JsonSerializer.Deserialize<PurchaseClassificationEntity>(payload)!;
			archived.Source.Should().Be("jev");
			archived.CategoryId.Should().Be(seed.Root);
			archived.Reason.Should().Be(assignment.Reason);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task RestoreKeepsTheDestinationsOwnJevActivity()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95, portable: true);
		(await Writer().ApplyNextAsync()).Should().BeTrue();
		var assignment = await CurrentAsync(seed.Items[0]);
		var operation = (await Historical().ListAutomaticAsync(seed.Actor, 0, 10)).Items.Single();

		string path = await ExportAsync();
		try { await ImportAsync(path); }
		finally { File.Delete(path); }

		(await CurrentAsync(seed.Items[0])).Should().BeEquivalentTo(assignment);
		(await Historical().ListAutomaticAsync(seed.Actor, 0, 10)).Items.Should().ContainSingle()
			.Which.OperationId.Should().Be(operation.OperationId);
	}

	[Fact]
	public async Task ArchiveThatClaimsAJevOperationIsRejected()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95, portable: true);
		(await Writer().ApplyNextAsync()).Should().BeTrue();
		HistoricalOperationEntity header;
		await using (var db = fixture.CreateDbContext())
		{
			header = await db.HistoricalOperations.AsNoTracking().SingleAsync();
		}
		var assignment = await CurrentAsync(seed.Items[0]);
		string path = await ExportAsync();
		string tampered = path + ".jev";
		try
		{
			File.Copy(path, tampered);
			header.Id = Guid.NewGuid();
			await using (var sqlite = new SqliteConnection($"Data Source={tampered};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using var command = sqlite.CreateCommand();
				command.CommandText = "INSERT INTO historical_operation_archive(kind,id,payload) VALUES('operation',$id,$payload)";
				command.Parameters.AddWithValue("$id", JsonSerializer.Serialize(header.Id));
				command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(header));
				await command.ExecuteNonQueryAsync();
			}

			// Restore into a destination with no Jev activity of its own, so only the incoming header can be refused.
			await ReplaceDatabaseAsync();
			Func<Task> import = () => ImportAsync(tampered);
			(await import.Should().ThrowAsync<InvalidOperationException>()).Which.InnerException.Should()
				.BeOfType<InvalidDataException>().Which.Message.Should().Be("Automatic historical operations cannot be restored yet.");
			await using (var rejected = fixture.CreateDbContext())
			{
				(await rejected.HistoricalOperations.CountAsync()).Should().Be(0);
				(await rejected.Classifications.AnyAsync(x => x.Id == assignment.Id)).Should().BeFalse();
			}

			// The same archive without the injected header restores, including the jev assignment and its evidence.
			await ImportAsync(path);
		}
		finally { File.Delete(path); File.Delete(tampered); }

		await using var verify = fixture.CreateDbContext();
		(await verify.HistoricalOperations.CountAsync()).Should().Be(0);
		(await CurrentAsync(seed.Items[0])).Should().BeEquivalentTo(assignment);
	}

	[Fact]
	public async Task DowngradeRefusesWhileJevHistoryIsRetained()
	{
		await using (var clean = fixture.CreateDbContext())
		{
			// Without automatic Jev history the migration is reversible.
			var migrator = clean.GetService<IMigrator>();
			string[] applied = [.. await clean.Database.GetAppliedMigrationsAsync()];
			string previous = applied[Array.IndexOf(applied, JevMigration) - 1];
			await migrator.MigrateAsync(previous);
			(await clean.Database.GetAppliedMigrationsAsync()).Should().NotContain(JevMigration);
			await migrator.MigrateAsync(applied[^1]);
		}

		var seed = await SeedAsync(root: 0.95, leaf: 0.95, portable: true);
		(await Writer().ApplyNextAsync()).Should().BeTrue();
		await using var db = fixture.CreateDbContext();
		string[] current = [.. await db.Database.GetAppliedMigrationsAsync()];
		current[^1].Should().Be(JevMigration);

		Func<Task> downgrade = () => db.GetService<IMigrator>().MigrateAsync(current[^2]);

		var refusal = (await downgrade.Should().ThrowAsync<PostgresException>()).Which;
		refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
		refusal.MessageText.Should().Be("Automatic Jev history is retained");
		(await db.Database.GetAppliedMigrationsAsync()).Should().Equal(current);
		(await db.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.Attempt)).State.Should().Be("applied");
		(await CurrentAsync(seed.Items[0])).Source.Should().Be("jev");
	}

	private Task<string> ExportAsync() =>
		new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();

	private async Task ImportAsync(string path)
	{
		await using var stream = File.OpenRead(path);
		await new BackupImportService(new Factory(fixture), NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, default);
	}

	private static async Task<object?> ArchiveScalarAsync(string path, string sql)
	{
		await using var sqlite = new SqliteConnection($"Data Source={path};Pooling=False");
		await sqlite.OpenAsync();
		await using var command = sqlite.CreateCommand();
		command.CommandText = sql;
		return await command.ExecuteScalarAsync();
	}
}
