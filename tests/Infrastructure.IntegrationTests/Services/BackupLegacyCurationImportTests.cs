using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class BackupLegacyCurationImportTests(PostgresFixture server) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
	private PostgresFixture _source = null!;
	private PostgresFixture _target = null!;

	public async Task InitializeAsync()
	{
		_source = await server.CreateIsolatedAsync();
		try
		{
			_target = await server.CreateIsolatedAsync();
			await SeededTemplateCleanup.RemoveFromFreshDatabaseAsync(_target);
		}
		catch
		{
			if (_target is not null)
			{
				await _target.DisposeAsync();
			}

			await _source.DisposeAsync();
			throw;
		}
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

	[Theory]
	[InlineData("Milk", false)]
	[InlineData("Bread", true)]
	[InlineData("milk", true)]
	[InlineData("Milk ", true)]
	public async Task CurrentImport_RestoresSourceCurationAndProtectedCanonicalState(string description, bool changed)
	{
		DbContextOptions<ApplicationDbContext> options = _target.CreateOptions();
		Guid receipt = Guid.NewGuid(), item = Guid.NewGuid(), canonical = Guid.NewGuid(), template = Guid.NewGuid();
		Guid receiptRevision, itemRevision;
		await using (var seed = new ApplicationDbContext(options))
		{
			seed.Receipts.Add(new() { Id = receipt, Location = "Store", Date = new(2024, 1, 1) });
			seed.NormalizedDescriptions.Add(new() { Id = canonical, CanonicalName = "Milk canonical", DisplayLabel = "Chosen milk label", NearestNeighbourSimilarity = .86, CreatedAt = DateTimeOffset.UtcNow });
			seed.ReceiptItems.Add(new() { Id = item, ReceiptId = receipt, Description = "Milk", Quantity = 1, Category = "Food", NormalizedDescriptionId = canonical, NormalizedDescriptionMatchScore = .79 });
			seed.ItemTemplates.Add(new() { Id = template, Name = "Milk template", NormalizedDescriptionId = canonical });
			await seed.SaveChangesAsync();
			receiptRevision = await seed.Receipts.AsNoTracking().Where(x => x.Id == receipt).Select(x => x.NormalizationRevision).SingleAsync();
			itemRevision = await seed.ReceiptItems.AsNoTracking().Where(x => x.Id == item).Select(x => x.NormalizationRevision).SingleAsync();
		}
		string path = Path.Combine(Path.GetTempPath(), $"current-curation-{Guid.NewGuid()}.db");
		try
		{
			using (var sqlite = new SqliteConnection($"Data Source={path};Pooling=False"))
			{
				sqlite.Open(); await BackupService.CreateSchemaAsync(sqlite, CancellationToken.None);
				Execute(sqlite, "INSERT INTO backup_metadata VALUES('export_version','13')");
				Execute(sqlite, "INSERT INTO receipts VALUES($id,'Store','2024-01-01','0','USD',NULL,NULL)", ("$id", receipt.ToString()));
				Execute(sqlite, "INSERT INTO normalized_descriptions(id,canonical_name,status,created_at,display_label,nearest_neighbour_similarity) VALUES($id,'Milk canonical','PendingReview','2024-01-01T00:00:00Z','Chosen milk label','0.86')", ("$id", canonical.ToString()));
				Execute(sqlite, "INSERT INTO receipt_items(id,receipt_id,description,quantity,unit_price,unit_price_currency,total_amount,total_amount_currency,category,normalized_description_id,normalized_description_match_score) VALUES($id,$receipt,$description,'2','3','USD','6','USD','Food',$canonical,$score)", ("$id", item.ToString()), ("$receipt", receipt.ToString()), ("$description", description), ("$canonical", changed ? DBNull.Value : canonical.ToString()), ("$score", changed ? DBNull.Value : "0.79"));
				Execute(sqlite, "INSERT INTO item_templates(id,name,normalized_description_id) VALUES($id,'Updated template',$canonical)", ("$id", template.ToString()), ("$canonical", canonical.ToString()));
				await CurrentBackupFixture.CompleteAsync(sqlite, _source);
				Execute(sqlite, "INSERT INTO source_revision_archive VALUES('Receipts',$id,$revision)", ("$id", receipt.ToString()), ("$revision", receiptRevision.ToString()));
				Execute(sqlite, "INSERT INTO source_revision_archive VALUES('ReceiptItems',$id,$revision)", ("$id", item.ToString()), ("$revision", itemRevision.ToString()));
			}
			await using var stream = File.OpenRead(path);
			await new BackupImportService(new Factory(options), NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, CancellationToken.None);
			await using var read = new ApplicationDbContext(options);
			var restored = await read.ReceiptItems.SingleAsync();
			restored.Description.Should().Be(description); restored.Quantity.Should().Be(2);
			restored.NormalizedDescriptionId.Should().Be(changed ? null : canonical);
			restored.NormalizedDescriptionMatchScore.Should().Be(changed ? null : .79);
			(await read.ItemTemplates.SingleAsync()).NormalizedDescriptionId.Should().Be(canonical);
			var concept = await read.NormalizedDescriptions.SingleAsync();
			concept.DisplayLabel.Should().Be("Chosen milk label"); concept.NearestNeighbourId.Should().BeNull();
			concept.NearestNeighbourSimilarity.Should().Be(.86); concept.Status.Should().Be(NormalizedDescriptionStatus.PendingReview);
		}
		finally { File.Delete(path); }
	}


	private static void Execute(SqliteConnection sqlite, string sql, params (string Name, object Value)[] parameters)
	{
		using SqliteCommand command = sqlite.CreateCommand();
		command.CommandText = sql;
		foreach ((string name, object value) in parameters)
		{
			command.Parameters.AddWithValue(name, value);
		}

		command.ExecuteNonQuery();
	}

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(new ApplicationDbContext(options));
	}
}
