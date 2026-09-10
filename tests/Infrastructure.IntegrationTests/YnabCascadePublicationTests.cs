using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class YnabCascadePublicationTests(PostgresFixture fixture)
{
	[Fact]
	public async Task ReceiptDelete_PublishesOnlyAfterCascadeIsDurableToAnotherConnection()
	{
		(ReceiptEntity receipt, TransactionEntity transaction, YnabSyncRecordEntity syncRecord) = await SeedSyncedReceiptAsync();
		Mock<ICommittedChangePublisher> publisher = new(MockBehavior.Strict);
		publisher.Setup(x => x.PublishAsync(It.Is<CommittedEntityChange>(change =>
			change.EntityType == CommittedEntityType.YnabSyncRecord
			&& change.ChangeType == CommittedChangeType.Deleted
			&& change.EntityId == null
			&& change.SuppressToast)))
			.Returns(async () =>
			{
				await using ApplicationDbContext observer = fixture.CreateDbContext();
				(await observer.Receipts.AnyAsync(x => x.Id == receipt.Id)).Should().BeFalse();
				(await observer.Transactions.AnyAsync(x => x.Id == transaction.Id)).Should().BeFalse();
				(await observer.YnabSyncRecords.AnyAsync(x => x.Id == syncRecord.Id)).Should().BeFalse();
			});
		ReceiptService service = new(
			new ReceiptRepository(new ContextFactory(fixture.CreateOptions())),
			new ReceiptMapper(), publisher.Object);

		await service.DeleteAsync([receipt.Id], CancellationToken.None);

		publisher.VerifyAll();
	}

	[Fact]
	public async Task ReceiptDelete_SaveFailureRollsBackCascadeAndDoesNotPublish()
	{
		(ReceiptEntity receipt, TransactionEntity transaction, YnabSyncRecordEntity syncRecord) = await SeedSyncedReceiptAsync();
		DbContextOptions<ApplicationDbContext> failingOptions =
			new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
				.AddInterceptors(new ThrowingSaveChangesInterceptor())
				.Options;
		Mock<ICommittedChangePublisher> publisher = new(MockBehavior.Strict);
		ReceiptService service = new(
			new ReceiptRepository(new ContextFactory(failingOptions)),
			new ReceiptMapper(), publisher.Object);

		Func<Task> act = async () => await service.DeleteAsync([receipt.Id], CancellationToken.None);

		await act.Should().ThrowAsync<IOException>().WithMessage("forced rollback");
		publisher.VerifyNoOtherCalls();
		await using ApplicationDbContext observer = fixture.CreateDbContext();
		(await observer.Receipts.AnyAsync(x => x.Id == receipt.Id)).Should().BeTrue();
		(await observer.Transactions.AnyAsync(x => x.Id == transaction.Id)).Should().BeTrue();
		(await observer.YnabSyncRecords.AnyAsync(x => x.Id == syncRecord.Id)).Should().BeTrue();
	}

	private async Task<(ReceiptEntity Receipt, TransactionEntity Transaction, YnabSyncRecordEntity SyncRecord)>
		SeedSyncedReceiptAsync()
	{
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.Id = account.Id;
		card.AccountId = account.Id;
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id);
		YnabSyncRecordEntity syncRecord = YnabSyncRecordEntityGenerator.Generate(localTransactionId: transaction.Id);
		await using ApplicationDbContext context = fixture.CreateDbContext();
		context.Accounts.Add(account);
		context.Cards.Add(card);
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();
		context.Transactions.Add(transaction);
		await context.SaveChangesAsync();
		context.YnabSyncRecords.Add(syncRecord);
		await context.SaveChangesAsync();
		return (receipt, transaction, syncRecord);
	}

	private sealed class ContextFactory(DbContextOptions<ApplicationDbContext> options)
		: IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
	}

	private sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
	{
		public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
			DbContextEventData eventData,
			InterceptionResult<int> result,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<InterceptionResult<int>>(new IOException("forced rollback"));
	}
}
