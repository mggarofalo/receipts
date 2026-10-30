using Application.Envelopes;
using Application.Exceptions;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Models.Ynab;
using Domain.Envelopes;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Envelopes;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Moq;
using SampleData.Entities;

namespace Infrastructure.Tests.Services.Envelopes;

public class EnvelopeServicesTests
{
	private const string BudgetA = "budget-a";
	private const string BudgetB = "budget-b";
	private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
	private readonly Mock<ICommittedChangePublisher> _publisher = new();

	// --- EnvelopeService ---

	[Fact]
	public async Task Envelope_CreateTrimsNameAndAssignsId()
	{
		// Arrange
		EnvelopeService service = new((await EnvelopeWorld.CreateAsync(withEnvelopes: false)).Factory);

		// Act
		Envelope actual = await service.CreateAsync(new(Guid.Empty, "  Groceries  "), CancellationToken.None);

		// Assert
		actual.Id.Should().NotBe(Guid.Empty);
		actual.Name.Should().Be("Groceries");
		(await service.GetByIdAsync(actual.Id, CancellationToken.None)).Should().BeEquivalentTo(actual);
	}

	[Theory]
	[InlineData(null, new[] { "Car", "Food", "Retired" })]
	[InlineData(true, new[] { "Car", "Food" })]
	[InlineData(false, new[] { "Retired" })]
	public async Task Envelope_GetAll_FiltersByActiveAndOrdersByName(bool? isActive, string[] expected)
	{
		// Arrange
		EnvelopeService service = new((await EnvelopeWorld.CreateAsync()).Factory);

		// Act
		List<Envelope> actual = await service.GetAllAsync(isActive, CancellationToken.None);

		// Assert
		actual.Select(envelope => envelope.Name).Should().Equal(expected);
	}

	[Fact]
	public async Task Envelope_CreateDuplicateName_Throws()
	{
		// Arrange
		EnvelopeService service = new((await EnvelopeWorld.CreateAsync()).Factory);

		// Act
		Func<Task> act = () => service.CreateAsync(new(Guid.Empty, "Food "), CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<DuplicateEntityException>();
	}

	[Fact]
	public async Task Envelope_UpdateRenamesAndArchives()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		EnvelopeService service = new(world.Factory);

		// Act
		bool actual = await service.UpdateAsync(new(world.Food, "Groceries", false), CancellationToken.None);

		// Assert
		actual.Should().BeTrue();
		(await service.GetByIdAsync(world.Food, CancellationToken.None)).Should().BeEquivalentTo(new Envelope(world.Food, "Groceries", false));
	}

	[Fact]
	public async Task Envelope_UpdateToAnotherEnvelopesName_Throws()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		EnvelopeService service = new(world.Factory);

		// Act
		Func<Task> act = () => service.UpdateAsync(new(world.Food, "Car"), CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<DuplicateEntityException>();
		(await service.GetByIdAsync(world.Food, CancellationToken.None))!.Name.Should().Be("Food");
	}

	[Fact]
	public async Task Envelope_UpdateOrDeleteMissing_ReturnsFalse()
	{
		// Arrange
		EnvelopeService service = new((await EnvelopeWorld.CreateAsync()).Factory);

		// Act
		bool updated = await service.UpdateAsync(new(Guid.NewGuid(), "Nope"), CancellationToken.None);
		bool deleted = await service.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

		// Assert
		updated.Should().BeFalse();
		deleted.Should().BeFalse();
	}

	[Fact]
	public async Task Envelope_DeleteUnreferenced_SoftDeletesAndRemovesYnabLinks()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		EnvelopeService service = new(world.Factory);
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			db.YnabEnvelopeMappings.Add(Link(world.Retired, BudgetA, "old"));
			await db.SaveChangesAsync();
		}

		// Act
		bool actual = await service.DeleteAsync(world.Retired, CancellationToken.None);

		// Assert
		actual.Should().BeTrue();
		(await service.GetByIdAsync(world.Retired, CancellationToken.None)).Should().BeNull();
		await using ApplicationDbContext verify = world.Factory.CreateDbContext();
		(await verify.Envelopes.IgnoreQueryFilters().SingleAsync(row => row.Id == world.Retired)).DeletedAt.Should().NotBeNull();
		(await verify.YnabEnvelopeMappings.AnyAsync(row => row.EnvelopeId == world.Retired)).Should().BeFalse();
	}

	[Fact]
	public async Task Envelope_DeleteReferenced_Throws409WithCounts()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync(paidFrom: world.Food);
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			ReceiptItemEntity live = ReceiptItemEntityGenerator.Generate(receipt.Id);
			live.EnvelopeId = world.Food;
			ReceiptItemEntity trashed = ReceiptItemEntityGenerator.Generate(receipt.Id);
			trashed.EnvelopeId = world.Food;
			trashed.DeletedAt = Now;
			db.ReceiptItems.AddRange(live, trashed);
			await db.SaveChangesAsync();
		}
		EnvelopeService service = new(world.Factory);

		// Act
		Func<Task> act = () => service.DeleteAsync(world.Food, CancellationToken.None);

		// Assert
		EnvelopeException actual = (await act.Should().ThrowAsync<EnvelopeException>()).Which;
		actual.Status.Should().Be(409);
		actual.Extensions.Should().BeEquivalentTo(new Dictionary<string, object?> { ["itemCount"] = 2, ["receiptCount"] = 1, ["defaultCount"] = 1 });
		(await service.GetByIdAsync(world.Food, CancellationToken.None)).Should().NotBeNull();
	}

	// --- OccasionService ---

	[Fact]
	public async Task Occasion_CrudRoundTrip()
	{
		// Arrange
		OccasionService service = new((await EnvelopeWorld.CreateAsync()).Factory);

		// Act
		Occasion created = await service.CreateAsync(new(Guid.Empty, " Birthday "), CancellationToken.None);
		bool updated = await service.UpdateAsync(new(created.Id, "Anniversary", false), CancellationToken.None);
		List<Occasion> archived = await service.GetAllAsync(false, CancellationToken.None);
		bool deleted = await service.DeleteAsync(created.Id, CancellationToken.None);

		// Assert
		created.Name.Should().Be("Birthday");
		updated.Should().BeTrue();
		archived.Should().ContainSingle().Which.Should().BeEquivalentTo(new Occasion(created.Id, "Anniversary", false));
		deleted.Should().BeTrue();
		(await service.GetByIdAsync(created.Id, CancellationToken.None)).Should().BeNull();
	}

	[Fact]
	public async Task Occasion_DuplicateName_Throws()
	{
		// Arrange
		OccasionService service = new((await EnvelopeWorld.CreateAsync()).Factory);
		await service.CreateAsync(new(Guid.Empty, "Birthday"), CancellationToken.None);

		// Act
		Func<Task> act = () => service.CreateAsync(new(Guid.Empty, "Birthday"), CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<DuplicateEntityException>();
	}

	[Fact]
	public async Task Occasion_DeleteReferenced_Throws409WithCounts()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		OccasionService service = new(world.Factory);
		Occasion occasion = await service.CreateAsync(new(Guid.Empty, "Birthday"), CancellationToken.None);
		await world.AddReceiptAsync(occasion: occasion.Id);

		// Act
		Func<Task> act = () => service.DeleteAsync(occasion.Id, CancellationToken.None);

		// Assert
		EnvelopeException actual = (await act.Should().ThrowAsync<EnvelopeException>()).Which;
		actual.Status.Should().Be(409);
		actual.Extensions.Should().BeEquivalentTo(new Dictionary<string, object?> { ["itemCount"] = 0, ["receiptCount"] = 1 });
	}

	// --- ItemTypeEnvelopeDefaultService ---

	[Fact]
	public async Task Default_SetCreatesThenReplaces()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		FakeTimeProvider clock = new(Now);
		ItemTypeEnvelopeDefaultService service = new(world.Factory, clock);

		// Act
		ItemTypeEnvelopeDefault created = await service.SetAsync(world.Gifts, world.Food, CancellationToken.None);
		clock.Advance(TimeSpan.FromHours(1));
		ItemTypeEnvelopeDefault replaced = await service.SetAsync(world.Gifts, world.Car, CancellationToken.None);

		// Assert
		created.Should().Be(new ItemTypeEnvelopeDefault(world.Gifts, world.Food, Now));
		replaced.Should().Be(new ItemTypeEnvelopeDefault(world.Gifts, world.Car, Now.AddHours(1)));
		(await service.GetAllAsync(CancellationToken.None)).Where(row => row.CategoryId == world.Gifts).Should().ContainSingle();
	}

	[Fact]
	public async Task Default_ItemTypeNotInCurrentTaxonomy_Throws400()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ItemTypeEnvelopeDefaultService service = new(world.Factory, new FakeTimeProvider(Now));

		// Act
		Func<Task> act = () => service.SetAsync(Guid.NewGuid(), world.Food, CancellationToken.None);

		// Assert
		(await act.Should().ThrowAsync<EnvelopeException>()).Which.Status.Should().Be(400);
	}

	[Fact]
	public async Task Default_ArchivedItemType_Throws400()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			(await db.TaxonomyNodes.SingleAsync(row => row.CategoryId == world.Gifts)).Archived = true;
			await db.SaveChangesAsync();
		}
		ItemTypeEnvelopeDefaultService service = new(world.Factory, new FakeTimeProvider(Now));

		// Act
		Func<Task> act = () => service.SetAsync(world.Gifts, world.Food, CancellationToken.None);

		// Assert
		(await act.Should().ThrowAsync<EnvelopeException>()).Which.Status.Should().Be(400);
	}

	[Fact]
	public async Task Default_ArchivedEnvelope_Throws400()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ItemTypeEnvelopeDefaultService service = new(world.Factory, new FakeTimeProvider(Now));

		// Act
		Func<Task> act = () => service.SetAsync(world.Gifts, world.Retired, CancellationToken.None);

		// Assert
		(await act.Should().ThrowAsync<EnvelopeException>()).Which.Status.Should().Be(400);
	}

	[Fact]
	public async Task Default_Delete_ReturnsWhetherARowWasRemoved()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ItemTypeEnvelopeDefaultService service = new(world.Factory, new FakeTimeProvider(Now));

		// Act
		bool first = await service.DeleteAsync(world.Fuel, CancellationToken.None);
		bool second = await service.DeleteAsync(world.Fuel, CancellationToken.None);

		// Assert
		first.Should().BeTrue();
		second.Should().BeFalse();
		(await service.GetAllAsync(CancellationToken.None)).Select(row => row.CategoryId).Should().Equal(world.Groceries);
	}

	// --- YnabEnvelopeMappingService ---

	[Fact]
	public async Task Mapping_SetCreatesThenReplacesTheSameRow()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		FakeTimeProvider clock = new(Now);
		YnabEnvelopeMappingService service = new(world.Factory, clock, _publisher.Object);

		// Act
		YnabEnvelopeMappingDto created = await service.SetAsync(world.Food, BudgetA, "cat-1", "Groceries", "Needs", CancellationToken.None);
		clock.Advance(TimeSpan.FromMinutes(5));
		YnabEnvelopeMappingDto replaced = await service.SetAsync(world.Food, BudgetA, "cat-2", "Dining", "Wants", CancellationToken.None);

		// Assert
		replaced.Id.Should().Be(created.Id);
		replaced.Should().BeEquivalentTo(new YnabEnvelopeMappingDto(created.Id, world.Food, "Food", "cat-2", "Dining", "Wants", BudgetA,
			Now, Now.AddMinutes(5)));
		(await service.GetByBudgetIdAsync(BudgetA, CancellationToken.None)).Should().ContainSingle().Which.Should().BeEquivalentTo(replaced);
		_publisher.Verify(p => p.PublishAsync(new CommittedEntityChange(CommittedEntityType.YnabMapping, CommittedChangeType.Created, created.Id, false)), Times.Once);
		_publisher.Verify(p => p.PublishAsync(new CommittedEntityChange(CommittedEntityType.YnabMapping, CommittedChangeType.Updated, created.Id, false)), Times.Once);
	}

	[Fact]
	public async Task Mapping_SetForMissingEnvelope_ThrowsKeyNotFound()
	{
		// Arrange
		YnabEnvelopeMappingService service = new((await EnvelopeWorld.CreateAsync()).Factory, new FakeTimeProvider(Now), _publisher.Object);

		// Act
		Func<Task> act = () => service.SetAsync(Guid.NewGuid(), BudgetA, "cat-1", "Groceries", "Needs", CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<KeyNotFoundException>();
	}

	[Fact]
	public async Task Mapping_GetByBudget_ReturnsOnlyThatBudgetOrderedByEnvelopeName()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		await AddLinksAsync(world, Link(world.Food, BudgetA, "a-food"), Link(world.Car, BudgetA, "a-car"), Link(world.Food, BudgetB, "b-food"));
		YnabEnvelopeMappingService service = new(world.Factory, new FakeTimeProvider(Now), _publisher.Object);

		// Act
		List<YnabEnvelopeMappingDto> actual = await service.GetByBudgetIdAsync(BudgetA, CancellationToken.None);

		// Assert
		actual.Select(row => (row.EnvelopeName, row.YnabCategoryId)).Should().Equal(("Car", "a-car"), ("Food", "a-food"));
	}

	[Fact]
	public async Task Mapping_DeleteOnlyRemovesTheSelectedBudgetsLink()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		await AddLinksAsync(world, Link(world.Food, BudgetA, "a-food"), Link(world.Food, BudgetB, "b-food"));
		YnabEnvelopeMappingService service = new(world.Factory, new FakeTimeProvider(Now), _publisher.Object);

		// Act
		bool deleted = await service.DeleteAsync(world.Food, BudgetA, CancellationToken.None);
		bool again = await service.DeleteAsync(world.Food, BudgetA, CancellationToken.None);

		// Assert
		deleted.Should().BeTrue();
		again.Should().BeFalse();
		(await service.GetByBudgetIdAsync(BudgetB, CancellationToken.None)).Should().ContainSingle();
	}

	[Fact]
	public async Task Mapping_Unmapped_ListsActiveAndInUseArchivedEnvelopesWithoutALink()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		Guid unusedArchived = Guid.NewGuid();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			db.Envelopes.Add(new EnvelopeEntity { Id = unusedArchived, Name = "Unused archived", IsActive = false });
			ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
			item.EnvelopeId = world.Retired;
			db.ReceiptItems.Add(item);
			db.YnabEnvelopeMappings.AddRange(Link(world.Food, BudgetA, "a-food"), Link(world.Car, BudgetB, "b-car"));
			await db.SaveChangesAsync();
		}
		YnabEnvelopeMappingService service = new(world.Factory, new FakeTimeProvider(Now), _publisher.Object);

		// Act
		List<Envelope> actual = await service.GetUnmappedEnvelopesAsync(BudgetA, CancellationToken.None);

		// Assert
		actual.Select(envelope => envelope.Name).Should().Equal("Car", "Retired");
	}

	[Fact]
	public async Task Mapping_StaleCountAndDeleteTargetOtherBudgets()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		await AddLinksAsync(world, Link(world.Food, BudgetA, "a-food"), Link(world.Food, BudgetB, "b-food"), Link(world.Car, BudgetB, "b-car"));
		YnabEnvelopeMappingService service = new(world.Factory, new FakeTimeProvider(Now), _publisher.Object);

		// Act
		int counted = await service.CountStaleMappingsAsync(BudgetA, CancellationToken.None);
		int deleted = await service.DeleteStaleMappingsAsync(BudgetA, CancellationToken.None);
		int remaining = await service.CountStaleMappingsAsync(BudgetA, CancellationToken.None);

		// Assert
		counted.Should().Be(2);
		deleted.Should().Be(2);
		remaining.Should().Be(0);
		(await service.GetByBudgetIdAsync(BudgetA, CancellationToken.None)).Should().ContainSingle();
		_publisher.Verify(p => p.PublishAsync(new CommittedEntityChange(CommittedEntityType.YnabMapping, CommittedChangeType.Deleted, null, false)), Times.Once);
	}

	[Fact]
	public async Task Mapping_DeleteStaleWithNothingStale_PublishesNothing()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		await AddLinksAsync(world, Link(world.Food, BudgetA, "a-food"));
		YnabEnvelopeMappingService service = new(world.Factory, new FakeTimeProvider(Now), _publisher.Object);

		// Act
		int actual = await service.DeleteStaleMappingsAsync(BudgetA, CancellationToken.None);

		// Assert
		actual.Should().Be(0);
		_publisher.Verify(p => p.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Never);
	}

	private static YnabEnvelopeMappingEntity Link(Guid envelopeId, string budget, string category) => new()
	{
		Id = Guid.NewGuid(),
		EnvelopeId = envelopeId,
		YnabBudgetId = budget,
		YnabCategoryId = category,
		YnabCategoryName = category,
		YnabCategoryGroupName = "Group",
		CreatedAt = Now,
		UpdatedAt = Now,
	};

	private static async Task AddLinksAsync(EnvelopeWorld world, params YnabEnvelopeMappingEntity[] links)
	{
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		db.YnabEnvelopeMappings.AddRange(links);
		await db.SaveChangesAsync();
	}
}
