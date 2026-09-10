using System.Transactions;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class YnabPushOperationConcurrencyTests(PostgresFixture fixture)
{
	[Fact]
	public async Task GetOrCreatePushOperation_ConcurrentDifferentPayloads_PersistsOneImmutableWinner()
	{
		Guid transactionId = await SeedTransactionAsync();
		YnabSyncRecordRepository firstRepository = Repository();
		YnabSyncRecordRepository secondRepository = Repository();
		YnabSyncRecordEntity first = Operation(transactionId, "import-original", "payload-original", "hash-original");
		YnabSyncRecordEntity second = Operation(transactionId, "import-edited", "payload-edited", "hash-edited");

		PreparedYnabPushOperation[] results = await Task.WhenAll(
			firstRepository.GetOrCreatePushOperationAsync(first, CancellationToken.None),
			secondRepository.GetOrCreatePushOperationAsync(second, CancellationToken.None));

		results.Should().ContainSingle(result => result.Created);
		results.Select(result => result.Record.Id).Distinct().Should().ContainSingle();
		results.Select(result => result.Record.ImportId).Distinct().Should().ContainSingle();
		results.Select(result => result.Record.RequestPayloadJson).Distinct().Should().ContainSingle();
		await using ApplicationDbContext read = fixture.CreateDbContext();
		YnabSyncRecordEntity stored = await read.YnabSyncRecords
			.AsNoTracking()
			.SingleAsync(row => row.LocalTransactionId == transactionId);
		stored.ImportId.Should().Be(results[0].Record.ImportId);
		stored.RequestPayloadJson.Should().Be(results[0].Record.RequestPayloadJson);
		stored.PayloadHash.Should().Be(results[0].Record.PayloadHash);
	}

	[Fact]
	public async Task TryClaimPushOperation_ConcurrentClaims_HasOneWinnerAndTokenGuardsCompletion()
	{
		Guid transactionId = await SeedTransactionAsync();
		YnabSyncRecordRepository repository = Repository();
		PreparedYnabPushOperation prepared = await repository.GetOrCreatePushOperationAsync(
			Operation(transactionId, "import-1", "payload-1", "hash-1"), CancellationToken.None);
		Guid firstToken = Guid.NewGuid();
		Guid secondToken = Guid.NewGuid();
		DateTimeOffset now = DateTimeOffset.UtcNow;

		YnabSyncRecordEntity?[] claims = await Task.WhenAll(
			Repository().TryClaimPushOperationAsync(prepared.Record.Id, firstToken, now, now.AddMinutes(-5), CancellationToken.None),
			Repository().TryClaimPushOperationAsync(prepared.Record.Id, secondToken, now, now.AddMinutes(-5), CancellationToken.None));

		claims.Count(claim => claim is not null).Should().Be(1);
		YnabSyncRecordEntity winner = claims.Single(claim => claim is not null)!;
		Guid winningToken = winner.ClaimToken!.Value;
		Guid losingToken = winningToken == firstToken ? secondToken : firstToken;
		(await repository.CompletePushOperationAsync(
			prepared.Record.Id, losingToken, YnabSyncStatus.Synced, "wrong", null,
			now.AddSeconds(1), CancellationToken.None)).Should().BeFalse();
		(await repository.CompletePushOperationAsync(
			prepared.Record.Id, winningToken, YnabSyncStatus.Unknown, null, "response lost",
			now.AddSeconds(2), CancellationToken.None)).Should().BeTrue();

		await using ApplicationDbContext read = fixture.CreateDbContext();
		YnabSyncRecordEntity stored = await read.YnabSyncRecords.AsNoTracking()
			.SingleAsync(row => row.Id == prepared.Record.Id);
		stored.SyncStatus.Should().Be(YnabSyncStatus.Unknown);
		stored.LastError.Should().Be("response lost");
		stored.ClaimToken.Should().BeNull();
		stored.AttemptCount.Should().Be(1);
	}

	[Fact]
	public async Task TryClaimPushOperation_AmbientTransactionRollback_LeavesOperationUnclaimed()
	{
		Guid transactionId = await SeedTransactionAsync();
		YnabSyncRecordRepository repository = Repository();
		PreparedYnabPushOperation prepared = await repository.GetOrCreatePushOperationAsync(
			Operation(transactionId, "import-rollback", "payload-rollback", "hash-rollback"),
			CancellationToken.None);

		using (TransactionScope transaction = new(TransactionScopeAsyncFlowOption.Enabled))
		{
			YnabSyncRecordEntity? claimed = await repository.TryClaimPushOperationAsync(
				prepared.Record.Id, Guid.NewGuid(), DateTimeOffset.UtcNow,
				DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);
			claimed.Should().NotBeNull();
			// Deliberately do not complete the ambient transaction.
		}

		await using ApplicationDbContext read = fixture.CreateDbContext();
		YnabSyncRecordEntity stored = await read.YnabSyncRecords.AsNoTracking()
			.SingleAsync(row => row.Id == prepared.Record.Id);
		stored.ClaimToken.Should().BeNull();
		stored.ClaimedAtUtc.Should().BeNull();
		stored.LastAttemptAtUtc.Should().BeNull();
		stored.AttemptCount.Should().Be(0);
		stored.SyncStatus.Should().Be(YnabSyncStatus.Pending);
	}

	[Fact]
	public async Task TryClaimPushOperation_ActiveLeaseRejectsRetry_ButExpiredLeaseCanResume()
	{
		Guid transactionId = await SeedTransactionAsync();
		YnabSyncRecordRepository repository = Repository();
		PreparedYnabPushOperation prepared = await repository.GetOrCreatePushOperationAsync(
			Operation(transactionId, "import-lease", "payload-lease", "hash-lease"),
			CancellationToken.None);
		DateTimeOffset firstAttempt = DateTimeOffset.UtcNow;
		Guid firstToken = Guid.NewGuid();
		(await repository.TryClaimPushOperationAsync(
			prepared.Record.Id, firstToken, firstAttempt, firstAttempt.AddMinutes(-2),
			CancellationToken.None)).Should().NotBeNull();

		YnabSyncRecordEntity? conflicting = await Repository().TryClaimPushOperationAsync(
			prepared.Record.Id, Guid.NewGuid(), firstAttempt.AddSeconds(30), firstAttempt.AddMinutes(-1),
			CancellationToken.None);
		Guid resumedToken = Guid.NewGuid();
		YnabSyncRecordEntity? resumed = await Repository().TryClaimPushOperationAsync(
			prepared.Record.Id, resumedToken, firstAttempt.AddMinutes(3), firstAttempt.AddMinutes(1),
			CancellationToken.None);

		conflicting.Should().BeNull();
		resumed.Should().NotBeNull();
		resumed!.ClaimToken.Should().Be(resumedToken);
		resumed.AttemptCount.Should().Be(2);
	}

	private YnabSyncRecordRepository Repository() => new(new ContextFactory(fixture.CreateOptions()));

	private async Task<Guid> SeedTransactionAsync()
	{
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.Id = account.Id;
		card.AccountId = account.Id;
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receipt.Id, account.Id);
		await using ApplicationDbContext context = fixture.CreateDbContext();
		context.Accounts.Add(account);
		context.Cards.Add(card);
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();
		context.Transactions.Add(transaction);
		await context.SaveChangesAsync();
		return transaction.Id;
	}

	private static YnabSyncRecordEntity Operation(
		Guid transactionId,
		string importId,
		string payload,
		string hash) => new()
		{
			Id = Guid.NewGuid(),
			LocalTransactionId = transactionId,
			YnabBudgetId = "budget-operation-tests",
			YnabAccountId = "account-1",
			ImportId = importId,
			RequestPayloadJson = payload,
			PayloadHash = hash,
			SourceVersion = "source-v1",
			SyncType = YnabSyncType.TransactionPush,
			SyncStatus = YnabSyncStatus.Pending,
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};

	private sealed class ContextFactory(DbContextOptions<ApplicationDbContext> options)
		: IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
	}
}
