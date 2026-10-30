using API.Generated.Dtos;
using Common;
using Domain;
using Domain.Core;
using Riok.Mapperly.Abstractions;

namespace API.Mapping.Core;

[Mapper]
public partial class ReceiptMapper
{
	[MapProperty(nameof(Receipt.TaxAmount.Amount), nameof(ReceiptResponse.TaxAmount))]
	[MapperIgnoreTarget(nameof(ReceiptResponse.AdditionalProperties))]
	public partial ReceiptResponse ToResponse(Receipt source);

	public Receipt ToDomain(CreateReceiptRequest source)
	{
		return new Receipt(
			Guid.Empty,
			source.Location,
			source.Date,
			new Money((decimal)source.TaxAmount, Currency.USD)
		)
		{ PaidFromEnvelopeId = source.PaidFromEnvelopeId, OccasionId = source.OccasionId };
	}

	public Receipt ToDomain(UpdateReceiptRequest source)
	{
		return new Receipt(
			source.Id,
			source.Location,
			source.Date,
			new Money((decimal)source.TaxAmount, Currency.USD)
		)
		{ PaidFromEnvelopeId = source.PaidFromEnvelopeId, OccasionId = source.OccasionId };
	}

	private double MapDecimalToDouble(decimal value) => (double)value;
}
