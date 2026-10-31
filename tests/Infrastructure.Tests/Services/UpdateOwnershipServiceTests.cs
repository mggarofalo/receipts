using Common;
using Domain;
using Domain.Core;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Infrastructure.Tests.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

public class UpdateOwnershipServiceTests
{
	[Fact]
	public async Task ReceiptUpdate_ChangesEditableFields_PreservesArchivalImagePaths()
	{
		IDbContextFactory<ApplicationDbContext> factory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid id = Guid.NewGuid();
		await using (ApplicationDbContext seed = factory.CreateDbContext())
		{
			seed.Receipts.Add(new ReceiptEntity { Id = id, Location = "Before", Date = new DateOnly(2025, 1, 1), OriginalImagePath = "original.jpg", ProcessedImagePath = "processed.png" });
			await seed.SaveChangesAsync();
		}
		ReceiptService service = new(new ReceiptRepository(factory), new ReceiptMapper());

		await service.UpdateAsync([new Receipt(id, "After", new DateOnly(2025, 2, 1), new Money(2.5m, Currency.USD))], CancellationToken.None);

		await using (ApplicationDbContext read = factory.CreateDbContext())
		{
			ReceiptEntity stored = await read.Receipts.SingleAsync();
			stored.Location.Should().Be("After");
			stored.Date.Should().Be(new DateOnly(2025, 2, 1));
			stored.TaxAmount.Should().Be(2.5m);
			stored.TaxAmountCurrency.Should().Be(Currency.USD);
			stored.OriginalImagePath.Should().Be("original.jpg");
			stored.ProcessedImagePath.Should().Be("processed.png");
		}
	}

	[Theory]
	[InlineData("Milk")]
	[InlineData("Bread")]
	[InlineData("milk")]
	[InlineData("Milk ")]
	public async Task ItemUpdate_PreservesOnlyUnchangedDescriptionMetadata_AndKeepsStoredParent(string newDescription)
	{
		IDbContextFactory<ApplicationDbContext> factory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid receiptId = Guid.NewGuid(), itemId = Guid.NewGuid(), canonicalId = Guid.NewGuid();
		await using (ApplicationDbContext seed = factory.CreateDbContext())
		{
			seed.Receipts.Add(new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2025, 1, 1) });
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity { Id = canonicalId, CanonicalName = "Curated dairy" });
			seed.ReceiptItems.Add(new ReceiptItemEntity
			{
				Id = itemId,
				ReceiptId = receiptId,
				Description = "Milk",
				Quantity = 1,
				UnitPrice = 3,
				TotalAmount = 3,
				Category = "Food",
				NormalizedDescriptionId = canonicalId,
			});
			await seed.SaveChangesAsync();
		}
		ReceiptItemService service = new(new ReceiptItemRepository(factory), new ReceiptItemMapper());
		ReceiptItem update = new(itemId, "new-code", newDescription, 2, new Money(4, Currency.USD), new Money(8, Currency.USD), "Groceries", "Dairy")
		{
			NormalizedDescriptionId = Guid.NewGuid(),
		};

		await service.UpdateAsync([update], Guid.NewGuid(), CancellationToken.None);

		await using ApplicationDbContext read = factory.CreateDbContext();
		ReceiptItemEntity stored = await read.ReceiptItems.IgnoreAutoIncludes().SingleAsync();
		stored.ReceiptId.Should().Be(receiptId);
		stored.Description.Should().Be(newDescription);
		stored.ReceiptItemCode.Should().Be("new-code");
		stored.Quantity.Should().Be(2);
		stored.UnitPrice.Should().Be(4);
		stored.UnitPriceCurrency.Should().Be(Currency.USD);
		stored.TotalAmount.Should().Be(8);
		stored.TotalAmountCurrency.Should().Be(Currency.USD);
		stored.Category.Should().Be("Groceries");
		stored.Subcategory.Should().Be("Dairy");
		stored.NormalizedDescriptionId.Should().Be(newDescription == "Milk" ? canonicalId : null);
	}

	[Fact]
	public void CreateMapping_ExcludesCanonicalIdentityAndScore_ButReadMappingPreservesThem()
	{
		ReceiptItemMapper mapper = new();
		Guid canonicalId = Guid.NewGuid();
		ReceiptItem input = new(Guid.NewGuid(), null, "Milk", 1, new Money(3), new Money(3), "Food", null)
		{
			NormalizedDescriptionId = canonicalId,
		};

		ReceiptItemEntity created = mapper.ToEntity(input);

		created.NormalizedDescriptionId.Should().BeNull();
		created.NormalizedDescriptionId = canonicalId;
		ReceiptItem read = mapper.ToDomain(created);
		read.NormalizedDescriptionId.Should().Be(canonicalId);
	}

	[Fact]
	public async Task RepositoryUpdate_CannotWriteImageOrSoftDeleteMetadata_FromReplacementInput()
	{
		IDbContextFactory<ApplicationDbContext> factory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid id = Guid.NewGuid();
		await using (ApplicationDbContext seed = factory.CreateDbContext())
		{
			seed.Receipts.Add(new ReceiptEntity { Id = id, Location = "Before", Date = new DateOnly(2025, 1, 1) });
			await seed.SaveChangesAsync();
		}

		await new ReceiptRepository(factory).UpdateAsync([new ReceiptEntity
		{
			Id = id, Location = "After", Date = new DateOnly(2025, 1, 1), OriginalImagePath = "unowned.jpg",
			DeletedAt = DateTimeOffset.UtcNow, DeletedByUserId = "unowned", DeletedByApiKeyId = Guid.NewGuid(), CascadeDeletedByParentId = Guid.NewGuid(),
		}], CancellationToken.None);

		await using ApplicationDbContext read = factory.CreateDbContext();
		ReceiptEntity stored = await read.Receipts.IgnoreQueryFilters().SingleAsync();
		stored.Location.Should().Be("After");
		stored.OriginalImagePath.Should().BeNull();
		stored.ProcessedImagePath.Should().BeNull();
		stored.DeletedAt.Should().BeNull();
		stored.DeletedByUserId.Should().BeNull();
		stored.DeletedByApiKeyId.Should().BeNull();
		stored.CascadeDeletedByParentId.Should().BeNull();
	}
}
