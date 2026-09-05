using Common;
using Infrastructure.Entities.Core;

namespace SampleData.Entities;

public static class TransactionEntityGenerator
{
	public static TransactionEntity Generate(Guid? receiptId = null, Guid? accountId = null, Guid? cardId = null)
	{
		Guid resolvedAccountId = accountId ?? Guid.NewGuid();
		return new TransactionEntity
		{
			Id = Guid.NewGuid(),
			ReceiptId = receiptId ?? Guid.NewGuid(),
			CardId = cardId ?? resolvedAccountId,
			Amount = 100m,
			AmountCurrency = Currency.USD,
			Date = DateOnly.FromDateTime(DateTime.Now)
		};
	}

	public static List<TransactionEntity> GenerateList(int count, Guid? receiptId = null, Guid? accountId = null, Guid? cardId = null)
	{
		return [.. Enumerable.Range(0, count).Select(_ => Generate(receiptId, accountId, cardId))];
	}
}
