using Application.Utilities;
using FluentAssertions;

namespace Application.Tests.Utilities;

public class YnabImportIdTests
{
	private static readonly Guid TestReceiptId = Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789");
	private static readonly string TestReceiptPrefix = TestReceiptId.ToString("N")[..6]; // "abcdef"

	[Fact]
	public void Generate_TransactionIdentity_IsGloballyStableAndExactly36Characters()
	{
		Guid transactionId = Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789");

		string first = YnabImportId.Generate(transactionId);
		string repeated = YnabImportId.Generate(transactionId);
		string other = YnabImportId.Generate(Guid.Parse("abcdef01-2345-6789-abcd-ef0123456788"));

		first.Should().Be("YNABabcdef0123456789abcdef0123456789");
		first.Should().HaveLength(36);
		repeated.Should().Be(first);
		other.Should().NotBe(first);
	}

	[Fact]
	public void Generate_StandardInput_ReturnsExpectedFormat()
	{
		// Arrange
		long milliunits = -11000;
		DateOnly date = new(2025, 3, 15);
		int occurrence = 1;

		// Act
		string actual = YnabImportId.Generate(milliunits, date, TestReceiptId, occurrence);

		// Assert
		string expected = $"YNAB:-11000:2025-03-15:{TestReceiptPrefix}:1";
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
		string actual = YnabImportId.Generate(milliunits, date, TestReceiptId, occurrence);

		// Assert
		string expected = $"YNAB:-5500:2025-01-01:{TestReceiptPrefix}:1";
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
		string actual = YnabImportId.Generate(milliunits, date, TestReceiptId, occurrence);

		// Assert
		string expected = $"YNAB:3000:2025-12-31:{TestReceiptPrefix}:1";
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
		string actual = YnabImportId.Generate(milliunits, date, TestReceiptId, occurrence);

		// Assert
		string expected = $"YNAB:0:2025-06-15:{TestReceiptPrefix}:1";
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
		string actual = YnabImportId.Generate(milliunits, date, TestReceiptId, occurrence);

		// Assert
		string expected = $"YNAB:-11000:2025-03-15:{TestReceiptPrefix}:3";
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
		Action act = () => YnabImportId.Generate(milliunits, date, TestReceiptId, occurrence);

		// Assert
		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void Generate_ResultExceeds36Characters_ThrowsInvalidOperationException()
	{
		// Arrange — long.MaxValue (19 digits) pushes the string beyond 36 chars
		long milliunits = long.MaxValue;
		DateOnly date = new(2025, 3, 15);
		int occurrence = 1;

		// Act
		Action act = () => YnabImportId.Generate(milliunits, date, TestReceiptId, occurrence);

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*exceeds YNAB's 36-character limit*");
	}

	[Fact]
	public void Generate_DifferentReceiptIds_ProduceDifferentImportIds()
	{
		// Arrange
		long milliunits = -11000;
		DateOnly date = new(2025, 3, 15);
		int occurrence = 1;
		Guid receiptId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
		Guid receiptId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

		// Act
		string id1 = YnabImportId.Generate(milliunits, date, receiptId1, occurrence);
		string id2 = YnabImportId.Generate(milliunits, date, receiptId2, occurrence);

		// Assert
		id1.Should().NotBe(id2);
	}

	[Fact]
	public void Generate_ReceiptPrefixIsFirst6HexCharsWithoutHyphens()
	{
		// Arrange
		Guid receiptId = Guid.Parse("deadbeef-cafe-babe-dead-beefcafebabe");
		long milliunits = -1000;
		DateOnly date = new(2025, 1, 1);
		int occurrence = 1;

		// Act
		string actual = YnabImportId.Generate(milliunits, date, receiptId, occurrence);

		// Assert
		actual.Should().Contain("deadbe");
		actual.Should().Be("YNAB:-1000:2025-01-01:deadbe:1");
	}

	[Fact]
	public void Generate_TypicalValues_StaysWithin36CharLimit()
	{
		// Arrange — typical receipt amount in milliunits and common date
		long milliunits = -9999999;
		DateOnly date = new(2025, 12, 31);
		int occurrence = 1;

		// Act
		string actual = YnabImportId.Generate(milliunits, date, TestReceiptId, occurrence);

		// Assert
		actual.Length.Should().BeLessThanOrEqualTo(36);
	}
}
