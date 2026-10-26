using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Services;

public class BackupFormatContractTests
{
	[Theory]
	[InlineData("0")]
	[InlineData("12")]
	[InlineData("invalid")]
	[InlineData("")]
	public async Task Import_ExplicitUnsupportedOrMalformedVersion_IsRejected(string version)
	{
		using Artifact artifact = new();
		artifact.Execute("CREATE TABLE backup_metadata (key TEXT PRIMARY KEY, value TEXT)");
		artifact.Execute("INSERT INTO backup_metadata VALUES ('export_version', $version)", ("$version", version));
		BackupImportService importer = Importer(Options());

		Func<Task> import = () => artifact.Import(importer);
		await import.Should().ThrowAsync<InvalidOperationException>().WithMessage("*export_version*");
	}

	[Theory]
	[InlineData("accepted_duplicate_pairs")]
	[InlineData("normalized_descriptions")]
	public async Task Import_CurrentSnapshotMissingDurableTable_RejectsBeforeUpdatingExistingRows(string table)
	{
		DbContextOptions<ApplicationDbContext> options = Options();
		Guid receiptId = Guid.NewGuid();
		await using (ApplicationDbContext seed = new(options))
		{
			seed.Receipts.Add(new() { Id = receiptId, Location = "Before", Date = new(2024, 1, 1) });
			await seed.SaveChangesAsync();
		}
		using Artifact artifact = new();
		await artifact.CreateCurrentSchema();
		artifact.Execute($"DROP TABLE {table}");
		artifact.Execute("INSERT INTO receipts VALUES ($id, 'After', '2024-01-01', '0', 'USD', NULL, NULL)", ("$id", receiptId.ToString()));

		Func<Task> import = () => artifact.Import(Importer(options));
		await import.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*missing required '{table}'*");

		await using ApplicationDbContext read = new(options);
		(await read.Receipts.SingleAsync()).Location.Should().Be("Before");
	}

	private static DbContextOptions<ApplicationDbContext> Options() => new DbContextOptionsBuilder<ApplicationDbContext>()
		.UseInMemoryDatabase($"backup-format-{Guid.NewGuid():N}").ConfigureWarnings(row => row.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
	private static BackupImportService Importer(DbContextOptions<ApplicationDbContext> options) => new(new Factory(options), NullLogger<BackupImportService>.Instance);
	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext()
		{
			var db = new ApplicationDbContext(options); CurrentBackupFixture.Seed(db); return db;
		}
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
	}
	private sealed class Artifact : IDisposable
	{
		private readonly string _path = Path.Combine(Path.GetTempPath(), $"backup-format-{Guid.NewGuid():N}.db");
		private readonly SqliteConnection _sqlite;
		private bool _current;
		public Artifact()
		{
			_sqlite = new(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false, ForeignKeys = false }.ToString());
			_sqlite.Open();
		}
		public async Task CreateCurrentSchema()
		{
			await BackupService.CreateSchemaAsync(_sqlite, CancellationToken.None);
			_current = true;
			Execute("INSERT INTO backup_metadata VALUES ('export_version', '10')");
		}
		public async Task Import(BackupImportService importer)
		{
			if (_current)
			{
				await CurrentBackupFixture.CompleteAsync(_sqlite);
			}

			_sqlite.Close();
			await using FileStream stream = File.OpenRead(_path);
			await importer.ImportFromSqliteAsync(stream, CancellationToken.None);
		}
		public void Execute(string sql, params (string Name, object Value)[] parameters)
		{
			using SqliteCommand command = _sqlite.CreateCommand();
			command.CommandText = sql;
			foreach ((string name, object value) in parameters)
			{
				command.Parameters.AddWithValue(name, value);
			}
			command.ExecuteNonQuery();
		}
		public void Dispose()
		{
			_sqlite.Dispose();
			File.Delete(_path);
		}
	}
}
