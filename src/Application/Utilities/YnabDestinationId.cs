namespace Application.Utilities;

public static class YnabDestinationId
{
	public static string Canonicalize(string value)
	{
		string trimmed = value.Trim();
		return Guid.TryParse(trimmed, out Guid id) ? id.ToString("D") : trimmed;
	}
}
