using Domain.Envelopes;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Envelopes;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.Tests.Services.Envelopes;

/// <summary>
/// The format 15 envelope tables on their own: InMemory source → SQLite archive → InMemory destination.
/// Full BackupService/BackupImportService restores run on PostgreSQL in Infrastructure.IntegrationTests.
/// </summary>
public sealed class PortableEnvelopeArchiveTests : IAsyncDisposable
{
	private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
	// The archive's parent tables (receipts, receipt_items) belong to BackupService; these tests own only the envelope tables.
	private readonly SqliteConnection _sqlite = new("Data Source=:memory:;Foreign Keys=False");

	[Fact]
	public async Task V15_RoundTripsEveryEnvelopeTableAndChoice()
	{
		// Arrange
		await OpenArchiveAsync();
		Guid receiptId = Guid.NewGuid(), liveItem = Guid.NewGuid(), trashedItem = Guid.NewGuid(), plainItem = Guid.NewGuid();
		Guid food = Guid.NewGuid(), deleted = Guid.NewGuid(), birthday = Guid.NewGuid(), category = Guid.NewGuid();
		await using ApplicationDbContext source = NewContext();
		await using ApplicationDbContext destination = NewContext();
		foreach (ApplicationDbContext db in new[] { source, destination })
		{
			db.Receipts.Add(Receipt(receiptId));
			db.ReceiptItems.AddRange(Item(liveItem, receiptId), Item(trashedItem, receiptId, Now), Item(plainItem, receiptId));
			await db.SaveChangesAsync();
		}
		source.Envelopes.AddRange(
			new EnvelopeEntity { Id = food, Name = "Food", IsActive = true },
			new EnvelopeEntity { Id = deleted, Name = "Gone", IsActive = false, DeletedAt = Now, DeletedByUserId = "owner", DeletedByApiKeyId = Guid.NewGuid() });
		source.Occasions.Add(new OccasionEntity { Id = birthday, Name = "Birthday", IsActive = true });
		source.ItemTypeEnvelopeDefaults.Add(new ItemTypeEnvelopeDefaultEntity { Id = Guid.NewGuid(), CategoryId = category, EnvelopeId = food, CreatedAt = Now.AddDays(-1), UpdatedAt = Now });
		source.YnabEnvelopeMappings.Add(new YnabEnvelopeMappingEntity
		{
			Id = Guid.NewGuid(),
			EnvelopeId = food,
			YnabBudgetId = "budget",
			YnabCategoryId = "cat",
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "Needs",
			CreatedAt = Now.AddDays(-2),
			UpdatedAt = Now,
		});
		source.EnvelopeBackfills.Add(new EnvelopeBackfillEntity { Id = 1, CompletedAt = Now, ItemCount = 3, EnvelopeCount = 2, DefaultCount = 1 });
		await source.SaveChangesAsync();
		await SetItemAsync(source, liveItem, food, EnvelopeSource.Item, birthday);
		await SetItemAsync(source, trashedItem, food, EnvelopeSource.Historical, null);
		ReceiptEntity sourceReceipt = await source.Receipts.SingleAsync();
		sourceReceipt.PaidFromEnvelopeId = food;
		sourceReceipt.OccasionId = birthday;
		await source.SaveChangesAsync();

		// Act
		await PortableEnvelopeArchive.ExportAsync(source, _sqlite, CancellationToken.None);
		await PortableEnvelopeArchive.ImportAsync(destination, _sqlite, PortableEnvelopeArchive.Version, CancellationToken.None);

		// Assert
		destination.ChangeTracker.Clear();
		(await destination.Envelopes.IgnoreQueryFilters().OrderBy(row => row.Name).ToListAsync()).Should().BeEquivalentTo(
			await source.Envelopes.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Name).ToListAsync());
		(await destination.Occasions.ToListAsync()).Should().BeEquivalentTo(await source.Occasions.AsNoTracking().ToListAsync());
		(await destination.ItemTypeEnvelopeDefaults.ToListAsync()).Should().BeEquivalentTo(await source.ItemTypeEnvelopeDefaults.AsNoTracking().ToListAsync());
		(await destination.YnabEnvelopeMappings.ToListAsync()).Should().BeEquivalentTo(await source.YnabEnvelopeMappings.AsNoTracking().ToListAsync());
		(await destination.EnvelopeBackfills.SingleAsync()).Should().BeEquivalentTo(await source.EnvelopeBackfills.AsNoTracking().SingleAsync());
		var expectedItems = await Choices(source);
		(await Choices(destination)).Should().BeEquivalentTo(expectedItems);
		ReceiptEntity actualReceipt = await destination.Receipts.SingleAsync();
		actualReceipt.PaidFromEnvelopeId.Should().Be(food);
		actualReceipt.OccasionId.Should().Be(birthday);
	}

	[Fact]
	public async Task V15_ImportIsIdempotent()
	{
		// Arrange
		await OpenArchiveAsync();
		await using ApplicationDbContext source = NewContext();
		source.Envelopes.Add(new EnvelopeEntity { Id = Guid.NewGuid(), Name = "Food", IsActive = true });
		await source.SaveChangesAsync();
		await PortableEnvelopeArchive.ExportAsync(source, _sqlite, CancellationToken.None);
		await using ApplicationDbContext destination = NewContext();

		// Act
		await PortableEnvelopeArchive.ImportAsync(destination, _sqlite, PortableEnvelopeArchive.Version, CancellationToken.None);
		await PortableEnvelopeArchive.ImportAsync(destination, _sqlite, PortableEnvelopeArchive.Version, CancellationToken.None);

		// Assert
		(await destination.Envelopes.CountAsync()).Should().Be(1);
	}

	[Fact]
	public async Task V15_ChoiceForItemAbsentFromBackup_Throws()
	{
		// Arrange
		await OpenArchiveAsync();
		await ExecuteAsync($"INSERT INTO receipt_item_envelopes VALUES ('{Guid.NewGuid()}', NULL, 'Default', NULL)");
		await using ApplicationDbContext destination = NewContext();

		// Act
		Func<Task> act = () => PortableEnvelopeArchive.ImportAsync(destination, _sqlite, PortableEnvelopeArchive.Version, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*receipt item absent*");
	}

	[Theory]
	[InlineData("Bogus")]
	[InlineData("99")]
	[InlineData("default")]
	public async Task V15_InvalidEnvelopeSource_Throws(string source)
	{
		// Arrange
		await OpenArchiveAsync();
		Guid itemId = Guid.NewGuid();
		await using ApplicationDbContext destination = NewContext();
		destination.ReceiptItems.Add(Item(itemId, Guid.NewGuid()));
		await destination.SaveChangesAsync();
		await ExecuteAsync($"INSERT INTO receipt_item_envelopes VALUES ('{itemId}', NULL, '{source}', NULL)");

		// Act
		Func<Task> act = () => PortableEnvelopeArchive.ImportAsync(destination, _sqlite, PortableEnvelopeArchive.Version, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*envelope source*");
	}

	[Fact]
	public async Task OlderFormat_DerivesEnvelopesForRestoredItemsAsHistorical()
	{
		// Arrange
		await OpenArchiveAsync();
		await ExecuteAsync("CREATE TABLE receipt_items (id TEXT NOT NULL PRIMARY KEY)");
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync(withEnvelopes: false);
		ReceiptEntity receipt = await world.AddReceiptAsync();
		Guid restored = Guid.NewGuid(), createdLocally = Guid.NewGuid(), assignment = Guid.NewGuid();
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			db.ReceiptItems.AddRange(Item(restored, receipt.Id), Item(createdLocally, receipt.Id));
			db.Classifications.Add(new() { Id = assignment, ReceiptItemId = restored, CategoryId = world.Fuel, TaxonomyRevisionId = world.Revision, Sequence = 1 });
			db.CurrentClassifications.Add(new() { ReceiptItemId = restored, AssignmentId = assignment });
			db.YnabCategoryMappings.Add(new YnabCategoryMappingEntity
			{
				Id = Guid.NewGuid(),
				ReceiptsCategory = "Fuel",
				ReceiptsCategoryId = world.Fuel,
				YnabCategoryId = "ynab-car",
				YnabCategoryName = "Car",
				YnabCategoryGroupName = "Needs",
				YnabBudgetId = "budget",
				CreatedAt = Now,
				UpdatedAt = Now,
			});
			await db.SaveChangesAsync();
		}
		await ExecuteAsync($"INSERT INTO receipt_items VALUES ('{restored}')");

		// Act
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			await PortableEnvelopeArchive.ImportAsync(db, _sqlite, 14, CancellationToken.None);
		}

		// Assert
		await using ApplicationDbContext verify = world.Factory.CreateDbContext();
		EnvelopeEntity car = await verify.Envelopes.SingleAsync();
		car.Name.Should().Be("Car");
		ReceiptItemEntity actual = await world.ReadItemAsync(restored);
		actual.EnvelopeId.Should().Be(car.Id);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Historical);
		ReceiptItemEntity local = await world.ReadItemAsync(createdLocally);
		local.EnvelopeId.Should().BeNull();
		local.EnvelopeSource.Should().Be(EnvelopeSource.Default);
		(await verify.ItemTypeEnvelopeDefaults.SingleAsync()).CategoryId.Should().Be(world.Fuel);
		(await verify.EnvelopeBackfills.AnyAsync()).Should().BeTrue();
	}

	[Theory]
	[InlineData(14, true, true)]
	[InlineData(14, false, false)]
	[InlineData(15, true, false)]
	public async Task RequireDerivableDestination_OlderFormatNeedsADestinationWithoutEnvelopes(int version, bool hasEnvelopes, bool throws)
	{
		// Arrange
		await using ApplicationDbContext db = NewContext();
		if (hasEnvelopes)
		{
			db.Envelopes.Add(new EnvelopeEntity { Id = Guid.NewGuid(), Name = "Food", IsActive = true, DeletedAt = Now });
			await db.SaveChangesAsync();
		}

		// Act
		Func<Task> act = () => PortableEnvelopeArchive.RequireDerivableDestinationAsync(db, version, CancellationToken.None);

		// Assert
		if (throws)
		{
			await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*fresh database*");
		}
		else
		{
			await act.Should().NotThrowAsync();
		}
	}

	public async ValueTask DisposeAsync() => await _sqlite.DisposeAsync();

	private async Task OpenArchiveAsync()
	{
		await _sqlite.OpenAsync();
		foreach (string ddl in PortableEnvelopeArchive.Ddl)
		{
			await ExecuteAsync(ddl);
		}
	}

	private async Task ExecuteAsync(string sql)
	{
		await using SqliteCommand command = _sqlite.CreateCommand();
		command.CommandText = sql;
		await command.ExecuteNonQueryAsync();
	}

	private static ApplicationDbContext NewContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
		.UseInMemoryDatabase($"envelope-archive-{Guid.NewGuid():N}").Options);

	private static ReceiptEntity Receipt(Guid id)
	{
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.Id = id;
		return receipt;
	}

	private static ReceiptItemEntity Item(Guid id, Guid receiptId, DateTimeOffset? deletedAt = null)
	{
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receiptId);
		item.Id = id;
		item.DeletedAt = deletedAt;
		return item;
	}

	private static async Task SetItemAsync(ApplicationDbContext db, Guid itemId, Guid? envelopeId, EnvelopeSource source, Guid? occasionId)
	{
		ReceiptItemEntity item = await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().SingleAsync(row => row.Id == itemId);
		item.EnvelopeId = envelopeId;
		item.EnvelopeSource = source;
		item.OccasionId = occasionId;
		await db.SaveChangesAsync();
	}

	private static async Task<List<(Guid, Guid?, EnvelopeSource, Guid?)>> Choices(ApplicationDbContext db)
		=> (await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking().OrderBy(row => row.Id)
			.Select(row => new { row.Id, row.EnvelopeId, row.EnvelopeSource, row.OccasionId }).ToListAsync())
			.Select(row => (row.Id, row.EnvelopeId, row.EnvelopeSource, row.OccasionId)).ToList();
}
