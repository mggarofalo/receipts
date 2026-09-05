using API.Generated.Dtos;
using Common;
using Domain;
using Domain.Core;
using Riok.Mapperly.Abstractions;

namespace API.Mapping.Core;

[Mapper]
public partial class TransactionMapper
{
	[MapProperty(nameof(Transaction.Amount.Amount), nameof(TransactionResponse.Amount))]
	[MapperIgnoreTarget(nameof(TransactionResponse.AdditionalProperties))]
	public partial TransactionResponse ToResponse(Transaction source);

	public Transaction ToDomain(CreateTransactionRequest source)
	{
		return new Transaction(
			Guid.Empty,
			source.CardId,
			new Money((decimal)source.Amount, Currency.USD),
			source.Date
		);
	}

	public Transaction ToDomain(UpdateTransactionRequest source)
	{
		return new Transaction(
			source.Id,
			source.CardId,
			new Money((decimal)source.Amount, Currency.USD),
			source.Date
		);
	}

	private double MapDecimalToDouble(decimal value) => (double)value;
}
