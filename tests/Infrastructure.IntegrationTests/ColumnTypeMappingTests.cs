using Common;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ColumnTypeMappingTests(PostgresFixture fixture)
{
	[Fact]
	public async Task CardEntity_RoundTrips_AllColumnTypes()
	{
		// Arrange — uuid, text, boolean, nullable-uuid FK. AccountId is nullable,
		// so the row inserts without a parent Account, but that leaves the FK column
		// untested. Seed a parent AccountEntity and populate AccountId so every
		// column on CardEntity round-trips.
		await using ApplicationDbContext context = fixture.CreateDbContext();
		AccountEntity parent = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = parent.Id;

		// Act
		context.Accounts.Add(parent);
		context.Cards.Add(card);
		await context.SaveChangesAsync();

		// Assert
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		CardEntity? loaded = await readContext.Cards.FirstOrDefaultAsync(a => a.Id == card.Id);

		loaded.Should().NotBeNull();
		loaded!.Id.Should().Be(card.Id);
		loaded.CardCode.Should().Be(card.CardCode);
		loaded.Name.Should().Be(card.Name);
		loaded.IsActive.Should().Be(card.IsActive);
		loaded.AccountId.Should().Be(parent.Id);
	}

	[Fact]
	public async Task ReceiptEntity_RoundTrips_DecimalAndDateOnly()
	{
		// Arrange — decimal(18,2), date, uuid, text, enum-to-text
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();

		// Act
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();

		// Assert
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		ReceiptEntity? loaded = await readContext.Receipts.FirstOrDefaultAsync(r => r.Id == receipt.Id);

		loaded.Should().NotBeNull();
		loaded!.Location.Should().Be(receipt.Location);
		loaded.Date.Should().Be(receipt.Date);
		loaded.TaxAmount.Should().Be(receipt.TaxAmount);
		loaded.TaxAmountCurrency.Should().Be(Currency.USD);
	}

	[Fact]
	public async Task TransactionEntity_RoundTrips_WithForeignKeys()
	{
		// Arrange — FK to Receipt, Account, and Card.
		// Transaction.AccountId references Accounts (post-RECEIPTS-543);
		// Transaction.CardId is now NOT NULL and references Cards (post-RECEIPTS-574).
		// Card.AccountId is also required and references Accounts (post-RECEIPTS-575).
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		context.Receipts.Add(receipt);
		context.Accounts.Add(account);
		context.Cards.Add(card);
		await context.SaveChangesAsync();

		TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id, card.Id);

		// Act
		context.Transactions.Add(transaction);
		await context.SaveChangesAsync();

		// Assert
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		TransactionEntity? loaded = await readContext.Transactions.FirstOrDefaultAsync(t => t.Id == transaction.Id);

		loaded.Should().NotBeNull();
		loaded!.ReceiptId.Should().Be(receipt.Id);
		loaded.AccountId.Should().Be(account.Id);
		loaded.CardId.Should().Be(card.Id);
		loaded.Amount.Should().Be(transaction.Amount);
		loaded.AmountCurrency.Should().Be(Currency.USD);
		loaded.Date.Should().Be(transaction.Date);
	}

	[Fact]
	public async Task ReceiptItemEntity_RoundTrips_AllFields()
	{
		// Arrange
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();

		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);

		// Act
		context.ReceiptItems.Add(item);
		await context.SaveChangesAsync();

		// Assert
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		ReceiptItemEntity? loaded = await readContext.ReceiptItems.FirstOrDefaultAsync(i => i.Id == item.Id);

		loaded.Should().NotBeNull();
		loaded!.Quantity.Should().Be(item.Quantity);
		loaded.UnitPrice.Should().Be(item.UnitPrice);
		loaded.TotalAmount.Should().Be(item.TotalAmount);
		loaded.Category.Should().Be(item.Category);
	}

	[Fact]
	public async Task ReceiptItemEntity_FractionalQuantityAndSubCentUnitPrice_RoundTripWithoutTruncation()
	{
		// RECEIPTS-770: Quantity and UnitPrice were mapped to the money type decimal(18,2),
		// which silently rounds fractional quantities and sub-cent unit prices on insert.
		// With numeric(18,4) they must round-trip exactly. The chosen values have scale > 2,
		// so under the old (18,2) mapping this assertion would fail (rounded to 2 places).
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();

		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.Quantity = 1.125m;    // scale 3 — a fractional quantity (e.g. 1.125 kg)
		item.UnitPrice = 3.4599m;  // scale 4 — a sub-cent unit price (e.g. fuel per gallon)
		item.TotalAmount = 3.89m;  // money stays scale 2

		// Act
		context.ReceiptItems.Add(item);
		await context.SaveChangesAsync();

		// Assert — read on a fresh context so the values come back from Postgres, not the tracker
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		ReceiptItemEntity? loaded = await readContext.ReceiptItems.FirstOrDefaultAsync(i => i.Id == item.Id);

		loaded.Should().NotBeNull();
		loaded!.Quantity.Should().Be(1.125m, "numeric(18,4) must preserve a fractional quantity without rounding to scale 2");
		loaded.UnitPrice.Should().Be(3.4599m, "numeric(18,4) must preserve a sub-cent unit price without rounding to scale 2");
		loaded.TotalAmount.Should().Be(3.89m, "TotalAmount remains money at decimal(18,2)");
	}

	[Fact]
	public async Task AdjustmentEntity_RoundTrips_WithEnumToStringConversion()
	{
		// Arrange — AdjustmentType enum stored as text
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();

		AdjustmentEntity adjustment = new()
		{
			Id = Guid.NewGuid(),
			ReceiptId = receipt.Id,
			Type = AdjustmentType.Coupon,
			Amount = 3.50m,
			AmountCurrency = Currency.USD,
			Description = "Test coupon",
		};

		// Act
		context.Adjustments.Add(adjustment);
		await context.SaveChangesAsync();

		// Assert
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		AdjustmentEntity? loaded = await readContext.Adjustments.FirstOrDefaultAsync(a => a.Id == adjustment.Id);

		loaded.Should().NotBeNull();
		loaded!.Type.Should().Be(AdjustmentType.Coupon);
		loaded.Amount.Should().Be(3.50m);
		loaded.Description.Should().Be("Test coupon");
	}

	[Fact]
	public async Task CategoryAndSubcategory_RoundTrip_WithForeignKey()
	{
		// Arrange
		await using ApplicationDbContext context = fixture.CreateDbContext();
		CategoryEntity category = CategoryEntityGenerator.Generate();
		context.Categories.Add(category);
		await context.SaveChangesAsync();

		SubcategoryEntity subcategory = new()
		{
			Id = Guid.NewGuid(),
			Name = "Test Sub",
			CategoryId = category.Id,
			Description = "Sub description",
		};
		context.Subcategories.Add(subcategory);
		await context.SaveChangesAsync();

		// Assert
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		SubcategoryEntity? loaded = await readContext.Subcategories
			.Include(s => s.Category)
			.FirstOrDefaultAsync(s => s.Id == subcategory.Id);

		loaded.Should().NotBeNull();
		loaded!.CategoryId.Should().Be(category.Id);
		loaded.Category.Should().NotBeNull();
		loaded.Category!.Name.Should().Be(category.Name);
	}

	[Fact]
	public async Task ItemTemplateEntity_RoundTrips_AllFields()
	{
		// Arrange
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ItemTemplateEntity template = new()
		{
			Id = Guid.NewGuid(),
			Name = $"Template_{Guid.NewGuid():N}",
			DefaultCategory = "Groceries",
			DefaultSubcategory = "Produce",
			DefaultUnitPrice = 2.99m,
			DefaultUnitPriceCurrency = Currency.USD,
			DefaultItemCode = "PROD001",
			Description = "Fresh produce template",
		};

		// Act
		context.ItemTemplates.Add(template);
		await context.SaveChangesAsync();

		// Assert
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		ItemTemplateEntity? loaded = await readContext.ItemTemplates.FirstOrDefaultAsync(t => t.Id == template.Id);

		loaded.Should().NotBeNull();
		loaded!.Name.Should().Be(template.Name);
		loaded.DefaultCategory.Should().Be("Groceries");
		loaded.DefaultUnitPrice.Should().Be(2.99m);
		loaded.DefaultItemCode.Should().Be("PROD001");
	}

	[Fact]
	public async Task ItemEmbeddingEntity_RoundTrips_VectorColumn()
	{
		// Arrange — pgvector column type
		await using ApplicationDbContext context = fixture.CreateDbContext();
		float[] values = new float[OnnxEmbeddingService.EmbeddingDimension];
		for (int i = 0; i < values.Length; i++)
		{
			values[i] = i * 0.001f;
		}

		ItemEmbeddingEntity embedding = new()
		{
			Id = Guid.NewGuid(),
			EntityType = "ItemTemplate",
			EntityId = Guid.NewGuid(),
			EntityText = "Test embedding text",
			Embedding = new Vector(values),
			ModelVersion = OnnxEmbeddingService.ModelName,
			CreatedAt = DateTimeOffset.UtcNow,
		};

		// Act
		context.ItemEmbeddings.Add(embedding);
		await context.SaveChangesAsync();

		// Assert
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		ItemEmbeddingEntity? loaded = await readContext.ItemEmbeddings
			.FirstOrDefaultAsync(e => e.Id == embedding.Id);

		loaded.Should().NotBeNull();
		loaded!.EntityType.Should().Be("ItemTemplate");
		loaded.Embedding.ToArray().Should().HaveCount(OnnxEmbeddingService.EmbeddingDimension);
		loaded.Embedding.ToArray()[0].Should().BeApproximately(0f, 0.001f);
		loaded.ModelVersion.Should().Be(OnnxEmbeddingService.ModelName);
	}
}
