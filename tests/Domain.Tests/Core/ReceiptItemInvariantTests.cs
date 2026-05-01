using Common;
using Domain.Core;

namespace Domain.Tests.Core;

public class ReceiptItemInvariantTests
{
	[Fact]
	public void Constructor_ZeroUnitPrice_ThrowsArgumentException()
	{
		// Arrange
		Money unitPrice = new(0m);
		Money totalAmount = new(0m);

		// Act & Assert
		ArgumentException exception = Assert.Throws<ArgumentException>(
			() => new ReceiptItem(Guid.NewGuid(), "ITEM001", "Test", 1, unitPrice, totalAmount, "Cat", "Sub"));
		Assert.StartsWith(ReceiptItem.UnitPriceMustBePositive, exception.Message);
	}

	[Fact]
	public void Constructor_NegativeUnitPrice_ThrowsArgumentException()
	{
		// Arrange
		Money unitPrice = new(-5.00m);
		Money totalAmount = new(-5.00m);

		// Act & Assert
		ArgumentException exception = Assert.Throws<ArgumentException>(
			() => new ReceiptItem(Guid.NewGuid(), "ITEM001", "Test", 1, unitPrice, totalAmount, "Cat", "Sub"));
		Assert.StartsWith(ReceiptItem.UnitPriceMustBePositive, exception.Message);
	}

	[Fact]
	public void Constructor_TotalWithinTolerance_Succeeds()
	{
		// Arrange: qty=3, unitPrice=$3.33, expected floor = floor(3*3.33*100)/100 = floor(999)/100 = $9.99
		// total=$9.99 is exact match → within tolerance
		Money unitPrice = new(3.33m);
		Money totalAmount = new(9.99m);

		// Act
		ReceiptItem item = new(Guid.NewGuid(), "ITEM001", "Test", 3, unitPrice, totalAmount, "Cat", "Sub");

		// Assert
		Assert.Equal(9.99m, item.TotalAmount.Amount);
	}

	[Fact]
	public void Constructor_TotalWithinOneCentTolerance_Succeeds()
	{
		// Arrange: qty=3, unitPrice=$3.33, floor expected = $9.99
		// total=$10.00 is within $0.01 tolerance
		Money unitPrice = new(3.33m);
		Money totalAmount = new(10.00m);

		// Act
		ReceiptItem item = new(Guid.NewGuid(), "ITEM001", "Test", 3, unitPrice, totalAmount, "Cat", "Sub");

		// Assert
		Assert.Equal(10.00m, item.TotalAmount.Amount);
	}

	[Fact]
	public void Constructor_TotalExceedsTolerance_ThrowsArgumentException()
	{
		// Arrange: qty=3, unitPrice=$3.33, floor expected = $9.99
		// total=$10.02 exceeds $0.01 tolerance
		Money unitPrice = new(3.33m);
		Money totalAmount = new(10.02m);

		// Act & Assert
		ArgumentException exception = Assert.Throws<ArgumentException>(
			() => new ReceiptItem(Guid.NewGuid(), "ITEM001", "Test", 3, unitPrice, totalAmount, "Cat", "Sub"));
		Assert.StartsWith(ReceiptItem.TotalAmountExceedsTolerance, exception.Message);
	}

	[Fact]
	public void Constructor_ExactTotal_Succeeds()
	{
		// Arrange: qty=2, unitPrice=$10.00, expected=$20.00
		Money unitPrice = new(10.00m);
		Money totalAmount = new(20.00m);

		// Act
		ReceiptItem item = new(Guid.NewGuid(), "ITEM001", "Test", 2, unitPrice, totalAmount, "Cat", "Sub");

		// Assert
		Assert.Equal(20.00m, item.TotalAmount.Amount);
	}

	[Fact]
	public void Constructor_TotalBelowExpectedByOneCent_Succeeds()
	{
		// Arrange: qty=2, unitPrice=$10.00, floor expected=$20.00
		// total=$19.99 is within $0.01
		Money unitPrice = new(10.00m);
		Money totalAmount = new(19.99m);

		// Act
		ReceiptItem item = new(Guid.NewGuid(), "ITEM001", "Test", 2, unitPrice, totalAmount, "Cat", "Sub");

		// Assert
		Assert.Equal(19.99m, item.TotalAmount.Amount);
	}

	[Fact]
	public void Constructor_TotalBelowExpectedByTwoCents_ThrowsArgumentException()
	{
		// Arrange: qty=2, unitPrice=$10.00, floor expected=$20.00
		// total=$19.98 exceeds $0.01 tolerance
		Money unitPrice = new(10.00m);
		Money totalAmount = new(19.98m);

		// Act & Assert
		ArgumentException exception = Assert.Throws<ArgumentException>(
			() => new ReceiptItem(Guid.NewGuid(), "ITEM001", "Test", 2, unitPrice, totalAmount, "Cat", "Sub"));
		Assert.StartsWith(ReceiptItem.TotalAmountExceedsTolerance, exception.Message);
	}

	// RECEIPTS-655: flat-priced items legitimately have unitPrice = 0 because
	// the source receipt prints only a line total. Domain must accept that
	// when pricingMode == Flat as long as totalAmount is positive.
	[Fact]
	public void Constructor_FlatMode_ZeroUnitPrice_PositiveTotal_Succeeds()
	{
		// Arrange — Walmart shape.
		Money unitPrice = new(0m);
		Money totalAmount = new(4.97m);

		// Act
		ReceiptItem item = new(
			Guid.NewGuid(),
			"WMT-001",
			"GV WHL MLK",
			1,
			unitPrice,
			totalAmount,
			"Groceries",
			null,
			PricingMode.Flat);

		// Assert
		Assert.Equal(PricingMode.Flat, item.PricingMode);
		Assert.Equal(0m, item.UnitPrice.Amount);
		Assert.Equal(4.97m, item.TotalAmount.Amount);
	}

	[Fact]
	public void Constructor_FlatMode_ZeroTotal_ThrowsArgumentException()
	{
		// Arrange — flat-priced rows with no positive total are nonsensical.
		Money unitPrice = new(0m);
		Money totalAmount = new(0m);

		// Act & Assert
		ArgumentException exception = Assert.Throws<ArgumentException>(
			() => new ReceiptItem(
				Guid.NewGuid(),
				"WMT-001",
				"X",
				1,
				unitPrice,
				totalAmount,
				"Groceries",
				null,
				PricingMode.Flat));
		Assert.StartsWith(ReceiptItem.TotalAmountMustBePositive, exception.Message);
	}

	[Fact]
	public void Constructor_FlatMode_NegativeUnitPrice_ThrowsArgumentException()
	{
		// Arrange
		Money unitPrice = new(-1m);
		Money totalAmount = new(4.97m);

		// Act & Assert
		ArgumentException exception = Assert.Throws<ArgumentException>(
			() => new ReceiptItem(
				Guid.NewGuid(),
				"WMT-001",
				"X",
				1,
				unitPrice,
				totalAmount,
				"Groceries",
				null,
				PricingMode.Flat));
		Assert.StartsWith(ReceiptItem.UnitPriceMustBeNonNegative, exception.Message);
	}
}
