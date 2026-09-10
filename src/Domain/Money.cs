using Common;

namespace Domain;

public record Money(decimal Amount, Currency Currency)
{
	public Money(decimal amount) : this(amount, Currency.USD) { }

	public static Money Zero => new(0);

	public static Money operator +(Money a, Money b)
	{
		EnsureSameCurrency(a, b);
		return new Money(a.Amount + b.Amount, a.Currency);
	}

	public static Money operator -(Money a, Money b)
	{
		EnsureSameCurrency(a, b);
		return new Money(a.Amount - b.Amount, a.Currency);
	}

	public static Money operator *(Money money, decimal scalar) => new(money.Amount * scalar, money.Currency);
	public static Money operator *(decimal scalar, Money money) => money * scalar;
	public static Money operator /(Money money, decimal scalar) => new(money.Amount / scalar, money.Currency);
	public static decimal operator /(Money numerator, Money denominator) => numerator.RatioTo(denominator);

	public decimal RatioTo(Money denominator)
	{
		EnsureSameCurrency(this, denominator);
		return Amount / denominator.Amount;
	}

	private static void EnsureSameCurrency(Money left, Money right)
	{
		if (left.Currency != right.Currency)
		{
			throw new InvalidOperationException(
				$"Cannot combine {left.Currency} and {right.Currency} monetary values.");
		}
	}
}
