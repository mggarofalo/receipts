using System.Text.Json;
using Application.Interfaces.Services;
using Application.Taxonomy;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Configurations;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class LegacyPortableTaxonomyImportTests
{
	private static Guid Id(int n) => Guid.Parse($"10470000-0000-0000-0000-{n:D12}");
	private sealed class Actor : ICurrentUserAccessor
	{
		public string? UserId => "legacy-portable-test";
		public Guid? ApiKeyId => null;
		public string? IpAddress => null;
		public string? UserAgent => null;
	}
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => new(fixture.CreateOptions(), new Actor()); }
	private static BackupImportService Importer(PostgresFixture fixture) => new(new Factory(fixture), NullLogger<BackupImportService>.Instance);
	private static async Task ImportAsync(PostgresFixture fixture, string path)
	{ await using var input = File.OpenRead(path); await Importer(fixture).ImportFromSqliteAsync(input, CancellationToken.None); }
	private static async Task EditAsync(string path, string sql)
	{
		await using var db = new SqliteConnection($"Data Source={path};Pooling=False");
		await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = sql;
		await command.ExecuteNonQueryAsync();
	}

	// Construct the pre-cutover schema directly. No v10 export or taxonomy/deletion tables exist.
	private static async Task<string> LegacyFileAsync(int version)
	{
		string path = Path.Combine(Path.GetTempPath(), $"legacy-taxonomy-{Guid.NewGuid():N}.db");
		await using var db = new SqliteConnection($"Data Source={path};Pooling=False");
		await db.OpenAsync();
		await BackupService.CreateSchemaAsync(db, CancellationToken.None, exportVersion: 9);
		await using var command = db.CreateCommand();
		command.CommandText = $"""
            INSERT INTO backup_metadata VALUES('export_version','{version}'),('format','receipts-sqlite-backup');
            INSERT INTO categories(id,name,description,is_active) VALUES('{Id(1)}','Food',NULL,1);
            INSERT INTO subcategories(id,category_id,name,description,is_active) VALUES('{Id(2)}','{Id(1)}','Dairy',NULL,1);
            INSERT INTO normalized_descriptions(id,canonical_name,status,created_at,display_label) VALUES('{Id(3)}','Product is not a category','Active','2026-10-24','Readable product');
            INSERT INTO receipts(id,location,date,tax_amount,tax_amount_currency) VALUES('{Id(4)}',' Original shop ','2020-01-01','0.13','USD');
            INSERT INTO item_templates(id,name,default_category,normalized_description_id) VALUES('{Id(5)}','Template is not a category','Template-only default','{Id(3)}');
            """;
		await command.ExecuteNonQueryAsync();
		(string Category, string? Child)[] labels = [("Food", "Dairy"), ("Grocery", "Dairy"), ("Household", "Dairy"), ("Grocery", null), ("", "orphan"), ("Uncategorized", null)];
		for (int i = 0; i < labels.Length; i++)
		{
			command.CommandText = $"""
                INSERT INTO receipt_items(id,receipt_id,description,quantity,unit_price,unit_price_currency,total_amount,total_amount_currency,category,subcategory,normalized_description_id)
                VALUES('{Id(10 + i)}','{Id(4)}',' RAW milk {i} ','0.5','2.46','USD','1.23','USD',$category,$child,'{Id(3)}');
                """;
			command.Parameters.Clear(); command.Parameters.AddWithValue("$category", labels[i].Category);
			command.Parameters.AddWithValue("$child", (object?)labels[i].Child ?? DBNull.Value);
			await command.ExecuteNonQueryAsync();
		}
		if (version >= 7)
		{
			command.Parameters.Clear();
			command.CommandText = "CREATE TABLE normalization_catalog(kind TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(kind,id))";
			await command.ExecuteNonQueryAsync();
			for (int i = 0; i < labels.Length; i++)
			{
				foreach (var record in new[] {
					(Kind: "assignment", Payload: JsonSerializer.Serialize(new PortableCatalogArchive.Assignment(Id(10+i), "legacy_unknown", null, Id(100+i)))),
					(Kind: "review_evidence", Payload: JsonSerializer.Serialize(new PortableCatalogArchive.ReviewEvidence(Id(10+i), NormalizationReviewState.NeedsReview, null, null, null, null, null, null, null, null))) })
				{
					command.Parameters.Clear(); command.CommandText = "INSERT INTO normalization_catalog VALUES($kind,$id,$payload)";
					command.Parameters.AddWithValue("$kind", record.Kind); command.Parameters.AddWithValue("$id", Id(10 + i).ToString()); command.Parameters.AddWithValue("$payload", record.Payload);
					await command.ExecuteNonQueryAsync();
				}
			}
		}
		return path;
	}

	[Theory]
	[InlineData(6)]
	[InlineData(8)]
	[InlineData(9)]
	public async Task OldBackup_UsesOnlySourceEvidence_ReplaysWithoutWrites_AndRoundTripsInitialHistory(int version)
	{
		string path = await LegacyFileAsync(version); string? exported = null;
		var target = new PostgresFixture(); await target.InitializeAsync();
		var restored = new PostgresFixture(); await restored.InitializeAsync();
		try
		{
			DateTimeOffset before = DateTimeOffset.UtcNow;
			await ImportAsync(target, path);
			LegacyMigrationPlan plan;
			await using (var db = target.CreateDbContext())
			{
				var migration = await db.LegacyMigrations.SingleAsync();
				migration.SourceDataset.Should().StartWith($"portable-v{version}:sha256:");
				migration.ObservedAt.Should().BeOnOrAfter(before);
				plan = JsonSerializer.Deserialize<LegacyMigrationPlan>(migration.PlanJson)!;
				var nodes = await db.TaxonomyNodes.Where(n => n.RevisionId == migration.RevisionId).ToListAsync();
				nodes.Should().HaveCount(7);
				nodes.Should().NotContain(n => n.Name == "Product is not a category" || n.Name == "Template-only default");
				var grocery = nodes.Single(n => n.Name == "Grocery");
				var household = nodes.Single(n => n.Name == "Household");
				nodes.Where(n => n.Name == "Dairy").Select(n => n.ParentId).Should().BeEquivalentTo(new Guid?[] { nodes.Single(n => n.Name == "Food").CategoryId, grocery.CategoryId, household.CategoryId });
				var assignments = await db.Classifications.OrderBy(a => a.ReceiptItemId).ToListAsync();
				assignments.Should().HaveCount(6).And.OnlyContain(a => a.Source == "legacy_import" && a.RecordedAt == migration.ObservedAt && a.Sequence == 1);
				assignments.Single(a => a.ReceiptItemId == Id(13)).CategoryId.Should().Be(grocery.CategoryId);
				assignments.Single(a => a.ReceiptItemId == Id(14)).CategoryId.Should().BeNull();
				var unresolved = await db.LegacyMigrationItems.SingleAsync(a => a.ReceiptItemId == Id(14));
				JsonSerializer.Deserialize<LegacyItemResolution>(unresolved.EvidenceJson)!.Evidence.Subcategory.Should().Be("orphan");
				assignments.Single(a => a.ReceiptItemId == Id(15)).CategoryId.Should().Be(nodes.Single(n => n.Name == "Uncategorized").CategoryId);
				var items = await db.ReceiptItems.IgnoreAutoIncludes().OrderBy(i => i.Id).ToListAsync();
				items.Should().OnlyContain(i => i.TotalAmount == 1.23m && i.UnitPrice == 2.46m && i.Quantity == .5m && i.NormalizedDescriptionId == Id(3));
				items[1].Description.Should().Be(" RAW milk 1 "); items[1].Category.Should().Be("Grocery"); items[1].Subcategory.Should().Be("Dairy");
				// An exact replay may not rewind a later ordinary business edit.
				(await db.Receipts.SingleAsync(r => r.Id == Id(4))).Location = "Later store edit";
				await db.SaveChangesAsync();
			}
			await ImportAsync(target, path);
			await using (var db = target.CreateDbContext())
			{
				(await db.Receipts.SingleAsync(r => r.Id == Id(4))).Location.Should().Be("Later store edit");
				(await db.LegacyMigrations.SingleAsync()).PlanJson.Should().Be(JsonSerializer.Serialize(plan));
				(await db.TaxonomyRevisions.CountAsync()).Should().Be(2);
				(await db.Classifications.CountAsync()).Should().Be(6);
				(await db.LegacyMigrationItems.CountAsync()).Should().Be(6);
			}
			await new ReceiptItemRepository(new Factory(target)).CreateAsync([new ReceiptItemEntity {
				Id = Id(70), ReceiptId = Id(4), Description = "Ordinary later save", Category = "Grocery", Subcategory = "Dairy",
				Quantity = 1, UnitPrice = 2, TotalAmount = 2 }], CancellationToken.None);
			exported = await new BackupService(new Factory(target), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
			await ImportAsync(restored, exported);
			await using (var db = restored.CreateDbContext())
			{
				(await db.LegacyMigrations.SingleAsync()).PlanJson.Should().Be(JsonSerializer.Serialize(plan));
				(await db.Classifications.Where(a => a.Source == "legacy_import").Select(a => a.Id).ToListAsync()).Should().BeEquivalentTo(plan.Items.Select(i => i.AssignmentId));
				var ordinary = await db.Classifications.SingleAsync(a => a.ReceiptItemId == Id(70));
				ordinary.Source.Should().Be("legacy_write"); ordinary.LegacyCategory.Should().Be("Grocery"); ordinary.LegacySubcategory.Should().Be("Dairy");
				(await db.ReceiptItems.SumAsync(i => i.TotalAmount)).Should().Be(9.38m);
				(await db.ReceiptItems.SingleAsync(i => i.Id == Id(11))).Description.Should().Be(" RAW milk 1 ");
				(await db.NormalizedDescriptions.SingleAsync(p => p.Id == Id(3))).CanonicalName.Should().Be("Product is not a category");
			}
		}
		finally
		{
			await restored.DisposeAsync(); await target.DisposeAsync(); File.Delete(path); if (exported != null)
			{
				File.Delete(exported);
			}
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task OldBackup_RejectsUnrelatedTargetsBeforeChangingBusinessRows(bool versioned)
	{
		string path = await LegacyFileAsync(9);
		var target = new PostgresFixture(); await target.InitializeAsync();
		try
		{
			await using (var db = target.CreateDbContext())
			{
				db.Categories.Add(new CategoryEntity { Id = Id(999), Name = "Unrelated destination suggestion", IsActive = true });
				db.Receipts.Add(new ReceiptEntity { Id = Id(4), Location = "Must survive", Date = new(2021, 1, 1) });
				await db.SaveChangesAsync();
			}
			if (versioned)
			{
				var service = new LegacyTaxonomyMigrationService(new Factory(target));
				var plan = await service.PreflightAsync(new("existing-target", Guid.NewGuid(), "admin", DateTimeOffset.UtcNow));
				await service.ApplyAsync(LegacyMigrationFreeze.Source(plan));
			}
			Func<Task> import = () => ImportAsync(target, path);
			await import.Should().ThrowAsync<InvalidOperationException>();
			await using var verify = target.CreateDbContext();
			(await verify.Receipts.SingleAsync(r => r.Id == Id(4))).Location.Should().Be("Must survive");
			(await verify.Categories.AnyAsync(c => c.Id == Id(1))).Should().BeFalse();
			(await verify.ReceiptItems.CountAsync()).Should().Be(0);
			(await verify.LegacyMigrations.CountAsync()).Should().Be(versioned ? 1 : 0);
		}
		finally { await target.DisposeAsync(); File.Delete(path); }
	}

	[Fact]
	public async Task OldBackup_InvalidLaterEntityRollsBackBusinessRowsAndMigrationTogether()
	{
		string path = await LegacyFileAsync(9);
		await EditAsync(path, $"INSERT INTO adjustments(id,receipt_id,type,amount,amount_currency) VALUES('{Id(80)}','{Id(4)}','InvalidLaterType','1','USD')");
		var target = new PostgresFixture(); await target.InitializeAsync();
		try
		{
			Func<Task> import = () => ImportAsync(target, path);
			await import.Should().ThrowAsync<ArgumentException>().WithMessage("*InvalidLaterType*");
			await using var db = target.CreateDbContext();
			(await db.Receipts.CountAsync()).Should().Be(0);
			(await db.ReceiptItems.CountAsync()).Should().Be(0);
			(await db.Categories.AnyAsync(c => c.Id == Id(1))).Should().BeFalse();
			(await db.Classifications.CountAsync()).Should().Be(0);
			(await db.LegacyMigrations.CountAsync()).Should().Be(0);
			(await db.TaxonomyHeads.SingleAsync()).RevisionId.Should().Be(TaxonomyConfiguration.GenesisId);
		}
		finally { await target.DisposeAsync(); File.Delete(path); }
	}

	[Fact]
	public async Task V1PartialBackup_WithoutProductColumnsOrSuggestionTables_CreatesObservedHistory()
	{
		string path = Path.Combine(Path.GetTempPath(), $"legacy-v1-{Guid.NewGuid():N}.db");
		await EditAsync(path, $"""
            CREATE TABLE backup_metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            INSERT INTO backup_metadata VALUES('export_version','1');
            CREATE TABLE receipts(id TEXT PRIMARY KEY,location TEXT,date TEXT,tax_amount TEXT,tax_amount_currency TEXT,original_image_path TEXT,processed_image_path TEXT);
            CREATE TABLE receipt_items(id TEXT PRIMARY KEY,receipt_id TEXT,receipt_item_code TEXT,description TEXT,quantity TEXT,unit_price TEXT,unit_price_currency TEXT,total_amount TEXT,total_amount_currency TEXT,category TEXT,subcategory TEXT);
            INSERT INTO receipts VALUES('{Id(4)}','Oldest shape','2001-01-01','0','USD',NULL,NULL);
            INSERT INTO receipt_items VALUES('{Id(10)}','{Id(4)}',NULL,' Raw old item ','1','3.45','USD','3.45','USD','Historical root','Historical child');
            """);
		var target = new PostgresFixture(); await target.InitializeAsync();
		try
		{
			await ImportAsync(target, path);
			await using var db = target.CreateDbContext();
			var migration = await db.LegacyMigrations.SingleAsync();
			migration.SourceDataset.Should().StartWith("portable-v1:sha256:");
			var nodes = await db.TaxonomyNodes.Where(n => n.RevisionId == migration.RevisionId).ToListAsync();
			nodes.Should().HaveCount(2);
			nodes.Single(n => n.Name == "Historical child").ParentId.Should().Be(nodes.Single(n => n.Name == "Historical root").CategoryId);
			(await db.Classifications.SingleAsync()).CategoryId.Should().Be(nodes.Single(n => n.Name == "Historical child").CategoryId);
			var item = await db.ReceiptItems.IgnoreAutoIncludes().SingleAsync();
			item.NormalizedDescriptionId.Should().BeNull(); item.Description.Should().Be(" Raw old item "); item.TotalAmount.Should().Be(3.45m);
			await ImportAsync(target, path);
			(await db.Classifications.CountAsync()).Should().Be(1);
		}
		finally { await target.DisposeAsync(); File.Delete(path); }
	}
}


