namespace Application.Utilities;

public static class YnabImportId
{
	private const int MaxLength = 36;

	public static string Generate(long milliunits, DateOnly date, int occurrence)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(occurrence, 1);

		string importId = $"YNAB:{milliunits}:{date:yyyy-MM-dd}:{occurrence}";

		if (importId.Length > MaxLength)
		{
			throw new InvalidOperationException(
				$"Generated import_id '{importId}' exceeds YNAB's {MaxLength}-character limit (length: {importId.Length}).");
		}

		return importId;
	}
}
