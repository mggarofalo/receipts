namespace Infrastructure.Utilities;

public static class YnabConvert
{
	public static long ToMilliunits(decimal amount)
		=> (long)Math.Round(amount * 1000m, MidpointRounding.AwayFromZero);

	public static decimal FromMilliunits(long milliunits)
		=> milliunits / 1000m;
}
