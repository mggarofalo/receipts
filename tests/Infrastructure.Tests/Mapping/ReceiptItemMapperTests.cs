using Common;
using Domain;
using Domain.Core;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;

namespace Infrastructure.Tests.Mapping;

public class ReceiptItemMapperTests
{
	[Theory]
	[InlineData(625)]
	[InlineData(626)]
	public void Write_ExactOrToleratedTotal_PreservesSuppliedFacts(int cents)
	{
		// Arrange
		decimal total = cents / 100m;
		ReceiptItem source = new(Guid.NewGuid(), " raw-code ", " Raw description ", 2,
			new Money(3.125m), new Money(total), "Historical category", null);

		// Act
		ReceiptItemEntity actual = new ReceiptItemMapper().ToEntity(source);

		// Assert
		Assert.Equal(source.Id, actual.Id);
		Assert.Equal(source.ReceiptItemCode, actual.ReceiptItemCode);
		Assert.Equal(source.Description, actual.Description);
		Assert.Equal(source.Quantity, actual.Quantity);
		Assert.Equal(3.125m, actual.UnitPrice);
		Assert.Equal(total, actual.TotalAmount);
		Assert.Equal(Currency.USD, actual.UnitPriceCurrency);
		Assert.Equal(Currency.USD, actual.TotalAmountCurrency);
		Assert.Equal(source.Category, actual.Category);
		Assert.Null(actual.Subcategory);
	}

	[Fact]
	public void Read_HistoricalTotalOutsideWriteTolerance_PreservesStoredFacts()
	{
		// Arrange
		ReceiptItemEntity source = HistoricalItem();
		// Act
		ReceiptItem actual = new ReceiptItemMapper().ToDomain(source);

		// Assert
		Assert.Equal(source.Id, actual.Id);
		Assert.Equal(source.ReceiptId, actual.ReceiptId);
		Assert.Equal(source.ReceiptItemCode, actual.ReceiptItemCode);
		Assert.Equal(source.Description, actual.Description);
		Assert.Equal(source.Quantity, actual.Quantity);
		Assert.Equal(new Money(source.UnitPrice, source.UnitPriceCurrency), actual.UnitPrice);
		Assert.Equal(new Money(source.TotalAmount, source.TotalAmountCurrency), actual.TotalAmount);
		Assert.Equal(source.Category, actual.Category);
		Assert.Equal(source.Subcategory, actual.Subcategory);
		Assert.Equal(source.NormalizationRevision, actual.NormalizationRevision);
		Assert.Equal(source.NormalizedDescriptionId, actual.NormalizedDescriptionId);
		Assert.Equal(source.NormalizedDescriptionMatchScore, actual.NormalizedDescriptionMatchScore);
		Assert.Equal(source.NormalizationReviewState, actual.NormalizationReviewState);
		Assert.Equal(source.NormalizationReviewReason, actual.NormalizationReviewReason);
		Assert.Equal(source.NormalizationReviewDecisionId, actual.NormalizationReviewDecisionId);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Write_InconsistentTotal_RejectsNewAndEditedItems(bool persisted)
	{
		// Arrange: mutable transfer models must be checked again at the write boundary.
		ReceiptItem source = new(persisted ? Guid.NewGuid() : Guid.Empty, null, "Raw item", 2,
			new Money(3.125m), new Money(6.25m), "Food", null);
		source.TotalAmount = new Money(6.12m);

		// Act
		ArgumentException actual = Assert.Throws<ArgumentException>(() => new ReceiptItemMapper().ToEntity(source));

		// Assert
		Assert.StartsWith(ReceiptItem.TotalAmountExceedsTolerance, actual.Message);
	}

	[Fact]
	public void Write_HistoricalReadCannotBypassCurrentWriteValidation()
	{
		// Arrange
		ReceiptItemMapper mapper = new();
		ReceiptItem source = mapper.ToDomain(HistoricalItem());

		// Act
		ArgumentException actual = Assert.Throws<ArgumentException>(() => mapper.ToEntity(source));

		// Assert
		Assert.StartsWith(ReceiptItem.TotalAmountExceedsTolerance, actual.Message);
	}

	internal static ReceiptItemEntity HistoricalItem() => new()
	{
		Id = Guid.NewGuid(),
		ReceiptId = Guid.NewGuid(),
		ReceiptItemCode = "  HIST-001  ",
		Description = "  RAW historical item  ",
		Quantity = 1.2345m,
		UnitPrice = 3.1257m,
		UnitPriceCurrency = Currency.USD,
		TotalAmount = 3.62m,
		TotalAmountCurrency = Currency.USD,
		Category = "Historical category",
		Subcategory = "Historical subcategory",
		NormalizationRevision = Guid.NewGuid(),
		NormalizedDescriptionId = Guid.NewGuid(),
		NormalizedDescriptionMatchScore = 0.75,
		NormalizationReviewState = NormalizationReviewState.NeedsReview,
		NormalizationReviewReason = "Historical source",
		NormalizationReviewDecisionId = Guid.NewGuid()
	};
}
