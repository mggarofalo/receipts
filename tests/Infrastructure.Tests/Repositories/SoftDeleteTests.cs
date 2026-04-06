using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Extensions;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.Tests.Repositories;

public class SoftDeleteTests
{
	[Fact]
	public async Task SoftDelete_ItemTemplate_SetsDeletedAtOnDelete()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		ItemTemplateEntity entity = ItemTemplateEntityGenerator.Generate();

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.ItemTemplates.AddAsync(entity);
			await context.SaveChangesAsync();
		}

		// Act
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ItemTemplateEntity template = await context.ItemTemplates.FirstAsync();
			context.ItemTemplates.Remove(template);
			await context.SaveChangesAsync();
		}

		// Assert
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<ItemTemplateEntity> allTemplates = await context.ItemTemplates.IgnoreQueryFilters().ToListAsync();
			allTemplates.Should().HaveCount(1);
			allTemplates[0].DeletedAt.Should().NotBeNull();
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task SoftDelete_ItemTemplate_ExcludedFromNormalQueries()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		ItemTemplateEntity entity = ItemTemplateEntityGenerator.Generate();

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.ItemTemplates.AddAsync(entity);
			await context.SaveChangesAsync();
		}

		// Act
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ItemTemplateEntity template = await context.ItemTemplates.FirstAsync();
			context.ItemTemplates.Remove(template);
			await context.SaveChangesAsync();
		}

		// Assert
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<ItemTemplateEntity> visibleTemplates = await context.ItemTemplates.ToListAsync();
			visibleTemplates.Should().BeEmpty();
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task SoftDelete_Receipt_CascadesToReceiptItems()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			await context.Receipts.AddAsync(receipt);
			ReceiptItemEntity item1 = ReceiptItemEntityGenerator.Generate(receipt.Id);
			ReceiptItemEntity item2 = ReceiptItemEntityGenerator.Generate(receipt.Id);
			await context.ReceiptItems.AddRangeAsync(item1, item2);
			await context.SaveChangesAsync();
		}

		// Act - delete the receipt (need to load items into context for cascade)
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ReceiptEntity receipt = await context.Receipts.FirstAsync();
			// Load related items into tracker
			await context.ReceiptItems.Where(i => i.ReceiptId == receipt.Id).LoadAsync();
			context.Receipts.Remove(receipt);
			await context.SaveChangesAsync();
		}

		// Assert
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<ReceiptItemEntity> allItems = await context.ReceiptItems.IgnoreQueryFilters().ToListAsync();
			allItems.Should().HaveCount(2);
			allItems.Should().AllSatisfy(i => i.DeletedAt.Should().NotBeNull());
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task SoftDelete_Receipt_CascadesToTransactions()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		AccountEntity account = AccountEntityGenerator.Generate();

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.Accounts.AddAsync(account);
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			await context.Receipts.AddAsync(receipt);
			TransactionEntity transaction1 = TransactionEntityGenerator.Generate(receiptId: receipt.Id, accountId: account.Id);
			TransactionEntity transaction2 = TransactionEntityGenerator.Generate(receiptId: receipt.Id, accountId: account.Id);
			await context.Transactions.AddRangeAsync(transaction1, transaction2);
			await context.SaveChangesAsync();
		}

		// Act - delete the receipt (need to load transactions into context for cascade)
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ReceiptEntity receipt = await context.Receipts.FirstAsync();
			// Load related transactions into tracker
			await context.Transactions.Where(t => t.ReceiptId == receipt.Id).LoadAsync();
			context.Receipts.Remove(receipt);
			await context.SaveChangesAsync();
		}

		// Assert
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<TransactionEntity> allTransactions = await context.Transactions.IgnoreQueryFilters().ToListAsync();
			allTransactions.Should().HaveCount(2);
			allTransactions.Should().AllSatisfy(t => t.DeletedAt.Should().NotBeNull());
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task SoftDelete_SetsDeletedByUserId()
	{
		// Arrange
		(IDbContextFactory<ApplicationDbContext> contextFactory, MockCurrentUserAccessor accessor) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		accessor.UserId = "test-user-id";

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ItemTemplateEntity entity = ItemTemplateEntityGenerator.Generate();
			await context.ItemTemplates.AddAsync(entity);
			await context.SaveChangesAsync();
		}

		// Act
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ItemTemplateEntity template = await context.ItemTemplates.FirstAsync();
			context.ItemTemplates.Remove(template);
			await context.SaveChangesAsync();
		}

		// Assert
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<ItemTemplateEntity> allTemplates = await context.ItemTemplates.IgnoreQueryFilters().ToListAsync();
			allTemplates.Should().HaveCount(1);
			allTemplates[0].DeletedByUserId.Should().Be("test-user-id");
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task SoftDelete_SetsDeletedByApiKeyId()
	{
		// Arrange
		(IDbContextFactory<ApplicationDbContext> contextFactory, MockCurrentUserAccessor accessor) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		Guid apiKeyId = Guid.NewGuid();
		accessor.ApiKeyId = apiKeyId;

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ItemTemplateEntity entity = ItemTemplateEntityGenerator.Generate();
			await context.ItemTemplates.AddAsync(entity);
			await context.SaveChangesAsync();
		}

		// Act
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ItemTemplateEntity template = await context.ItemTemplates.FirstAsync();
			context.ItemTemplates.Remove(template);
			await context.SaveChangesAsync();
		}

		// Assert
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<ItemTemplateEntity> allTemplates = await context.ItemTemplates.IgnoreQueryFilters().ToListAsync();
			allTemplates.Should().HaveCount(1);
			allTemplates[0].DeletedByApiKeyId.Should().Be(apiKeyId);
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task OnlyDeleted_ReturnsOnlySoftDeletedEntities()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		List<ItemTemplateEntity> entities = ItemTemplateEntityGenerator.GenerateList(3);

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.ItemTemplates.AddRangeAsync(entities);
			await context.SaveChangesAsync();
		}

		// Delete only the first entity
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ItemTemplateEntity templateToDelete = await context.ItemTemplates.FirstAsync(t => t.Id == entities[0].Id);
			context.ItemTemplates.Remove(templateToDelete);
			await context.SaveChangesAsync();
		}

		// Act
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<ItemTemplateEntity> onlyDeleted = await context.ItemTemplates
				.IgnoreQueryFilters()
				.Where(e => e.DeletedAt != null)
				.ToListAsync();

			// Assert
			onlyDeleted.Should().HaveCount(1);
			onlyDeleted[0].Id.Should().Be(entities[0].Id);
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task IncludeDeleted_ReturnsBothActiveAndDeleted()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		List<ItemTemplateEntity> entities = ItemTemplateEntityGenerator.GenerateList(3);

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.ItemTemplates.AddRangeAsync(entities);
			await context.SaveChangesAsync();
		}

		// Delete one entity
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ItemTemplateEntity templateToDelete = await context.ItemTemplates.FirstAsync(t => t.Id == entities[0].Id);
			context.ItemTemplates.Remove(templateToDelete);
			await context.SaveChangesAsync();
		}

		// Act
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<ItemTemplateEntity> allIncludingDeleted = await context.ItemTemplates
				.IgnoreQueryFilters()
				.ToListAsync();

			// Assert
			allIncludingDeleted.Should().HaveCount(3);
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task CascadeDelete_Receipt_SetsCascadeDeletedByParentIdOnReceiptItems()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.Receipts.AddAsync(receipt);
			ReceiptItemEntity item1 = ReceiptItemEntityGenerator.Generate(receipt.Id);
			ReceiptItemEntity item2 = ReceiptItemEntityGenerator.Generate(receipt.Id);
			await context.ReceiptItems.AddRangeAsync(item1, item2);
			await context.SaveChangesAsync();
		}

		// Act - delete the receipt with children loaded
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ReceiptEntity receiptToDelete = await context.Receipts.FirstAsync();
			await context.ReceiptItems.Where(i => i.ReceiptId == receiptToDelete.Id).LoadAsync();
			context.Receipts.Remove(receiptToDelete);
			await context.SaveChangesAsync();
		}

		// Assert
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<ReceiptItemEntity> allItems = await context.ReceiptItems.IgnoreQueryFilters().ToListAsync();
			allItems.Should().HaveCount(2);
			allItems.Should().AllSatisfy(i =>
			{
				i.DeletedAt.Should().NotBeNull();
				i.CascadeDeletedByParentId.Should().Be(receipt.Id);
			});
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task CascadeDelete_Receipt_SetsCascadeDeletedByParentIdOnTransactions()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		AccountEntity account = AccountEntityGenerator.Generate();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.Accounts.AddAsync(account);
			await context.Receipts.AddAsync(receipt);
			TransactionEntity transaction = TransactionEntityGenerator.Generate(receiptId: receipt.Id, accountId: account.Id);
			await context.Transactions.AddAsync(transaction);
			await context.SaveChangesAsync();
		}

		// Act
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ReceiptEntity receiptToDelete = await context.Receipts.FirstAsync();
			await context.Transactions.Where(t => t.ReceiptId == receiptToDelete.Id).LoadAsync();
			context.Receipts.Remove(receiptToDelete);
			await context.SaveChangesAsync();
		}

		// Assert
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<TransactionEntity> allTransactions = await context.Transactions.IgnoreQueryFilters().ToListAsync();
			allTransactions.Should().HaveCount(1);
			allTransactions[0].DeletedAt.Should().NotBeNull();
			allTransactions[0].CascadeDeletedByParentId.Should().Be(receipt.Id);
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task CascadeDelete_DoesNotSetCascadeFieldOnAlreadyDeletedChildren()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity item1 = ReceiptItemEntityGenerator.Generate(receipt.Id);
		ReceiptItemEntity item2 = ReceiptItemEntityGenerator.Generate(receipt.Id);

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.Receipts.AddAsync(receipt);
			await context.ReceiptItems.AddRangeAsync(item1, item2);
			await context.SaveChangesAsync();
		}

		// Independently delete item1 first
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ReceiptItemEntity itemToDelete = await context.ReceiptItems.FirstAsync(i => i.Id == item1.Id);
			context.ReceiptItems.Remove(itemToDelete);
			await context.SaveChangesAsync();
		}

		// Act - now delete the receipt (cascade should only affect item2)
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ReceiptEntity receiptToDelete = await context.Receipts.FirstAsync();
			await context.ReceiptItems.IgnoreQueryFilters().Where(i => i.ReceiptId == receiptToDelete.Id).LoadAsync();
			context.Receipts.Remove(receiptToDelete);
			await context.SaveChangesAsync();
		}

		// Assert
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<ReceiptItemEntity> allItems = await context.ReceiptItems.IgnoreQueryFilters().ToListAsync();
			allItems.Should().HaveCount(2);

			ReceiptItemEntity independentlyDeleted = allItems.First(i => i.Id == item1.Id);
			independentlyDeleted.DeletedAt.Should().NotBeNull();
			independentlyDeleted.CascadeDeletedByParentId.Should().BeNull();

			ReceiptItemEntity cascadeDeleted = allItems.First(i => i.Id == item2.Id);
			cascadeDeleted.DeletedAt.Should().NotBeNull();
			cascadeDeleted.CascadeDeletedByParentId.Should().Be(receipt.Id);
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task Restore_Receipt_OnlyRestoresCascadeDeletedChildren()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity item1 = ReceiptItemEntityGenerator.Generate(receipt.Id);
		ReceiptItemEntity item2 = ReceiptItemEntityGenerator.Generate(receipt.Id);

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.Receipts.AddAsync(receipt);
			await context.ReceiptItems.AddRangeAsync(item1, item2);
			await context.SaveChangesAsync();
		}

		// Independently delete item1 first
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ReceiptItemEntity itemToDelete = await context.ReceiptItems.FirstAsync(i => i.Id == item1.Id);
			context.ReceiptItems.Remove(itemToDelete);
			await context.SaveChangesAsync();
		}

		// Delete the receipt (cascade should only affect item2)
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ReceiptEntity receiptToDelete = await context.Receipts.FirstAsync();
			await context.ReceiptItems.IgnoreQueryFilters().Where(i => i.ReceiptId == receiptToDelete.Id).LoadAsync();
			context.Receipts.Remove(receiptToDelete);
			await context.SaveChangesAsync();
		}

		// Act - restore the receipt
		Infrastructure.Repositories.ReceiptRepository repository = new(contextFactory);
		bool restored = await repository.RestoreAsync(receipt.Id, CancellationToken.None);

		// Assert
		restored.Should().BeTrue();

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// Receipt should be restored
			ReceiptEntity? restoredReceipt = await context.Receipts.FirstOrDefaultAsync(r => r.Id == receipt.Id);
			restoredReceipt.Should().NotBeNull();
			restoredReceipt!.DeletedAt.Should().BeNull();

			// item2 should be restored (was cascade-deleted)
			ReceiptItemEntity? restoredItem = await context.ReceiptItems.FirstOrDefaultAsync(i => i.Id == item2.Id);
			restoredItem.Should().NotBeNull();
			restoredItem!.DeletedAt.Should().BeNull();
			restoredItem.CascadeDeletedByParentId.Should().BeNull();

			// item1 should still be deleted (was independently deleted)
			ReceiptItemEntity? stillDeletedItem = await context.ReceiptItems.IgnoreQueryFilters()
				.FirstOrDefaultAsync(i => i.Id == item1.Id);
			stillDeletedItem.Should().NotBeNull();
			stillDeletedItem!.DeletedAt.Should().NotBeNull();
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task CascadeDelete_Receipt_GeneratesAuditLogsForCascadedItems()
	{
		// Arrange
		(IDbContextFactory<ApplicationDbContext> contextFactory, MockCurrentUserAccessor accessor) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		accessor.UserId = "test-user-id";

		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.Receipts.AddAsync(receipt);
			await context.ReceiptItems.AddAsync(item);
			await context.SaveChangesAsync();
		}

		// Act
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ReceiptEntity receiptToDelete = await context.Receipts.FirstAsync();
			await context.ReceiptItems.Where(i => i.ReceiptId == receiptToDelete.Id).LoadAsync();
			context.Receipts.Remove(receiptToDelete);
			await context.SaveChangesAsync();
		}

		// Assert - audit logs should exist for both the receipt and the cascaded item
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<Infrastructure.Entities.Audit.AuditLogEntity> auditLogs = await context.AuditLogs.ToListAsync();

			auditLogs.Should().Contain(log =>
				log.EntityType == "Receipt" &&
				log.EntityId == receipt.Id.ToString() &&
				log.Action == Infrastructure.Entities.Audit.AuditAction.Delete);

			auditLogs.Should().Contain(log =>
				log.EntityType == "ReceiptItem" &&
				log.EntityId == item.Id.ToString() &&
				log.Action == Infrastructure.Entities.Audit.AuditAction.Delete);
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task SoftDelete_YnabSyncRecord_ExcludedFromNormalQueriesWhenSoftDeleted()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		AccountEntity account = AccountEntityGenerator.Generate();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receiptId: receipt.Id, accountId: account.Id);

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.Accounts.AddAsync(account);
			await context.Receipts.AddAsync(receipt);
			await context.Transactions.AddAsync(transaction);
			YnabSyncRecordEntity syncRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transaction.Id);
			await context.YnabSyncRecords.AddAsync(syncRecord);
			await context.SaveChangesAsync();
		}

		// Act
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			YnabSyncRecordEntity record = await context.YnabSyncRecords.FirstAsync();
			context.YnabSyncRecords.Remove(record);
			await context.SaveChangesAsync();
		}

		// Assert
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<YnabSyncRecordEntity> visibleRecords = await context.YnabSyncRecords.ToListAsync();
			visibleRecords.Should().BeEmpty();

			List<YnabSyncRecordEntity> allRecords = await context.YnabSyncRecords.IgnoreQueryFilters().ToListAsync();
			allRecords.Should().HaveCount(1);
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task SoftDelete_YnabSyncRecord_AllowsReCreationAfterSoftDelete()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		AccountEntity account = AccountEntityGenerator.Generate();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receiptId: receipt.Id, accountId: account.Id);

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.Accounts.AddAsync(account);
			await context.Receipts.AddAsync(receipt);
			await context.Transactions.AddAsync(transaction);
			YnabSyncRecordEntity syncRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transaction.Id);
			await context.YnabSyncRecords.AddAsync(syncRecord);
			await context.SaveChangesAsync();
		}

		// Act - soft-delete the sync record
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			YnabSyncRecordEntity record = await context.YnabSyncRecords.FirstAsync();
			context.YnabSyncRecords.Remove(record);
			await context.SaveChangesAsync();
		}

		// Act - create a new sync record with the same (LocalTransactionId, SyncType) pair
		YnabSyncRecordEntity newRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transaction.Id);
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.YnabSyncRecords.AddAsync(newRecord);
			await context.SaveChangesAsync();
		}

		// Assert - both records should exist (one soft-deleted, one active)
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<YnabSyncRecordEntity> allRecords = await context.YnabSyncRecords.IgnoreQueryFilters().ToListAsync();
			allRecords.Should().HaveCount(2);

			List<YnabSyncRecordEntity> activeRecords = await context.YnabSyncRecords.ToListAsync();
			activeRecords.Should().HaveCount(1);
			activeRecords[0].Id.Should().Be(newRecord.Id);
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task SoftDelete_YnabSyncRecord_SetsDeletedAtOnDelete()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		AccountEntity account = AccountEntityGenerator.Generate();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receiptId: receipt.Id, accountId: account.Id);

		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			await context.Accounts.AddAsync(account);
			await context.Receipts.AddAsync(receipt);
			await context.Transactions.AddAsync(transaction);
			YnabSyncRecordEntity syncRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transaction.Id);
			await context.YnabSyncRecords.AddAsync(syncRecord);
			await context.SaveChangesAsync();
		}

		// Act
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			YnabSyncRecordEntity record = await context.YnabSyncRecords.FirstAsync();
			context.YnabSyncRecords.Remove(record);
			await context.SaveChangesAsync();
		}

		// Assert
		using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<YnabSyncRecordEntity> allRecords = await context.YnabSyncRecords.IgnoreQueryFilters().ToListAsync();
			allRecords.Should().HaveCount(1);
			allRecords[0].DeletedAt.Should().NotBeNull();
		}

		contextFactory.ResetDatabase();
	}
}
