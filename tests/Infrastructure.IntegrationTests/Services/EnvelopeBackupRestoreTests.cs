using Application.Envelopes;
using Application.Models;
using Domain.Envelopes;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Envelopes;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class EnvelopeBackupRestoreTests(PostgresFixture server) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
	private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
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
	public async Task V15_RoundTripsEnvelopesOccasionsDefaultsLinksChoicesAndMarker()
	{
		// Arrange
		Guid food = Guid.NewGuid(), retired = Guid.NewGuid(), birthday = Guid.NewGuid(), category = Guid.NewGuid();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity chosen = ReceiptItemEntityGenerator.Generate(receipt.Id);
		ReceiptItemEntity historical = ReceiptItemEntityGenerator.Generate(receipt.Id);
		await using (ApplicationDbContext db = _source.CreateDbContext())
		{
			db.Envelopes.AddRange(
				new EnvelopeEntity { Id = food, Name = "Food", IsActive = true },
				new EnvelopeEntity { Id = retired, Name = "Retired", IsActive = false });
			db.Occasions.Add(new OccasionEntity { Id = birthday, Name = "Birthday", IsActive = true });
			db.TaxonomyIdentities.Add(new() { Id = category });
			await db.SaveChangesAsync();
			db.ItemTypeEnvelopeDefaults.Add(new ItemTypeEnvelopeDefaultEntity { Id = Guid.NewGuid(), CategoryId = category, EnvelopeId = food, CreatedAt = Now, UpdatedAt = Now });
			db.YnabEnvelopeMappings.Add(new YnabEnvelopeMappingEntity
			{
				Id = Guid.NewGuid(),
				EnvelopeId = food,
				YnabBudgetId = "budget",
				YnabCategoryId = "cat",
				YnabCategoryName = "Groceries",
				YnabCategoryGroupName = "Needs",
				CreatedAt = Now,
				UpdatedAt = Now,
			});
			db.EnvelopeBackfills.Add(new EnvelopeBackfillEntity { Id = 1, CompletedAt = Now, ItemCount = 1, EnvelopeCount = 2, DefaultCount = 1 });
			receipt.PaidFromEnvelopeId = food;
			receipt.OccasionId = birthday;
			chosen.EnvelopeId = food;
			chosen.EnvelopeSource = EnvelopeSource.Item;
			chosen.OccasionId = birthday;
			historical.EnvelopeId = retired;
			historical.EnvelopeSource = EnvelopeSource.Historical;
			db.Receipts.Add(receipt);
			db.ReceiptItems.AddRange(chosen, historical);
			await db.SaveChangesAsync();
		}
		string path = await new BackupService(new Factory(_source.CreateOptions()), NullLogger<BackupService>.Instance).ExportToSqliteAsync();

		try
		{
			// Act
			BackupImportResult result = await ImportAsync(path);

			// Assert — a fresh destination creates every envelope row.
			EnvelopeCounts(result).Should().Be((2, 0, 1, 0, 1, 0, 1, 0));
			await using ApplicationDbContext verify = _target.CreateDbContext();
			(await verify.Envelopes.IgnoreQueryFilters().OrderBy(row => row.Name).Select(row => new { row.Id, row.Name, row.IsActive }).ToListAsync())
				.Should().Equal(new { Id = food, Name = "Food", IsActive = true }, new { Id = retired, Name = "Retired", IsActive = false });
			(await verify.Occasions.SingleAsync()).Id.Should().Be(birthday);
			(await verify.ItemTypeEnvelopeDefaults.SingleAsync()).Should().Match<ItemTypeEnvelopeDefaultEntity>(row => row.CategoryId == category && row.EnvelopeId == food);
			(await verify.YnabEnvelopeMappings.SingleAsync()).YnabCategoryId.Should().Be("cat");
			(await verify.EnvelopeBackfills.SingleAsync()).CompletedAt.Should().Be(Now);
			ReceiptEntity actualReceipt = await verify.Receipts.SingleAsync(row => row.Id == receipt.Id);
			actualReceipt.PaidFromEnvelopeId.Should().Be(food);
			actualReceipt.OccasionId.Should().Be(birthday);
			ReceiptItemEntity actualChosen = await verify.ReceiptItems.IgnoreAutoIncludes().SingleAsync(row => row.Id == chosen.Id);
			(actualChosen.EnvelopeId, actualChosen.EnvelopeSource, actualChosen.OccasionId).Should().Be((food, EnvelopeSource.Item, birthday));
			ReceiptItemEntity actualHistorical = await verify.ReceiptItems.IgnoreAutoIncludes().SingleAsync(row => row.Id == historical.Id);
			(actualHistorical.EnvelopeId, actualHistorical.EnvelopeSource).Should().Be((retired, EnvelopeSource.Historical));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public async Task V14_RestoreDerivesEnvelopesFromLegacyYnabMappings()
	{
		// Arrange
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		await using (ApplicationDbContext db = _source.CreateDbContext())
		{
			db.Receipts.Add(receipt);
			db.ReceiptItems.Add(item);
			db.YnabCategoryMappings.Add(new YnabCategoryMappingEntity
			{
				Id = Guid.NewGuid(),
				ReceiptsCategory = "Vacation",
				ReceiptsCategoryId = null,
				YnabCategoryId = "ynab-vacation",
				YnabCategoryName = "Vacation",
				YnabCategoryGroupName = "Wants",
				YnabBudgetId = "budget",
				CreatedAt = Now,
				UpdatedAt = Now,
			});
			await db.SaveChangesAsync();
		}
		string path = await new BackupService(new Factory(_source.CreateOptions()), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		await DowngradeToV14Async(path);

		try
		{
			// Act
			BackupImportResult result = await ImportAsync(path);

			// Assert — a derived restore reports everything it derived as created.
			EnvelopeCounts(result).Should().Be((1, 0, 0, 0, 0, 0, 1, 0));
			await using ApplicationDbContext verify = _target.CreateDbContext();
			(await verify.ItemTypeEnvelopeDefaults.CountAsync()).Should().Be(0);
			EnvelopeEntity vacation = await verify.Envelopes.SingleAsync();
			vacation.Name.Should().Be("Vacation");
			(await verify.YnabEnvelopeMappings.SingleAsync()).Should().Match<YnabEnvelopeMappingEntity>(row =>
				row.EnvelopeId == vacation.Id && row.YnabBudgetId == "budget" && row.YnabCategoryId == "ynab-vacation");
			ReceiptItemEntity actual = await verify.ReceiptItems.IgnoreAutoIncludes().SingleAsync(row => row.Id == item.Id);
			actual.EnvelopeSource.Should().Be(EnvelopeSource.Historical);
			actual.EnvelopeId.Should().BeNull("an unclassified purchase resolved to no YNAB category before envelopes");
			(await verify.EnvelopeBackfills.AnyAsync()).Should().BeTrue();
			EnvelopeParityReport parity = await EnvelopeParityReader.CheckAsync(verify, CancellationToken.None);
			parity.Mismatches.Should().Be(0);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public async Task V15_RestoreOverExistingRows_CountsThemAsUpdated()
	{
		// Arrange
		Guid food = Guid.NewGuid(), birthday = Guid.NewGuid(), category = Guid.NewGuid();
		await using (ApplicationDbContext db = _source.CreateDbContext())
		{
			db.Envelopes.Add(new EnvelopeEntity { Id = food, Name = "Food", IsActive = true });
			db.Occasions.Add(new OccasionEntity { Id = birthday, Name = "Birthday", IsActive = true });
			db.TaxonomyIdentities.Add(new() { Id = category });
			await db.SaveChangesAsync();
			db.ItemTypeEnvelopeDefaults.Add(new ItemTypeEnvelopeDefaultEntity { Id = Guid.NewGuid(), CategoryId = category, EnvelopeId = food, CreatedAt = Now, UpdatedAt = Now });
			db.YnabEnvelopeMappings.AddRange(
				Mapping(food, "budget-a", "cat-a"),
				Mapping(food, "budget-b", "cat-b"));
			await db.SaveChangesAsync();
		}
		string path = await new BackupService(new Factory(_source.CreateOptions()), NullLogger<BackupService>.Instance).ExportToSqliteAsync();

		try
		{
			BackupImportResult first = await ImportAsync(path);

			// Act
			BackupImportResult second = await ImportAsync(path);

			// Assert
			EnvelopeCounts(first).Should().Be((1, 0, 1, 0, 1, 0, 2, 0));
			EnvelopeCounts(second).Should().Be((0, 1, 0, 1, 0, 1, 0, 2));
			second.TotalUpdated.Should().BeGreaterThanOrEqualTo(5, "the envelope tables count toward the totals");
			await using ApplicationDbContext verify = _target.CreateDbContext();
			(await verify.YnabEnvelopeMappings.CountAsync()).Should().Be(2);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public async Task V14_RestoreIntoDestinationWithEnvelopes_IsRejected()
	{
		// Arrange
		string path = await new BackupService(new Factory(_source.CreateOptions()), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		await DowngradeToV14Async(path);
		await using (ApplicationDbContext db = _target.CreateDbContext())
		{
			db.Envelopes.Add(new EnvelopeEntity { Id = Guid.NewGuid(), Name = "Existing", IsActive = true });
			await db.SaveChangesAsync();
		}

		try
		{
			// Act
			Func<Task> act = () => ImportAsync(path);

			// Assert
			await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*fresh database*");
		}
		finally
		{
			File.Delete(path);
		}
	}

	private async Task<BackupImportResult> ImportAsync(string path)
	{
		BackupImportService importer = new(new Factory(_target.CreateOptions()), NullLogger<BackupImportService>.Instance);
		await using FileStream input = File.OpenRead(path);
		return await importer.ImportFromSqliteAsync(input, CancellationToken.None);
	}

	private static (int, int, int, int, int, int, int, int) EnvelopeCounts(BackupImportResult result) =>
		(result.EnvelopesCreated, result.EnvelopesUpdated, result.OccasionsCreated, result.OccasionsUpdated,
			result.ItemTypeEnvelopeDefaultsCreated, result.ItemTypeEnvelopeDefaultsUpdated,
			result.YnabEnvelopeMappingsCreated, result.YnabEnvelopeMappingsUpdated);

	private static YnabEnvelopeMappingEntity Mapping(Guid envelope, string budget, string category) => new()
	{
		Id = Guid.NewGuid(),
		EnvelopeId = envelope,
		YnabBudgetId = budget,
		YnabCategoryId = category,
		YnabCategoryName = category,
		YnabCategoryGroupName = "Needs",
		CreatedAt = Now,
		UpdatedAt = Now,
	};

	private static async Task DowngradeToV14Async(string path)
	{
		await using SqliteConnection sqlite = new($"Data Source={path};Pooling=False");
		await sqlite.OpenAsync();
		await using SqliteCommand command = sqlite.CreateCommand();
		command.CommandText = "PRAGMA foreign_keys = OFF; UPDATE backup_metadata SET value='14' WHERE key='export_version'; "
			+ string.Join(' ', PortableEnvelopeArchive.Tables.Select(table => $"DROP TABLE {table};"));
		await command.ExecuteNonQueryAsync();
	}

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(new ApplicationDbContext(options));
	}
}
