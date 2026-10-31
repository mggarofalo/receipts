using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Services;

public class BackupLegacyCurationTests
{
	[Theory]
	[InlineData(0, "Milk", false)]
	[InlineData(1, "Milk", false)]
	[InlineData(2, "Milk", false)]
	[InlineData(3, "Milk", false)]
	[InlineData(4, "Milk", false)]
	[InlineData(4, "Bread", true)]
	[InlineData(4, "milk", true)]
	[InlineData(4, "Milk ", true)]
	public async Task LegacyPartialImport_RejectsMissingSourceParent_BeforeOpeningDestination(int version, string description, bool changed)
	{
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase($"legacy-curation-{Guid.NewGuid():N}")
			.ConfigureWarnings(row => row.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
		Guid receiptId = Guid.NewGuid(), itemId = Guid.NewGuid(), canonicalId = Guid.NewGuid(), templateId = Guid.NewGuid();
		await using (ApplicationDbContext seed = new(options))
		{
			seed.Receipts.Add(new() { Id = receiptId, Location = "Store", Date = new(2024, 1, 1) });
			seed.NormalizedDescriptions.Add(new() { Id = canonicalId, CanonicalName = "Milk canonical", DisplayLabel = "Chosen milk label", Status = NormalizedDescriptionStatus.Active, NearestNeighbourSimilarity = 0.86, CreatedAt = DateTimeOffset.UtcNow });
			seed.ReceiptItems.Add(new() { Id = itemId, ReceiptId = receiptId, Description = "Milk", Quantity = 1, UnitPrice = 3, TotalAmount = 3, Category = "Food", NormalizedDescriptionId = canonicalId });
			seed.ItemTemplates.Add(new() { Id = templateId, Name = "Milk template", NormalizedDescriptionId = canonicalId });
			await seed.SaveChangesAsync();
		}
		string path = Path.Combine(Path.GetTempPath(), $"legacy-curation-{Guid.NewGuid():N}.db");
		try
		{
			using (SqliteConnection sqlite = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
			{
				sqlite.Open();
				if (version > 0)
				{
					Execute(sqlite, "CREATE TABLE backup_metadata (key TEXT PRIMARY KEY, value TEXT); INSERT INTO backup_metadata VALUES ('export_version', $version)", ("$version", version.ToString()));
				}
				if (version >= 3)
				{
					Execute(sqlite, "CREATE TABLE accounts (id TEXT PRIMARY KEY, name TEXT, is_active INTEGER)");
				}
				// Literal legacy schema: these columns intentionally do not come from the current exporter.
				Execute(sqlite, "CREATE TABLE receipt_items (id TEXT PRIMARY KEY, receipt_id TEXT, receipt_item_code TEXT, description TEXT, quantity TEXT, unit_price TEXT, unit_price_currency TEXT, total_amount TEXT, total_amount_currency TEXT, category TEXT, subcategory TEXT)");
				Execute(sqlite, "INSERT INTO receipt_items VALUES ($id, $receipt, NULL, $description, '2', '3', 'USD', '6', 'USD', 'Food', NULL)", ("$id", itemId.ToString()), ("$receipt", receiptId.ToString()), ("$description", description));
				Execute(sqlite, "CREATE TABLE item_templates (id TEXT PRIMARY KEY, name TEXT, default_category TEXT, default_subcategory TEXT, default_unit_price TEXT, default_unit_price_currency TEXT, default_item_code TEXT, description TEXT)");
				Execute(sqlite, "INSERT INTO item_templates VALUES ($id, 'Updated template', NULL, NULL, NULL, NULL, NULL, NULL)", ("$id", templateId.ToString()));
				if (version == 4)
				{
					Execute(sqlite, "CREATE TABLE normalized_descriptions (id TEXT PRIMARY KEY, canonical_name TEXT, status TEXT, created_at TEXT)");
					Execute(sqlite, "INSERT INTO normalized_descriptions VALUES ($id, 'Milk canonical', 'PendingReview', '2024-01-01T00:00:00Z')", ("$id", canonicalId.ToString()));
				}
			}
			BackupImportService importer = new(new NeverFactory(), NullLogger<BackupImportService>.Instance);
			await using (FileStream stream = File.OpenRead(path))
			{
				Func<Task> import = () => importer.ImportFromSqliteAsync(stream, CancellationToken.None);
				await import.Should().ThrowAsync<InvalidOperationException>().WithMessage("*missing source receipt*");
			}
			await using ApplicationDbContext read = new(options);
			ReceiptItemEntity item = await read.ReceiptItems.SingleAsync();
			changed.Should().Be(!string.Equals(description, "Milk", StringComparison.Ordinal));
			item.Description.Should().Be("Milk");
			item.Quantity.Should().Be(1);
			item.NormalizedDescriptionId.Should().Be(canonicalId);
			(await read.ItemTemplates.SingleAsync()).NormalizedDescriptionId.Should().Be(canonicalId);
			NormalizedDescriptionEntity canonical = await read.NormalizedDescriptions.SingleAsync();
			canonical.DisplayLabel.Should().Be("Chosen milk label");
			canonical.NearestNeighbourId.Should().BeNull();
			canonical.NearestNeighbourSimilarity.Should().Be(0.86);
			canonical.Status.Should().Be(NormalizedDescriptionStatus.Active);
		}
		finally
		{
			File.Delete(path);
		}
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

	private sealed class NeverFactory : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => throw new Exception("Destination must not be opened");
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => throw new Exception("Destination must not be opened");
	}

}
