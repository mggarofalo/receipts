using Domain.Core;

namespace Domain.Tests.Core;

public class ReceiptTests
{
	[Fact]
	public void Constructor_ValidInput_CreatesReceipt()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		string location = "Test Store";
		DateOnly date = DateOnly.FromDateTime(DateTime.Today);
		Money taxAmount = new(5.00m);

		// Act
		Receipt receipt = new(id, location, date, taxAmount);

		// Assert
		Assert.Equal(id, receipt.Id);
		Assert.Equal(location, receipt.Location);
		Assert.Equal(date, receipt.Date);
		Assert.Equal(taxAmount, receipt.TaxAmount);
	}

	[Fact]
	public void Constructor_EmptyId_CreatesReceiptWithEmptyId()
	{
		// Arrange
		string location = "Test Store";
		DateOnly date = DateOnly.FromDateTime(DateTime.Today);
		Money taxAmount = new(5.00m);

		// Act
		Receipt receipt = new(Guid.Empty, location, date, taxAmount);

		// Assert
		Assert.Equal(Guid.Empty, receipt.Id);
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData(null)]
	public void Constructor_InvalidLocation_ThrowsArgumentException(string invalidLocation)
	{
		// Arrange
		Guid id = Guid.NewGuid();
		DateOnly date = DateOnly.FromDateTime(DateTime.Today);
		Money taxAmount = new(5.00m);

		// Act & Assert
		ArgumentException exception = Assert.Throws<ArgumentException>(() => new Receipt(id, invalidLocation, date, taxAmount));
		Assert.StartsWith(Receipt.LocationCannotBeEmpty, exception.Message);
	}

	[Fact]
	public void Constructor_DateFutureRelativeToMachineClock_HydratesReceipt()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		string location = "Test Store";
		DateOnly date = new(2999, 12, 31);
		Money taxAmount = new(5.00m);

		// Act
		Receipt receipt = new(id, location, date, taxAmount);

		// Assert
		Assert.Equal(date, receipt.Date);
	}
}
