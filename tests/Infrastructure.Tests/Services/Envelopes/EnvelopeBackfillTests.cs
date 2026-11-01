using Application.Envelopes;
using Domain.Envelopes;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Envelopes;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using SampleData.Entities;

namespace Infrastructure.Tests.Services.Envelopes;

public class EnvelopeBackfillTests
{
	private const string Budget = "budget-a";
	private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task RunAsync_AppliesThePlanAndWritesTheMarker()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync(withEnvelopes: false);
		Guid groceriesItem = await AddClassifiedItemAsync(world, world.Produce);
		Guid fuelItem = await AddClassifiedItemAsync(world, world.Fuel);
		Guid giftsItem = await AddClassifiedItemAsync(world, world.Gifts);
		Guid unclassified = await AddItemAsync(world, EnvelopeSource.Historical);
		await AddLegacyMappingAsync(world, world.Groceries, "ynab-food", "Food");
		await AddLegacyMappingAsync(world, world.Fuel, "ynab-car", "Car");
		await AddLegacyMappingAsync(world, null, "ynab-vacation", "Vacation");

		// Act
		EnvelopeBackfillResult actual;
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			actual = await EnvelopeBackfill.RunAsync(db, new FakeTimeProvider(Now), CancellationToken.None);
		}

		// Assert
		actual.Should().Be(new EnvelopeBackfillResult(4, 3, 4, 2));
		await using ApplicationDbContext verify = world.Factory.CreateDbContext();
		Dictionary<string, EnvelopeEntity> envelopes = await verify.Envelopes.ToDictionaryAsync(row => row.Name);
		envelopes.Keys.Should().BeEquivalentTo(["Food", "Car", "Vacation", "Gifts"]);
		envelopes["Gifts"].IsActive.Should().BeFalse();
		(await world.ReadItemAsync(groceriesItem)).EnvelopeId.Should().Be(envelopes["Food"].Id);
		(await world.ReadItemAsync(fuelItem)).EnvelopeId.Should().Be(envelopes["Car"].Id);
		(await world.ReadItemAsync(giftsItem)).EnvelopeId.Should().Be(envelopes["Gifts"].Id);
		ReceiptItemEntity actualUnclassified = await world.ReadItemAsync(unclassified);
		actualUnclassified.EnvelopeId.Should().BeNull();
		actualUnclassified.EnvelopeSource.Should().Be(EnvelopeSource.Historical);
		(await verify.YnabEnvelopeMappings.Where(row => row.EnvelopeId == envelopes["Vacation"].Id).SingleAsync())
			.YnabCategoryId.Should().Be("ynab-vacation");
		(await verify.ItemTypeEnvelopeDefaults.ToDictionaryAsync(row => row.CategoryId, row => row.EnvelopeId))
			.Should().BeEquivalentTo(new Dictionary<Guid, Guid> { [world.Groceries] = envelopes["Food"].Id, [world.Fuel] = envelopes["Car"].Id });
		EnvelopeBackfillEntity marker = await verify.EnvelopeBackfills.SingleAsync();
		marker.Should().BeEquivalentTo(new EnvelopeBackfillEntity { Id = 1, CompletedAt = Now, ItemCount = 4, EnvelopeCount = 4, DefaultCount = 2 });
		EnvelopeParityReport parity = await EnvelopeParityReader.CheckAsync(verify, CancellationToken.None);
		parity.Mismatches.Should().Be(0);
		parity.ItemsMappedByOldPath.Should().Be(2);
	}

	[Fact]
	public async Task RunAsync_LeavesNonHistoricalItemsAlone()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync(withEnvelopes: false);
		Guid live = await AddClassifiedItemAsync(world, world.Fuel, EnvelopeSource.Default);
		await AddLegacyMappingAsync(world, world.Fuel, "ynab-car", "Car");

		// Act
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			await EnvelopeBackfill.RunAsync(db, new FakeTimeProvider(Now), CancellationToken.None);
		}

		// Assert
		(await world.ReadItemAsync(live)).EnvelopeId.Should().BeNull();
	}

	[Fact]
	public async Task RunAsync_DatabaseAlreadyHasEnvelopes_ThrowsWithoutWriting()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync(withEnvelopes: true);
		await AddClassifiedItemAsync(world, world.Fuel);

		// Act
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		Func<Task> act = () => EnvelopeBackfill.RunAsync(db, new FakeTimeProvider(Now), CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>();
		(await db.EnvelopeBackfills.AnyAsync()).Should().BeFalse();
	}

	[Fact]
	public async Task RunIfPendingAsync_FreshDatabase_IsNoOpAndWritesNothing()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync(withEnvelopes: false);
		await AddItemAsync(world, EnvelopeSource.Default);

		// Act
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		EnvelopeBackfillResult? actual = await EnvelopeBackfill.RunIfPendingAsync(db, new FakeTimeProvider(Now), CancellationToken.None);

		// Assert
		actual.Should().BeNull();
		(await db.EnvelopeBackfills.AnyAsync()).Should().BeFalse();
		(await db.Envelopes.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task RunIfPendingAsync_HistoricalItemsOrLegacyMappings_Runs(bool historicalItems)
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync(withEnvelopes: false);
		if (historicalItems)
		{
			await AddClassifiedItemAsync(world, world.Gifts);
		}
		else
		{
			await AddLegacyMappingAsync(world, world.Fuel, "ynab-car", "Car");
		}

		// Act
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		EnvelopeBackfillResult? actual = await EnvelopeBackfill.RunIfPendingAsync(db, new FakeTimeProvider(Now), CancellationToken.None);

		// Assert
		actual.Should().NotBeNull();
		actual!.Envelopes.Should().Be(1);
		(await db.EnvelopeBackfills.AnyAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task RunIfPendingAsync_AfterMarker_DoesNotRunAgain()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync(withEnvelopes: false);
		await AddClassifiedItemAsync(world, world.Gifts);
		await using (ApplicationDbContext first = world.Factory.CreateDbContext())
		{
			await EnvelopeBackfill.RunIfPendingAsync(first, new FakeTimeProvider(Now), CancellationToken.None);
		}

		// Act
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		EnvelopeBackfillResult? actual = await EnvelopeBackfill.RunIfPendingAsync(db, new FakeTimeProvider(Now.AddDays(1)), CancellationToken.None);

		// Assert
		actual.Should().BeNull();
		(await db.EnvelopeBackfills.SingleAsync()).CompletedAt.Should().Be(Now);
	}

	[Fact]
	public async Task ParityReader_DetectsAnItemMovedToTheWrongEnvelope()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync(withEnvelopes: false);
		Guid groceriesItem = await AddClassifiedItemAsync(world, world.Groceries);
		await AddClassifiedItemAsync(world, world.Fuel);
		await AddLegacyMappingAsync(world, world.Groceries, "ynab-food", "Food");
		await AddLegacyMappingAsync(world, world.Fuel, "ynab-car", "Car");
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			await EnvelopeBackfill.RunAsync(db, new FakeTimeProvider(Now), CancellationToken.None);
		}
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			Guid car = (await db.Envelopes.SingleAsync(row => row.Name == "Car")).Id;
			(await db.ReceiptItems.IgnoreAutoIncludes().SingleAsync(row => row.Id == groceriesItem)).EnvelopeId = car;
			await db.SaveChangesAsync();
		}

		// Act
		await using ApplicationDbContext verify = world.Factory.CreateDbContext();
		EnvelopeParityReport actual = await EnvelopeParityReader.CheckAsync(verify, CancellationToken.None);

		// Assert
		actual.Mismatches.Should().Be(1);
	}

	[Fact]
	public async Task ParityReader_RoutesAsync_RoutesEveryItemThroughItsEnvelope_IncludingDeletedItems()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity food = ReceiptItemEntityGenerator.Generate(receipt.Id);
		food.EnvelopeId = world.Food;
		ReceiptItemEntity car = ReceiptItemEntityGenerator.Generate(receipt.Id);
		car.EnvelopeId = world.Car;
		ReceiptItemEntity deleted = ReceiptItemEntityGenerator.Generate(receipt.Id);
		deleted.EnvelopeId = world.Food;
		deleted.DeletedAt = Now;
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			db.ReceiptItems.AddRange(food, car, deleted);
			db.YnabEnvelopeMappings.Add(new YnabEnvelopeMappingEntity
			{
				Id = Guid.NewGuid(),
				EnvelopeId = world.Food,
				YnabBudgetId = Budget,
				YnabCategoryId = "ynab-food",
				YnabCategoryName = "Food",
				YnabCategoryGroupName = "Needs",
				CreatedAt = Now,
				UpdatedAt = Now,
			});
			await db.SaveChangesAsync();
		}

		// Act
		await using ApplicationDbContext verify = world.Factory.CreateDbContext();
		IReadOnlyList<EnvelopeYnabRoute> routes = await EnvelopeParityReader.RoutesAsync(verify, CancellationToken.None);

		// Assert
		routes.Should().BeEquivalentTo(new[]
		{
			new EnvelopeYnabRoute(food.Id, Budget, "ynab-food"),
			new EnvelopeYnabRoute(car.Id, Budget, null),
			new EnvelopeYnabRoute(deleted.Id, Budget, "ynab-food"),
		});
	}

	private static async Task<Guid> AddItemAsync(EnvelopeWorld world, EnvelopeSource source)
	{
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.EnvelopeSource = source;
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		db.ReceiptItems.Add(item);
		await db.SaveChangesAsync();
		return item.Id;
	}

	private static async Task<Guid> AddClassifiedItemAsync(EnvelopeWorld world, Guid categoryId,
		EnvelopeSource source = EnvelopeSource.Historical)
	{
		Guid itemId = await AddItemAsync(world, source);
		Guid assignment = Guid.NewGuid();
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		db.Classifications.Add(new() { Id = assignment, ReceiptItemId = itemId, CategoryId = categoryId, TaxonomyRevisionId = world.Revision, Sequence = 1 });
		db.CurrentClassifications.Add(new() { ReceiptItemId = itemId, AssignmentId = assignment });
		await db.SaveChangesAsync();
		return itemId;
	}

	private static async Task AddLegacyMappingAsync(EnvelopeWorld world, Guid? categoryId, string ynabCategoryId, string name)
	{
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		db.YnabCategoryMappings.Add(new YnabCategoryMappingEntity
		{
			Id = Guid.NewGuid(),
			ReceiptsCategory = name,
			ReceiptsCategoryId = categoryId,
			YnabCategoryId = ynabCategoryId,
			YnabCategoryName = name,
			YnabCategoryGroupName = "Group",
			YnabBudgetId = Budget,
			CreatedAt = Now,
			UpdatedAt = Now,
		});
		await db.SaveChangesAsync();
	}
}
