using Application.Commands.Receipt.CreateComplete;
using Application.Interfaces.Services;
using Application.Normalization;
using Domain.Core;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Services;

public class CompleteReceiptWriter(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	ReceiptMapper receiptMapper,
	TransactionMapper transactionMapper,
	ReceiptItemMapper receiptItemMapper,
	AdjustmentMapper adjustmentMapper,
		Microsoft.Extensions.Options.IOptions<Application.Taxonomy.ClassificationOptions>? classificationOptions = null) : ICompleteReceiptWriter
{
	public Task<CreateCompleteReceiptResult> CreateAsync(Receipt receipt, List<Transaction> transactions, List<ReceiptItem> items,
		List<Adjustment> adjustments, CancellationToken cancellationToken)
		=> CreateCoreAsync(receipt, transactions, items, adjustments, null, cancellationToken);

	public Task<CreateCompleteReceiptResult> CreateAsync(Receipt receipt, List<Transaction> transactions, List<ReceiptItem> items,
		List<Adjustment> adjustments, Guid clientRequestId, CancellationToken cancellationToken)
		=> CreateCoreAsync(receipt, transactions, items, adjustments, clientRequestId, cancellationToken);

	private async Task<CreateCompleteReceiptResult> CreateCoreAsync(
		Receipt receipt,
		List<Transaction> transactions,
		List<ReceiptItem> items,
		List<Adjustment> adjustments,
		Guid? clientRequestId, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using IDbContextTransaction? transaction = context.Database.IsRelational() ? await context.Database.BeginTransactionAsync(cancellationToken) : null;
		await NormalizationWriteGuard.AcquireWriteGateAsync(context, cancellationToken);
		ClassificationService.RequireRequestId(items.Select(receiptItemMapper.ToEntity), clientRequestId);
		ReceiptWriteRequestEntity? request = null;
		if (clientRequestId is Guid requestId)
		{
			request = await ReceiptWriteIdempotency.FindOrCreateAsync(context, requestId, "complete", new
			{
				receipt.Location,
				receipt.Date,
				receipt.TaxAmount,
				transactions = transactions.Select(value => new { value.CardId, value.Amount, value.Date }),
				items = items.Select(value => new { value.Description, value.ReceiptItemCode, value.Quantity, value.UnitPrice, value.TotalAmount, value.Category, value.Subcategory, value.NormalizationIntent, value.Classification }),
				adjustments = adjustments.Select(value => new { value.Type, value.Amount, value.Description })
			}, cancellationToken);
			if (request.ReceiptId != Guid.Empty)
			{
				ReceiptEntity? stored = await context.Receipts.IgnoreQueryFilters().SingleOrDefaultAsync(value => value.Id == request.ReceiptId, cancellationToken);
				if (stored is null || stored.DeletedAt is not null) { throw new NormalizationCatalogException(409, "receipt_request_deleted", "This request already created a receipt that is now deleted. Restore it instead of replaying creation."); }
				var savedItems = await context.ReceiptItems.Where(value => value.ReceiptId == stored.Id).ToListAsync(cancellationToken);
				var savedTransactions = await context.Transactions.Where(value => value.ReceiptId == stored.Id).ToListAsync(cancellationToken);
				var savedAdjustments = await context.Adjustments.Where(value => value.ReceiptId == stored.Id).ToListAsync(cancellationToken);
				return new(receiptMapper.ToDomain(stored), savedTransactions.Select(transactionMapper.ToDomain).ToList(), savedItems.Select(receiptItemMapper.ToDomain).ToList(), savedAdjustments.Select(adjustmentMapper.ToDomain).ToList());
			}
		}

		// Map receipt and pre-generate ID for FK assignment
		ReceiptEntity receiptEntity = receiptMapper.ToEntity(receipt);
		receiptEntity.Id = Guid.NewGuid();

		// Transactions store only the originating card; its parent determines the account.
		List<TransactionEntity> transactionEntities = transactions.Select(t =>
		{
			TransactionEntity entity = transactionMapper.ToEntity(t);
			entity.Id = Guid.NewGuid();
			entity.ReceiptId = receiptEntity.Id;
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

		if (request is not null)
		{
			request.ReceiptId = receiptEntity.Id;
			request.ItemIds = itemEntities.Select(item => item.Id).ToArray();
		}

		// Add all to context and persist in a single SaveChangesAsync (implicit transaction)
		context.Set<ReceiptEntity>().Add(receiptEntity);
		context.Set<TransactionEntity>().AddRange(transactionEntities);
		context.Set<ReceiptItemEntity>().AddRange(itemEntities);
		context.Set<AdjustmentEntity>().AddRange(adjustmentEntities);

		await ClassificationService.PrepareAggregateAsync(context, itemEntities, classificationOptions?.Value.WritesEnabled == true, cancellationToken);
		// Load required response state before committing, so a failed read cannot leave
		// a saved transaction behind a failed create response. Tracking fixes up the cards.
		List<Guid> cardIds = transactionEntities.Select(entity => entity.CardId).Distinct().ToList();
		await context.Cards.IgnoreAutoIncludes().Where(card => cardIds.Contains(card.Id)).LoadAsync(cancellationToken);
		await context.SaveChangesAsync(cancellationToken);
		await ClassificationService.ApplyAggregateAsync(context, itemEntities, classificationOptions?.Value.WritesEnabled == true, cancellationToken);
		if (transaction is not null) { await transaction.CommitAsync(cancellationToken); }


		// Map back to domain
		Receipt createdReceipt = receiptMapper.ToDomain(receiptEntity);
		List<Transaction> createdTransactions = transactionEntities.Select(transactionMapper.ToDomain).ToList();
		List<ReceiptItem> createdItems = itemEntities.Select(receiptItemMapper.ToDomain).ToList();
		List<Adjustment> createdAdjustments = adjustmentEntities.Select(adjustmentMapper.ToDomain).ToList();

		return new CreateCompleteReceiptResult(createdReceipt, createdTransactions, createdItems, createdAdjustments);
	}
}
