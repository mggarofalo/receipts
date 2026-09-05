namespace Domain.Core;

public class Transaction
{
	public Guid Id { get; set; }
	public Guid ReceiptId { get; set; }
	/// <summary>Derived from the originating card on reads; ignored by persistence writes.</summary>
	public Guid AccountId { get; set; }
	public Guid CardId { get; set; }
	public Money Amount { get; set; }
	public DateOnly Date { get; set; }

	public const string AmountMustBeNonZero = "Amount must be non-zero";
	public const string DateCannotBeInTheFuture = "Date cannot be in the future";
	public const string CardIdCannotBeEmpty = "Card ID cannot be empty";

	public Transaction(Guid id, Guid cardId, Money amount, DateOnly date)
	{
		if (amount.Amount == 0)
		{
			throw new ArgumentException(AmountMustBeNonZero, nameof(amount));
		}

		if (date.ToDateTime(TimeOnly.MinValue) > DateTime.Today)
		{
			throw new ArgumentException(DateCannotBeInTheFuture, nameof(date));
		}

		if (cardId == Guid.Empty)
		{
			throw new ArgumentException(CardIdCannotBeEmpty, nameof(cardId));
		}

		Id = id;
		CardId = cardId;
		Amount = amount;
		Date = date;
	}
}
