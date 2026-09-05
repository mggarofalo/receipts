using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace Infrastructure.Tests.Services;

public class BackupFormatContractTests
{
	[Theory]
	[InlineData("0")]
	[InlineData("6")]
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

	[Theory]
	[InlineData(4, false)]
	[InlineData(4, true)]
	[InlineData(5, true)]
	public async Task Import_CanonicalTextChange_InvalidatesOldEmbeddingAndTreatsNeighbourEvidenceByFormat(int version, bool changed)
	{
		DbContextOptions<ApplicationDbContext> options = Options();
		Guid id = Guid.NewGuid(), neighbour = Guid.NewGuid();
		await using (ApplicationDbContext seed = new(options))
		{
			seed.NormalizedDescriptions.Add(new() { Id = id, CanonicalName = "Old matched text", DisplayLabel = "Human label", Embedding = new Vector(new float[] { 1, 0 }), EmbeddingModelVersion = "previous-model", NearestNeighbourId = neighbour, NearestNeighbourSimilarity = 0.81, CreatedAt = DateTimeOffset.UtcNow });
			await seed.SaveChangesAsync();
		}
		using Artifact artifact = new();
		if (version == 5)
		{
			await artifact.CreateCurrentSchema();
			artifact.Execute("INSERT INTO normalized_descriptions (id, canonical_name, status, created_at, display_label, nearest_neighbour_id, nearest_neighbour_similarity) VALUES ($id, $name, 'Active', '2024-01-01T00:00:00Z', 'Restored label', NULL, '0.66')", ("$id", id.ToString()), ("$name", "New matched text"));
		}
		else
		{
			artifact.Execute("CREATE TABLE backup_metadata (key TEXT PRIMARY KEY, value TEXT); INSERT INTO backup_metadata VALUES ('export_version', '4'); CREATE TABLE accounts (id TEXT PRIMARY KEY, name TEXT, is_active INTEGER); CREATE TABLE normalized_descriptions (id TEXT PRIMARY KEY, canonical_name TEXT, status TEXT, created_at TEXT)");
			artifact.Execute("INSERT INTO normalized_descriptions VALUES ($id, $name, 'Active', '2024-01-01T00:00:00Z')", ("$id", id.ToString()), ("$name", changed ? "New matched text" : "Old matched text"));
		}

		await artifact.Import(Importer(options));

		await using ApplicationDbContext read = new(options);
		NormalizedDescriptionEntity row = await read.NormalizedDescriptions.SingleAsync();
		if (changed)
		{
			row.Embedding.Should().BeNull();
			row.EmbeddingModelVersion.Should().BeNull();
			row.NearestNeighbourId.Should().BeNull();
			row.NearestNeighbourSimilarity.Should().Be(version == 5 ? 0.66 : null);
		}
		else
		{
			row.Embedding.Should().NotBeNull();
			row.EmbeddingModelVersion.Should().Be("previous-model");
			row.NearestNeighbourId.Should().Be(neighbour);
			row.NearestNeighbourSimilarity.Should().Be(0.81);
		}
		row.DisplayLabel.Should().Be(version == 5 ? "Restored label" : "Human label");
	}

	private static DbContextOptions<ApplicationDbContext> Options() => new DbContextOptionsBuilder<ApplicationDbContext>()
		.UseInMemoryDatabase($"backup-format-{Guid.NewGuid():N}").ConfigureWarnings(row => row.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
	private static BackupImportService Importer(DbContextOptions<ApplicationDbContext> options) => new(new Factory(options), NullLogger<BackupImportService>.Instance);
	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
	}
	private sealed class Artifact : IDisposable
	{
		private readonly string _path = Path.Combine(Path.GetTempPath(), $"backup-format-{Guid.NewGuid():N}.db");
		private readonly SqliteConnection _sqlite;
		public Artifact()
		{
			_sqlite = new(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false, ForeignKeys = false }.ToString());
			_sqlite.Open();
		}
		public async Task CreateCurrentSchema()
		{
			await BackupService.CreateSchemaAsync(_sqlite, CancellationToken.None);
			Execute("INSERT INTO backup_metadata VALUES ('export_version', '5')");
		}
		public async Task Import(BackupImportService importer)
		{
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
