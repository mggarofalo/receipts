using System.Diagnostics;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class CategoryAssistanceRawRestoreLimitTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Theory]
	[InlineData(0, 5000)]
	[InlineData(100, 5000)]
	[InlineData(6000, 6000)]
	public async Task UnknownSurvivingLedgerExhaustsMaximumWithoutReducingExistingUsage(int before, int expected)
	{
		await using var database = await MigrationDatabase.CreateAsync(fixture, "0");
		await using var db = database.CreateDbContext();
		await db.Database.MigrateAsync();
		await db.Database.ExecuteSqlInterpolatedAsync($"""
			INSERT INTO category_assistance."DailyUsage" ("UtcDay", "DispatchAttempts")
			VALUES ((now() AT TIME ZONE 'UTC')::date, {before})
			""");
		string sql = await GenerateUnknownLedgerFenceAsync();
		await using var connection = await database.OpenConnectionAsync();
		// The emitted script uses psql COPY framing. Execute its actual statements and feed
		// its COPY payload through Npgsql's PostgreSQL COPY protocol.
		int copy = sql.IndexOf("COPY captured_usage", StringComparison.Ordinal);
		copy.Should().BeGreaterThan(0);
		int data = sql.IndexOf('\n', copy) + 1;
		int end = sql.IndexOf("\\.", data, StringComparison.Ordinal);
		end.Should().BeGreaterThanOrEqualTo(data);
		await using (var command = new NpgsqlCommand(sql[..copy], connection))
		{
			await command.ExecuteNonQueryAsync();
		}

		await using (var writer = await connection.BeginTextImportAsync(sql[copy..data].Trim()))
		{
			await writer.WriteAsync(sql[data..end]);
		}

		await using (var command = new NpgsqlCommand(sql[(end + 2)..], connection))
		{
			await command.ExecuteNonQueryAsync();
		}

		(await db.CategoryAssistanceDailyUsage.AsNoTracking().SingleAsync()).DispatchAttempts.Should().Be(expected);
		(await db.CategoryAssistanceSettings.AsNoTracking().SingleAsync()).Enabled.Should().BeFalse();
		(await db.CategoryAssistanceSettingsAudits.AsNoTracking().SingleAsync()).Actor.Should().Be("backup_restore");
	}

	private static async Task<string> GenerateUnknownLedgerFenceAsync()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Receipts.slnx")))
		{
			directory = directory.Parent;
		}

		directory.Should().NotBeNull("the script lives in the repository containing the test output");
		string ledger = Path.GetTempFileName();
		try
		{
			await File.WriteAllTextAsync(ledger, "");
			var start = new ProcessStartInfo("dotnet")
			{
				WorkingDirectory = directory!.FullName,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			foreach (string argument in new[] { "run", "scripts/restore.cs", "--", "--test-fence-sql", ledger, "--unknown" })
			{
				start.ArgumentList.Add(argument);
			}

			using var process = Process.Start(start)!;
			Task<string> output = process.StandardOutput.ReadToEndAsync();
			Task<string> error = process.StandardError.ReadToEndAsync();
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
			try { await process.WaitForExitAsync(timeout.Token); }
			catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
			process.ExitCode.Should().Be(0, await error);
			return await output;
		}
		finally { File.Delete(ledger); }
	}
}
