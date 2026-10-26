using Application.Interfaces.Services;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

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

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Import_CurrentCanonicalRestore_InvalidatesEmbeddingAndRestoresNeighbourEvidence(bool changed)
	{
		var options = Options();
		Guid id = Guid.NewGuid();
		await using (var seed = new ApplicationDbContext(options))
		{
			seed.NormalizedDescriptions.Add(new() { Id = id, CanonicalName = "Old matched text", DisplayLabel = "Human label", Embedding = new Vector(new float[] { 1, 0 }), EmbeddingModelVersion = "previous-model", NearestNeighbourSimilarity = 0.81, CreatedAt = DateTimeOffset.UtcNow });
			await seed.SaveChangesAsync();
		}
		using Artifact artifact = new();
		await artifact.CreateCurrentSchema();
		artifact.Execute("INSERT INTO normalized_descriptions (id, canonical_name, status, created_at, display_label, nearest_neighbour_similarity) VALUES ($id, $name, 'Active', '2024-01-01T00:00:00Z', 'Restored label', '0.66')", ("$id", id.ToString()), ("$name", changed ? "New matched text" : "Old matched text"));
		await artifact.Import(Importer(options));
		await using var read = new ApplicationDbContext(options);
		var row = await read.NormalizedDescriptions.SingleAsync();
		row.Embedding.Should().BeNull("portable backups never own rebuildable vector projections");
		row.EmbeddingModelVersion.Should().BeNull();
		row.NearestNeighbourId.Should().BeNull();
		row.NearestNeighbourSimilarity.Should().Be(0.66);
		row.DisplayLabel.Should().Be("Restored label");
		row.CanonicalName.Should().Be(changed ? "New matched text" : "Old matched text");
	}

	[Fact]
	public async Task Import_FreshCanonicalWithoutVector_WorkerRebuildsSameConceptInPlace()
	{
		DbContextOptions<ApplicationDbContext> options = Options();
		Guid id = Guid.NewGuid();
		using Artifact artifact = new();
		await artifact.CreateCurrentSchema();
		artifact.Execute(
			"INSERT INTO normalized_descriptions (id, canonical_name, status, created_at, display_label) VALUES ($id, 'Organic Milk', 'Active', '2024-01-01T00:00:00Z', 'Milk')",
			("$id", id.ToString()));
		await artifact.Import(Importer(options));
		await using (ApplicationDbContext imported = new(options))
		{
			NormalizedDescriptionEntity row = await imported.NormalizedDescriptions.SingleAsync();
			row.Id.Should().Be(id);
			row.Embedding.Should().BeNull();
			row.EmbeddingModelVersion.Should().BeNull();
		}

		Factory factory = new(options);
		using ServiceProvider provider = new ServiceCollection()
			.AddSingleton<IDbContextFactory<ApplicationDbContext>>(factory)
			.AddSingleton<IEmbeddingService>(new FixedEmbeddingService())
			.BuildServiceProvider();
		EmbeddingGenerationService worker = new(
			provider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<EmbeddingGenerationService>.Instance);

		int processed = await worker.ProcessPendingEmbeddingsAsync(CancellationToken.None);

		processed.Should().Be(1);
		await using ApplicationDbContext rebuilt = new(options);
		NormalizedDescriptionEntity stored = await rebuilt.NormalizedDescriptions.SingleAsync();
		stored.Id.Should().Be(id);
		stored.CanonicalName.Should().Be("Organic Milk");
		stored.DisplayLabel.Should().Be("Milk");
		stored.Status.Should().Be(NormalizedDescriptionStatus.Active);
		stored.Embedding.Should().NotBeNull();
		stored.EmbeddingModelVersion.Should().Be(OnnxEmbeddingService.EmbeddingSpaceFingerprint);
		(await rebuilt.NormalizedDescriptions.CountAsync()).Should().Be(1,
			"rebuilding a restored concept must not create a duplicate canonical row");
	}

	private sealed class FixedEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => true;

		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
			=> Task.FromResult(Vector());

		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken)
			=> Task.FromResult(texts.Select(_ => Vector()).ToList());

		private static float[] Vector()
		{
			float[] values = new float[OnnxEmbeddingService.EmbeddingDimension];
			values[0] = 1;
			return values;
		}
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
