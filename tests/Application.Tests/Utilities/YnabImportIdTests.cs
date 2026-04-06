using Application.Utilities;
using FluentAssertions;

namespace Application.Tests.Utilities;

public class YnabImportIdTests
{
	[Fact]
	public void Generate_StandardInput_ReturnsExpectedFormat()
	{
		// Arrange
		long milliunits = -11000;
		DateOnly date = new(2025, 3, 15);
		int occurrence = 1;

		// Act
		string actual = YnabImportId.Generate(milliunits, date, occurrence);

		// Assert
		string expected = "YNAB:-11000:2025-03-15:1";
		actual.Should().Be(expected);
	}

	[Fact]
	public void Generate_NegativeMilliunits_IncludesNegativeSign()
	{
		// Arrange
		long milliunits = -5500;
		DateOnly date = new(2025, 1, 1);
		int occurrence = 1;

		// Act
		string actual = YnabImportId.Generate(milliunits, date, occurrence);

		// Assert
		string expected = "YNAB:-5500:2025-01-01:1";
		actual.Should().Be(expected);
	}

	[Fact]
	public void Generate_PositiveMilliunits_FormatsCorrectly()
	{
		// Arrange
		long milliunits = 3000;
		DateOnly date = new(2025, 12, 31);
		int occurrence = 1;

		// Act
		string actual = YnabImportId.Generate(milliunits, date, occurrence);

		// Assert
		string expected = "YNAB:3000:2025-12-31:1";
		actual.Should().Be(expected);
	}

	[Fact]
	public void Generate_ZeroMilliunits_FormatsCorrectly()
	{
		// Arrange
		long milliunits = 0;
		DateOnly date = new(2025, 6, 15);
		int occurrence = 1;

		// Act
		string actual = YnabImportId.Generate(milliunits, date, occurrence);

		// Assert
		string expected = "YNAB:0:2025-06-15:1";
		actual.Should().Be(expected);
	}

	[Fact]
	public void Generate_OccurrenceGreaterThanOne_IncludesOccurrence()
	{
		// Arrange
		long milliunits = -11000;
		DateOnly date = new(2025, 3, 15);
		int occurrence = 3;

		// Act
		string actual = YnabImportId.Generate(milliunits, date, occurrence);

		// Assert
		string expected = "YNAB:-11000:2025-03-15:3";
		actual.Should().Be(expected);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(-100)]
	public void Generate_OccurrenceLessThanOne_ThrowsArgumentOutOfRangeException(int occurrence)
	{
		// Arrange
		long milliunits = -11000;
		DateOnly date = new(2025, 3, 15);

		// Act
		Action act = () => YnabImportId.Generate(milliunits, date, occurrence);

		// Assert
		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void Generate_ResultExceeds36Characters_ThrowsInvalidOperationException()
	{
		// Arrange — long.MaxValue (19 digits) pushes the string beyond 36 chars
		// "YNAB:" (5) + 19-digit number + ":" (1) + "2025-03-15" (10) + ":" (1) + "1" (1) = 37
		long milliunits = long.MaxValue;
		DateOnly date = new(2025, 3, 15);
		int occurrence = 1;

		// Act
		Action act = () => YnabImportId.Generate(milliunits, date, occurrence);

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*exceeds YNAB's 36-character limit*");
	}
}
