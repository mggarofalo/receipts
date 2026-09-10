using Domain.Core;

namespace Domain.Tests.Core;

public class TransactionTests
{
	[Fact]
	public void Constructor_ValidInput_CreatesTransaction()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		Money amount = new(100.50m);
		DateOnly date = DateOnly.FromDateTime(DateTime.Today);

		// Act
		Transaction transaction = new(id, cardId, amount, date);

		// Assert
		Assert.Equal(id, transaction.Id);
		Assert.Equal(cardId, transaction.CardId);
		Assert.Equal(amount, transaction.Amount);
		Assert.Equal(date, transaction.Date);
	}

	[Fact]
	public void Constructor_EmptyId_CreatesTransactionWithEmptyId()
	{
		// Arrange
		Money amount = new(100.50m);
		DateOnly date = DateOnly.FromDateTime(DateTime.Today);

		// Act
		Transaction transaction = new(Guid.Empty, Guid.NewGuid(), amount, date);

		// Assert
		Assert.Equal(Guid.Empty, transaction.Id);
	}

	[Fact]
	public void Constructor_ZeroAmount_ThrowsArgumentException()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		Money amount = new(0m);
		DateOnly date = DateOnly.FromDateTime(DateTime.Today);

		// Act & Assert
		ArgumentException exception = Assert.Throws<ArgumentException>(() => new Transaction(id, Guid.NewGuid(), amount, date));
		Assert.StartsWith(Transaction.AmountMustBeNonZero, exception.Message);
	}

	[Fact]
	public void Constructor_DateFutureRelativeToMachineClock_HydratesTransaction()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		Money amount = new(100.50m);
		DateOnly date = new(2999, 12, 31);

		// Act
		Transaction transaction = new(id, Guid.NewGuid(), amount, date);

		// Assert
		Assert.Equal(date, transaction.Date);
	}

	[Fact]
	public void Constructor_EmptyCardId_ThrowsArgumentException()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		Money amount = new(100.50m);
		DateOnly date = DateOnly.FromDateTime(DateTime.Today);

		// Act & Assert
		ArgumentException exception = Assert.Throws<ArgumentException>(() => new Transaction(id, Guid.Empty, amount, date));
		Assert.StartsWith(Transaction.CardIdCannotBeEmpty, exception.Message);
	}
}
