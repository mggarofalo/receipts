using Domain;
using Domain.Core;
using Infrastructure.Entities.Core;
using Riok.Mapperly.Abstractions;

namespace Infrastructure.Mapping;

[Mapper]
public partial class TransactionMapper
{
	[MapProperty(nameof(Transaction.Amount.Amount), nameof(TransactionEntity.Amount))]
	[MapProperty(nameof(Transaction.Amount.Currency), nameof(TransactionEntity.AmountCurrency))]
	[MapperIgnoreSource(nameof(Transaction.ReceiptId))]
	[MapperIgnoreTarget(nameof(TransactionEntity.Receipt))]
	[MapperIgnoreTarget(nameof(TransactionEntity.ReceiptId))]
	[MapperIgnoreSource(nameof(Transaction.AccountId))]
	[MapperIgnoreTarget(nameof(TransactionEntity.Card))]
	[MapperIgnoreTarget(nameof(TransactionEntity.DeletedAt))]
	[MapperIgnoreTarget(nameof(TransactionEntity.DeletedByUserId))]
	[MapperIgnoreTarget(nameof(TransactionEntity.DeletedByApiKeyId))]
	[MapperIgnoreTarget(nameof(TransactionEntity.CascadeDeletedByParentId))]
	public partial TransactionEntity ToEntity(Transaction source);

	// Card is the sole persisted account relationship. Read callers load or project it;
	// write mapping deliberately ignores the derived account value on domain responses.
	public Transaction ToDomain(TransactionEntity source) => new(
		source.Id, source.CardId, new Money(source.Amount, source.AmountCurrency), source.Date)
	{
		ReceiptId = source.ReceiptId,
		AccountId = source.Card?.AccountId
			?? throw new InvalidOperationException("Transaction reads must include the originating card."),
	};
}
