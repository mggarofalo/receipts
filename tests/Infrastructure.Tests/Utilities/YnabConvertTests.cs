using FluentAssertions;
using Infrastructure.Utilities;

namespace Infrastructure.Tests.Utilities;

public class YnabConvertTests
{
	[Theory]
	[InlineData(12.34, 12340L)]
	[InlineData(0.01, 10L)]
	[InlineData(0, 0L)]
	[InlineData(-10.50, -10500L)]
	public void ToMilliunits_ConvertsCorrectly(decimal amount, long expected)
	{
		// Act
		long result = YnabConvert.ToMilliunits(amount);

		// Assert
		result.Should().Be(expected);
	}

	[Fact]
	public void ToMilliunits_RoundsAwayFromZero_NotBankersRounding()
	{
		// 1.2345 * 1000 = 1234.5 — AwayFromZero rounds to 1235, bankers to 1234
		long result = YnabConvert.ToMilliunits(1.2345m);

		result.Should().Be(1235L);
	}

	[Theory]
	[InlineData(12340L, 12.34)]
	[InlineData(1L, 0.001)]
	[InlineData(0L, 0)]
	public void FromMilliunits_ConvertsCorrectly(long milliunits, decimal expected)
	{
		// Act
		decimal result = YnabConvert.FromMilliunits(milliunits);

		// Assert
		result.Should().Be(expected);
	}
}
