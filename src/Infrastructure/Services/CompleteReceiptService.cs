using Application.Commands.Receipt.CreateComplete;
using Application.Interfaces.Services;
using Domain.Core;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class CompleteReceiptService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	ReceiptMapper receiptMapper,
	TransactionMapper transactionMapper,
	ReceiptItemMapper receiptItemMapper,
	AdjustmentMapper adjustmentMapper) : ICompleteReceiptService
{
	public async Task<CreateCompleteReceiptResult> CreateAsync(
		Receipt receipt,
		List<Transaction> transactions,
		List<ReceiptItem> items,
		List<Adjustment> adjustments,
		CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		// Map receipt and pre-generate ID for FK assignment
		ReceiptEntity receiptEntity = receiptMapper.ToEntity(receipt);
		receiptEntity.Id = Guid.NewGuid();

		// Map transactions, assigning FK, AccountId, and CardId.
		// Both AccountId and CardId are carried through until AccountId is dropped in a follow-up (RECEIPTS-553).
		List<TransactionEntity> transactionEntities = transactions.Select(t =>
		{
			TransactionEntity entity = transactionMapper.ToEntity(t);
			entity.Id = Guid.NewGuid();
			entity.ReceiptId = receiptEntity.Id;
			entity.AccountId = t.AccountId;
			entity.CardId = t.CardId;
			return entity;
		}).ToList();

		// Map receipt items, assigning FK
		List<ReceiptItemEntity> itemEntities = items.Select(i =>
		{
			ReceiptItemEntity entity = receiptItemMapper.ToEntity(i);
			entity.Id = Guid.NewGuid();
			entity.ReceiptId = receiptEntity.Id;
			return entity;
		}).ToList();

		List<AdjustmentEntity> adjustmentEntities = adjustments.Select(adjustment =>
		{
			AdjustmentEntity entity = adjustmentMapper.ToEntity(adjustment);
			entity.Id = Guid.NewGuid();
			entity.ReceiptId = receiptEntity.Id;
			return entity;
		}).ToList();

		// Add all to context and persist in a single SaveChangesAsync (implicit transaction)
		context.Set<ReceiptEntity>().Add(receiptEntity);
		context.Set<TransactionEntity>().AddRange(transactionEntities);
		context.Set<ReceiptItemEntity>().AddRange(itemEntities);
		context.Set<AdjustmentEntity>().AddRange(adjustmentEntities);

		await context.SaveChangesAsync(cancellationToken);

		// Map back to domain
		Receipt createdReceipt = receiptMapper.ToDomain(receiptEntity);
		List<Transaction> createdTransactions = transactionEntities.Select(transactionMapper.ToDomain).ToList();
		List<ReceiptItem> createdItems = itemEntities.Select(receiptItemMapper.ToDomain).ToList();
		List<Adjustment> createdAdjustments = adjustmentEntities.Select(adjustmentMapper.ToDomain).ToList();

		return new CreateCompleteReceiptResult(createdReceipt, createdTransactions, createdItems, createdAdjustments);
	}
}
