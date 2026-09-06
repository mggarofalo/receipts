namespace API.Validators;

internal static class ItemTemplatePriceValidation
{
	public const string OutOfRange = "Default unit price must be less than 100,000,000,000,000.";
	private const double MaximumExclusive = 100_000_000_000_000d;

	public static bool FitsColumn(double? price)
	{
		if (price is not double value)
		{
			return true;
		}

		// Check both bounds before casting: other validators do not short-circuit this rule.
		// The API mapper's double-to-decimal conversion can round a near-limit value up.
		return double.IsFinite(value)
			&& value > -MaximumExclusive && value < MaximumExclusive
			&& Math.Abs((decimal)value) < 100_000_000_000_000m;
	}
}
