namespace Domain.Core;

public class Receipt
{
	public Guid Id { get; set; }
	public string Location { get; set; }
	public DateOnly Date { get; set; }
	public Money TaxAmount { get; set; }

	public const string LocationCannotBeEmpty = "Location cannot be empty";

	public Receipt(Guid id, string location, DateOnly date, Money taxAmount)
	{
		if (string.IsNullOrWhiteSpace(location))
		{
			throw new ArgumentException(LocationCannotBeEmpty, nameof(location));
		}

		Id = id;
		Location = location;
		Date = date;
		TaxAmount = taxAmount;
	}
}
