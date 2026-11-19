using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

/// <summary>
/// The YNAB OAuth connection is a credential bound to one deployment's Data Protection keys:
/// it never leaves in a portable backup, and a restore never touches the one already there.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class YnabOAuthConnectionBackupTests(PostgresFixture server) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
	private static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");
	private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

	private PostgresFixture _source = null!;
	private PostgresFixture _target = null!;

	public async Task InitializeAsync()
	{
		_source = await server.CreateIsolatedAsync();
		try { _target = await server.CreateIsolatedAsync(); }
		catch { await _source.DisposeAsync(); throw; }
	}

	public async Task DisposeAsync()
	{
		if (_target is not null)
		{
			await _target.DisposeAsync();
		}

		if (_source is not null)
		{
			await _source.DisposeAsync();
		}
	}

	[Fact]
	public async Task Export_WithAConnection_ContainsNoTableForIt()
	{
		// Arrange
		await SeedConnectionAsync(_source, Connection("source-protected-tokens-sentinel", "source-admin-sentinel"));

		// Act
		string path = await ExportAsync(_source);

		// Assert
		try
		{
			await using SqliteConnection sqlite = new($"Data Source={path};Pooling=False");
			await sqlite.OpenAsync();
			await using SqliteCommand command = sqlite.CreateCommand();
			command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
			List<string> tables = [];
			await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
			{
				while (await reader.ReadAsync())
				{
					tables.Add(reader.GetString(0));
				}
			}

			tables.Should().NotBeEmpty();
			tables.Should().NotContain(table => table.Contains("oauth", StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public async Task Restore_OntoADeploymentWithAConnection_LeavesThatConnectionAsItWas()
	{
		// Arrange — both deployments are connected, each with its own tokens.
		await SeedConnectionAsync(_source, Connection("source-protected-tokens-sentinel", "source-admin-sentinel"));
		YnabOAuthConnectionEntity targetConnection = Connection("target-protected-tokens-sentinel", "target-admin-sentinel");
		await SeedConnectionAsync(_target, targetConnection);
		string path = await ExportAsync(_source);

		try
		{
			// Act
			BackupImportService importer = new(new Factory(_target.CreateOptions()), NullLogger<BackupImportService>.Instance);
			await using (FileStream input = File.OpenRead(path))
			{
				await importer.ImportFromSqliteAsync(input, CancellationToken.None);
			}

			// Assert
			await using ApplicationDbContext verify = _target.CreateDbContext();
			YnabOAuthConnectionEntity row = await verify.YnabOAuthConnections.AsNoTracking().SingleAsync();
			row.Should().BeEquivalentTo(targetConnection);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public async Task Restore_OntoADeploymentWithoutAConnection_DoesNotCreateOne()
	{
		// Arrange
		await SeedConnectionAsync(_source, Connection("source-protected-tokens-sentinel", "source-admin-sentinel"));
		string path = await ExportAsync(_source);

		try
		{
			// Act
			BackupImportService importer = new(new Factory(_target.CreateOptions()), NullLogger<BackupImportService>.Instance);
			await using (FileStream input = File.OpenRead(path))
			{
				await importer.ImportFromSqliteAsync(input, CancellationToken.None);
			}

			// Assert
			await using ApplicationDbContext verify = _target.CreateDbContext();
			(await verify.YnabOAuthConnections.CountAsync()).Should().Be(0);
		}
		finally
		{
			File.Delete(path);
		}
	}

	private static YnabOAuthConnectionEntity Connection(string protectedTokens, string connectedBy) => new()
	{
		Id = SingletonId,
		Status = YnabOAuthConnectionStatus.Connected,
		ProtectedTokens = protectedTokens,
		AccessTokenExpiresAtUtc = Now.AddHours(2),
		ConnectionId = Guid.NewGuid(),
		ConnectedAtUtc = Now,
		ConnectedByUserId = connectedBy,
		RefreshedAtUtc = Now.AddMinutes(30),
		StatusChangedAtUtc = Now,
		ConcurrencyStamp = Guid.NewGuid(),
	};

	private static async Task SeedConnectionAsync(PostgresFixture database, YnabOAuthConnectionEntity connection)
	{
		await using ApplicationDbContext db = database.CreateDbContext();
		db.YnabOAuthConnections.Add(connection);
		await db.SaveChangesAsync();
	}

	private static Task<string> ExportAsync(PostgresFixture database) =>
		new BackupService(new Factory(database.CreateOptions()), NullLogger<BackupService>.Instance).ExportToSqliteAsync();

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(new ApplicationDbContext(options));
	}
}
