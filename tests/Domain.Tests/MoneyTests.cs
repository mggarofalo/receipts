using Common;

namespace Domain.Tests;

public class MoneyTests
{
	[Fact]
	public void Money_CreatedWithAmount_ShouldHaveCorrectAmount()
	{
		Money money = new(100.50m);
		Assert.Equal(100.50m, money.Amount);
	}

	[Fact]
	public void Money_CreatedWithoutCurrency_ShouldDefaultToUSD()
	{
		Money money = new(50);
		Assert.Equal(Currency.USD, money.Currency);
	}

	[Fact]
	public void Money_EqualityCheck_ShouldBeTrue_WhenAmountsAreEqual()
	{
		Money money1 = new(75.25m);
		Money money2 = new(75.25m);
		Assert.Equal(money1, money2);
	}

	[Fact]
	public void Money_EqualityCheck_ShouldBeFalse_WhenAmountsAreDifferent()
	{
		Money money1 = new(75.25m);
		Money money2 = new(75.26m);
		Assert.NotEqual(money1, money2);
	}

	[Fact]
	public void Money_ToString_ShouldReturnCorrectString()
	{
		Money money = new(123.45m);
		Assert.Equal("Money { Amount = 123.45, Currency = USD }", money.ToString());
	}

	[Fact]
	public void Money_Zero_ShouldReturnCorrectResult()
	{
		Money money = Money.Zero;
		Assert.Equal(0m, money.Amount);
	}

	[Fact]
	public void Money_Addition_WithSameCurrency_ReturnsSumInThatCurrency()
	{
		Money money1 = new(100.50m, Currency.USD);
		Money money2 = new(200.75m, Currency.USD);
		Money result = money1 + money2;
		Assert.Equal(new Money(301.25m, Currency.USD), result);
	}

	[Fact]
	public void Money_Subtraction_WithSameCurrency_ReturnsDifferenceInThatCurrency()
	{
		Money money1 = new(200.75m, Currency.USD);
		Money money2 = new(100.50m, Currency.USD);
		Money result = money1 - money2;
		Assert.Equal(new Money(100.25m, Currency.USD), result);
	}

	[Theory]
	[InlineData(2, 201)]
	[InlineData(-0.5, -50.25)]
	public void Money_Multiplication_ByScalar_ReturnsScaledMoney(decimal scalar, decimal expectedAmount)
	{
		Money money = new(100.50m, Currency.USD);

		Assert.Equal(new Money(expectedAmount, Currency.USD), money * scalar);
		Assert.Equal(new Money(expectedAmount, Currency.USD), scalar * money);
	}

	[Fact]
	public void Money_Division_ByScalar_ReturnsScaledMoney()
	{
		Money money = new(201.00m, Currency.USD);

		Money result = money / 2m;

		Assert.Equal(new Money(100.50m, Currency.USD), result);
	}

	[Fact]
	public void Money_Division_ByMoneyWithSameCurrency_ReturnsDimensionlessRatio()
	{
		decimal result = new Money(201m, Currency.USD) / new Money(2m, Currency.USD);

		Assert.Equal(100.5m, result);
	}

	[Theory]
	[InlineData("add")]
	[InlineData("subtract")]
	[InlineData("ratio")]
	public void Money_Operations_WithMismatchedCurrencies_Throw(string operation)
	{
		Money usd = new(10m, Currency.USD);
		Money unsupportedCurrency = new(2m, (Currency)999);

		Action act = operation switch
		{
			"add" => () => _ = usd + unsupportedCurrency,
			"subtract" => () => _ = usd - unsupportedCurrency,
			"ratio" => () => _ = usd / unsupportedCurrency,
			_ => throw new ArgumentOutOfRangeException(nameof(operation)),
		};

		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(act);
		Assert.Contains("USD", exception.Message);
		Assert.Contains("999", exception.Message);
	}
}
