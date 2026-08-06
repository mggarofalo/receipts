using Application.Models.Merge;
using FluentAssertions;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SampleData.Entities;

namespace Infrastructure.Tests.Services;

public class AccountMergeServiceTests : IDisposable
{
	private readonly string _dbName;
	private readonly DbContextOptions<ApplicationDbContext> _options;
	private readonly MockCurrentUserAccessor _userAccessor;
	private readonly AccountMergeService _service;

	public AccountMergeServiceTests()
	{
		_dbName = Guid.NewGuid().ToString();
		_options = new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase(databaseName: _dbName)
			.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
			.Options;

		_userAccessor = new MockCurrentUserAccessor { UserId = "test-user" };

		TestFactory factory = new(_options, _userAccessor);
		_service = new AccountMergeService(factory, _userAccessor);
	}

	public void Dispose()
	{
		using ApplicationDbContext context = new(_options, _userAccessor);
		context.Database.EnsureDeleted();
		GC.SuppressFinalize(this);
	}

	private ApplicationDbContext CreateContext() => new(_options, _userAccessor);

	[Fact]
	public async Task MergeCardsAsync_WithNoCards_Throws()
	{
		AccountEntity target = AccountEntityGenerator.Generate();
		using (ApplicationDbContext seed = CreateContext())
		{
			seed.Accounts.Add(target);
			await seed.SaveChangesAsync();
		}

		Func<Task> act = () => _service.MergeCardsAsync(target.Id, [], null, CancellationToken.None);

		await act.Should().ThrowAsync<ArgumentException>()
			.WithMessage($"{AccountMergeService.AtLeastOneCardRequired}*");
	}

	[Fact]
	public async Task MergeCardsAsync_WithASingleCardFromAnotherAccount_MergesIt()
	{
		// RECEIPTS-887: this is the commonest merge there is — account B holds one card,
		// fold it into account A — and the old two-card floor made it impossible. The user
		// had to also tick unrelated cards already sitting on the target to satisfy a
		// count that never described the real requirement.
		AccountEntity target = AccountEntityGenerator.Generate();
		AccountEntity source = AccountEntityGenerator.Generate();

		CardEntity loneCard = CardEntityGenerator.Generate();
		loneCard.AccountId = source.Id;
		TransactionEntity tx = TransactionEntityGenerator.Generate(accountId: source.Id);

		using (ApplicationDbContext seed = CreateContext())
		{
			seed.Accounts.AddRange(target, source);
			seed.Cards.Add(loneCard);
			seed.Transactions.Add(tx);
			await seed.SaveChangesAsync();
		}

		MergeCardsResult result = await _service.MergeCardsAsync(
			target.Id,
			[loneCard.Id],
			null,
			CancellationToken.None);

		result.Conflicts.Should().BeNull();
		(result.AccountsRemoved, result.CardsMoved, result.TransactionsRepointed)
			.Should().Be((1, 1, 1));

		using ApplicationDbContext assert = CreateContext();
		(await assert.Cards.AsNoTracking().SingleAsync(c => c.Id == loneCard.Id))
			.AccountId.Should().Be(target.Id);
		(await assert.Accounts.AsNoTracking().ToListAsync())
			.Select(a => a.Id).Should().BeEquivalentTo([target.Id]);
	}

	[Fact]
	public async Task MergeCardsAsync_WithASingleCardAlreadyOnTheTarget_IsStillAReportedNoOp()
	{
		// Dropping the count floor must not turn a one-card no-op into an exception:
		// RECEIPTS-893 settled that a merge which changes nothing is idempotent and
		// correct, reported with zeroed counts rather than rejected.
		AccountEntity target = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = target.Id;

		using (ApplicationDbContext seed = CreateContext())
		{
			seed.Accounts.Add(target);
			seed.Cards.Add(card);
			await seed.SaveChangesAsync();
		}

		MergeCardsResult result = await _service.MergeCardsAsync(
			target.Id,
			[card.Id],
			null,
			CancellationToken.None);

		result.IsNoOp.Should().BeTrue();
	}

	[Fact]
	public async Task MergeCardsAsync_WithTargetNotFound_Throws()
	{
		Func<Task> act = () => _service.MergeCardsAsync(
			Guid.NewGuid(),
			[Guid.NewGuid(), Guid.NewGuid()],
			null,
			CancellationToken.None);

		await act.Should().ThrowAsync<KeyNotFoundException>()
			.WithMessage(AccountMergeService.TargetAccountNotFound);
	}

	[Fact]
	public async Task MergeCardsAsync_WithMissingSourceCard_Throws()
	{
		AccountEntity target = AccountEntityGenerator.Generate();
		using (ApplicationDbContext seed = CreateContext())
		{
			seed.Accounts.Add(target);
			await seed.SaveChangesAsync();
		}

		Func<Task> act = () => _service.MergeCardsAsync(
			target.Id,
			[Guid.NewGuid(), Guid.NewGuid()],
			null,
			CancellationToken.None);

		await act.Should().ThrowAsync<KeyNotFoundException>()
			.WithMessage(AccountMergeService.SourceCardNotFound);
	}

	[Fact]
	public async Task MergeCardsAsync_WithAllCardsAlreadyOnTarget_ReportsANoOpRatherThanAMerge()
	{
		// RECEIPTS-893: this is the branch that used to return the same value as a real
		// merge. The counts are what makes it distinguishable, so they are the assertion.
		AccountEntity target = AccountEntityGenerator.Generate();
		CardEntity card1 = CardEntityGenerator.Generate();
		CardEntity card2 = CardEntityGenerator.Generate();
		card1.AccountId = target.Id;
		card2.AccountId = target.Id;
		using (ApplicationDbContext seed = CreateContext())
		{
			seed.Accounts.Add(target);
			seed.Cards.AddRange(card1, card2);
			await seed.SaveChangesAsync();
		}

		MergeCardsResult result = await _service.MergeCardsAsync(
			target.Id,
			[card1.Id, card2.Id],
			null,
			CancellationToken.None);

		result.Conflicts.Should().BeNull();
		result.IsNoOp.Should().BeTrue();
		(result.AccountsRemoved, result.CardsMoved, result.TransactionsRepointed)
			.Should().Be((0, 0, 0));

		using ApplicationDbContext assert = CreateContext();
		(await assert.AuditLogs.Where(a => a.Action == AuditAction.Merge).ToListAsync())
			.Should().BeEmpty();
	}

	[Fact]
	public async Task MergeCardsAsync_HappyPath_RepointsCardsAndTransactionsAndDeletesOrphans()
	{
		AccountEntity target = AccountEntityGenerator.Generate();
		AccountEntity source1 = AccountEntityGenerator.Generate();
		AccountEntity source2 = AccountEntityGenerator.Generate();

		CardEntity cardOnSource1 = CardEntityGenerator.Generate();
		cardOnSource1.AccountId = source1.Id;
		CardEntity cardOnSource2 = CardEntityGenerator.Generate();
		cardOnSource2.AccountId = source2.Id;

		TransactionEntity txOnSource1 = TransactionEntityGenerator.Generate(accountId: source1.Id);
		TransactionEntity txOnSource2 = TransactionEntityGenerator.Generate(accountId: source2.Id);
		TransactionEntity txOnTarget = TransactionEntityGenerator.Generate(accountId: target.Id);

		using (ApplicationDbContext seed = CreateContext())
		{
			seed.Accounts.AddRange(target, source1, source2);
			seed.Cards.AddRange(cardOnSource1, cardOnSource2);
			seed.Transactions.AddRange(txOnSource1, txOnSource2, txOnTarget);
			await seed.SaveChangesAsync();
		}

		// Pre-merge: verify transactions exist.
		using (ApplicationDbContext preAssert = CreateContext())
		{
			int preCount = await preAssert.Transactions.IgnoreQueryFilters().CountAsync();
			int preAccountCount = await preAssert.Accounts.CountAsync();
			int preCardCount = await preAssert.Cards.CountAsync();
			(preCount, preAccountCount, preCardCount).Should().Be((3, 3, 2), "initial seed state");
		}

		MergeCardsResult result = await _service.MergeCardsAsync(
			target.Id,
			[cardOnSource1.Id, cardOnSource2.Id],
			null,
			CancellationToken.None);

		result.Conflicts.Should().BeNull();
		result.IsNoOp.Should().BeFalse();
		// Two source accounts deleted, two cards moved, and only the two transactions that
		// sat on a source — txOnTarget was already home and must not inflate the count.
		(result.AccountsRemoved, result.CardsMoved, result.TransactionsRepointed)
			.Should().Be((2, 2, 2));

		using ApplicationDbContext assert = CreateContext();
		List<CardEntity> cards = await assert.Cards.AsNoTracking().ToListAsync();
		cards.Should().OnlyContain(c => c.AccountId == target.Id);

		// IgnoreAutoIncludes avoids the InMemory provider filtering out rows whose
		// auto-included Receipt does not exist in the seed data.
		List<TransactionEntity> transactions = await assert.Transactions
			.IgnoreAutoIncludes().AsNoTracking().ToListAsync();
		transactions.Should().HaveCount(3);
		transactions.Should().OnlyContain(t => t.AccountId == target.Id);

		List<AccountEntity> remainingAccounts = await assert.Accounts.AsNoTracking().ToListAsync();
		remainingAccounts.Select(a => a.Id).Should().BeEquivalentTo([target.Id]);

		List<AuditLogEntity> auditLogs = await assert.AuditLogs.AsNoTracking()
			.Where(a => a.Action == AuditAction.Merge)
			.ToListAsync();
		auditLogs.Should().HaveCount(3); // 2 sources + 1 target
		auditLogs.Should().OnlyContain(a => a.EntityType == "Account");
		auditLogs.Should().OnlyContain(a => a.ChangedByUserId == "test-user");
	}

	[Fact]
	public async Task MergeCardsAsync_MovesSoftDeletedTransactions_AndDoesNotLoseThem()
	{
		// RECEIPTS-756: soft-deleted (trashed) transactions on a source account must be
		// repointed to the target during a merge. Before the fix the Phase-1 repoint passed
		// through the soft-delete query filter, leaving trashed rows behind for the Phase-2
		// account delete to cascade-destroy. They must survive and follow their card.
		AccountEntity target = AccountEntityGenerator.Generate();
		AccountEntity source = AccountEntityGenerator.Generate();

		CardEntity cardOnSource = CardEntityGenerator.Generate();
		cardOnSource.AccountId = source.Id;
		CardEntity cardOnTarget = CardEntityGenerator.Generate();
		cardOnTarget.AccountId = target.Id;

		TransactionEntity activeTx = TransactionEntityGenerator.Generate(accountId: source.Id);
		TransactionEntity softDeletedTx = TransactionEntityGenerator.Generate(accountId: source.Id);
		softDeletedTx.DeletedAt = DateTimeOffset.UtcNow;

		using (ApplicationDbContext seed = CreateContext())
		{
			seed.Accounts.AddRange(target, source);
			seed.Cards.AddRange(cardOnSource, cardOnTarget);
			seed.Transactions.AddRange(activeTx, softDeletedTx);
			await seed.SaveChangesAsync();
		}

		MergeCardsResult result = await _service.MergeCardsAsync(
			target.Id,
			[cardOnSource.Id, cardOnTarget.Id],
			null,
			CancellationToken.None);

		result.Conflicts.Should().BeNull();
		// Only cardOnSource changed hands — cardOnTarget was listed but already home.
		// TransactionsRepointed counts the soft-deleted row too, because it moved too.
		(result.AccountsRemoved, result.CardsMoved, result.TransactionsRepointed)
			.Should().Be((1, 1, 2));

		using ApplicationDbContext assert = CreateContext();

		// IgnoreQueryFilters reveals the soft-deleted row; IgnoreAutoIncludes avoids the
		// InMemory provider dropping rows whose auto-included Receipt is absent from the seed.
		List<TransactionEntity> allTransactions = await assert.Transactions
			.IgnoreQueryFilters()
			.IgnoreAutoIncludes()
			.AsNoTracking()
			.ToListAsync();

		// Nothing was lost: both transactions still exist and both now belong to the target.
		allTransactions.Should().HaveCount(2);
		allTransactions.Should().OnlyContain(t => t.AccountId == target.Id);

		// The soft-deleted transaction is still soft-deleted — merely repointed, not resurrected.
		allTransactions.Single(t => t.Id == softDeletedTx.Id).DeletedAt.Should().NotBeNull();

		// The orphaned source account was deleted (Phase 2 succeeded).
		List<AccountEntity> remainingAccounts = await assert.Accounts.AsNoTracking().ToListAsync();
		remainingAccounts.Select(a => a.Id).Should().BeEquivalentTo([target.Id]);
	}

	[Fact]
	public async Task MergeCardsAsync_WithSingleMappingOnSource_MovesMappingToTarget()
	{
		AccountEntity target = AccountEntityGenerator.Generate();
		AccountEntity source = AccountEntityGenerator.Generate();
		CardEntity card1 = CardEntityGenerator.Generate();
		CardEntity card2 = CardEntityGenerator.Generate();
		card1.AccountId = source.Id;
		card2.AccountId = source.Id;

		YnabAccountMappingEntity sourceMapping = new()
		{
			Id = Guid.NewGuid(),
			ReceiptsAccountId = source.Id,
			YnabAccountId = "ynab-1",
			YnabAccountName = "Source YNAB",
			YnabBudgetId = "budget-1",
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};

		using (ApplicationDbContext seed = CreateContext())
		{
			seed.Accounts.AddRange(target, source);
			seed.Cards.AddRange(card1, card2);
			seed.YnabAccountMappings.Add(sourceMapping);
			await seed.SaveChangesAsync();
		}

		MergeCardsResult result = await _service.MergeCardsAsync(
			target.Id,
			[card1.Id, card2.Id],
			null,
			CancellationToken.None);

		result.Conflicts.Should().BeNull();
		(result.AccountsRemoved, result.CardsMoved, result.TransactionsRepointed)
			.Should().Be((1, 2, 0));

		using ApplicationDbContext assert = CreateContext();
		List<YnabAccountMappingEntity> mappings = await assert.YnabAccountMappings.AsNoTracking().ToListAsync();
		mappings.Should().ContainSingle(m => m.ReceiptsAccountId == target.Id && m.YnabAccountId == "ynab-1");
	}

	[Fact]
	public async Task MergeCardsAsync_WithConflictingMappings_ReturnsConflictsWithoutMutation()
	{
		AccountEntity target = AccountEntityGenerator.Generate();
		AccountEntity source1 = AccountEntityGenerator.Generate();
		AccountEntity source2 = AccountEntityGenerator.Generate();
		CardEntity card1 = CardEntityGenerator.Generate();
		CardEntity card2 = CardEntityGenerator.Generate();
		card1.AccountId = source1.Id;
		card2.AccountId = source2.Id;

		YnabAccountMappingEntity mapping1 = new()
		{
			Id = Guid.NewGuid(),
			ReceiptsAccountId = source1.Id,
			YnabAccountId = "ynab-1",
			YnabAccountName = "Source1 YNAB",
			YnabBudgetId = "budget-1",
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};
		YnabAccountMappingEntity mapping2 = new()
		{
			Id = Guid.NewGuid(),
			ReceiptsAccountId = source2.Id,
			YnabAccountId = "ynab-2",
			YnabAccountName = "Source2 YNAB",
			YnabBudgetId = "budget-1",
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};

		using (ApplicationDbContext seed = CreateContext())
		{
			seed.Accounts.AddRange(target, source1, source2);
			seed.Cards.AddRange(card1, card2);
			seed.YnabAccountMappings.AddRange(mapping1, mapping2);
			await seed.SaveChangesAsync();
		}

		MergeCardsResult result = await _service.MergeCardsAsync(
			target.Id,
			[card1.Id, card2.Id],
			null,
			CancellationToken.None);

		result.Conflicts.Should().HaveCount(2);
		result.Conflicts!.Select(c => c.YnabAccountId).Should().BeEquivalentTo(["ynab-1", "ynab-2"]);
		// A refused merge wrote nothing, but it is not a no-op: the caller must prompt for a
		// winner rather than tell the user their cards were already where they wanted them.
		result.IsNoOp.Should().BeFalse();
		(result.AccountsRemoved, result.CardsMoved, result.TransactionsRepointed)
			.Should().Be((0, 0, 0));

		using ApplicationDbContext assert = CreateContext();
		List<CardEntity> cards = await assert.Cards.AsNoTracking().ToListAsync();
		cards.Single(c => c.Id == card1.Id).AccountId.Should().Be(source1.Id);
		cards.Single(c => c.Id == card2.Id).AccountId.Should().Be(source2.Id);
		(await assert.Accounts.AsNoTracking().ToListAsync()).Should().HaveCount(3);
	}

	[Fact]
	public async Task MergeCardsAsync_WithResolvedConflict_KeepsWinnerMapping()
	{
		AccountEntity target = AccountEntityGenerator.Generate();
		AccountEntity source1 = AccountEntityGenerator.Generate();
		AccountEntity source2 = AccountEntityGenerator.Generate();
		CardEntity card1 = CardEntityGenerator.Generate();
		CardEntity card2 = CardEntityGenerator.Generate();
		card1.AccountId = source1.Id;
		card2.AccountId = source2.Id;

		YnabAccountMappingEntity winnerMapping = new()
		{
			Id = Guid.NewGuid(),
			ReceiptsAccountId = source1.Id,
			YnabAccountId = "ynab-winner",
			YnabAccountName = "Winner YNAB",
			YnabBudgetId = "budget-1",
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};
		YnabAccountMappingEntity loserMapping = new()
		{
			Id = Guid.NewGuid(),
			ReceiptsAccountId = source2.Id,
			YnabAccountId = "ynab-loser",
			YnabAccountName = "Loser YNAB",
			YnabBudgetId = "budget-1",
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};

		using (ApplicationDbContext seed = CreateContext())
		{
			seed.Accounts.AddRange(target, source1, source2);
			seed.Cards.AddRange(card1, card2);
			seed.YnabAccountMappings.AddRange(winnerMapping, loserMapping);
			await seed.SaveChangesAsync();
		}

		MergeCardsResult result = await _service.MergeCardsAsync(
			target.Id,
			[card1.Id, card2.Id],
			source1.Id,
			CancellationToken.None);

		result.Conflicts.Should().BeNull();
		(result.AccountsRemoved, result.CardsMoved, result.TransactionsRepointed)
			.Should().Be((2, 2, 0));

		using ApplicationDbContext assert = CreateContext();
		List<YnabAccountMappingEntity> mappings = await assert.YnabAccountMappings.AsNoTracking().ToListAsync();
		mappings.Should().ContainSingle();
		mappings[0].YnabAccountId.Should().Be("ynab-winner");
		mappings[0].ReceiptsAccountId.Should().Be(target.Id);
	}

	[Fact]
	public async Task MergeCardsAsync_WithPartialSourceAccount_Throws()
	{
		AccountEntity target = AccountEntityGenerator.Generate();
		AccountEntity source = AccountEntityGenerator.Generate();
		CardEntity mergedCardOnSource = CardEntityGenerator.Generate();
		CardEntity leftBehindCardOnSource = CardEntityGenerator.Generate();
		CardEntity anotherMergedCard = CardEntityGenerator.Generate();
		mergedCardOnSource.AccountId = source.Id;
		leftBehindCardOnSource.AccountId = source.Id;
		anotherMergedCard.AccountId = target.Id;

		using (ApplicationDbContext seed = CreateContext())
		{
			seed.Accounts.AddRange(target, source);
			seed.Cards.AddRange(mergedCardOnSource, leftBehindCardOnSource, anotherMergedCard);
			await seed.SaveChangesAsync();
		}

		Func<Task> act = () => _service.MergeCardsAsync(
			target.Id,
			[mergedCardOnSource.Id, anotherMergedCard.Id],
			null,
			CancellationToken.None);

		await act.Should().ThrowAsync<ArgumentException>()
			.WithMessage(AccountMergeService.PartialSourceAccountMerge + "*");

		using ApplicationDbContext assert = CreateContext();
		CardEntity reloadedCard = await assert.Cards.AsNoTracking().FirstAsync(c => c.Id == leftBehindCardOnSource.Id);
		reloadedCard.AccountId.Should().Be(source.Id);
	}

	[Fact]
	public async Task MergeCardsAsync_AuditEntries_UsePerSourceTransactionCount()
	{
		AccountEntity target = AccountEntityGenerator.Generate();
		AccountEntity source1 = AccountEntityGenerator.Generate();
		AccountEntity source2 = AccountEntityGenerator.Generate();
		CardEntity card1 = CardEntityGenerator.Generate();
		CardEntity card2 = CardEntityGenerator.Generate();
		card1.AccountId = source1.Id;
		card2.AccountId = source2.Id;

		List<TransactionEntity> source1Txns = TransactionEntityGenerator.GenerateList(2, accountId: source1.Id);
		List<TransactionEntity> source2Txns = TransactionEntityGenerator.GenerateList(3, accountId: source2.Id);

		using (ApplicationDbContext seed = CreateContext())
		{
			seed.Accounts.AddRange(target, source1, source2);
			seed.Cards.AddRange(card1, card2);
			seed.Transactions.AddRange(source1Txns);
			seed.Transactions.AddRange(source2Txns);
			await seed.SaveChangesAsync();
		}

		await _service.MergeCardsAsync(
			target.Id,
			[card1.Id, card2.Id],
			null,
			CancellationToken.None);

		using ApplicationDbContext assert = CreateContext();
		List<AuditLogEntity> mergeAudits = await assert.AuditLogs
			.AsNoTracking()
			.Where(a => a.Action == AuditAction.Merge)
			.ToListAsync();

		// Assert through GetChanges rather than on the raw JSON. These entries now carry the same
		// FieldChange shape the automatic auditor produces, because the audit page parses
		// ChangesJson as a FieldChange array and rendered nothing for the previous object payload
		// (RECEIPTS-890). Reading them the way the app does keeps this test honest about the
		// contract rather than pinning an incidental serialization detail.
		AuditLogEntity source1Audit = mergeAudits.Single(a => a.EntityId == source1.Id.ToString());
		AuditLogEntity source2Audit = mergeAudits.Single(a => a.EntityId == source2.Id.ToString());
		MovedTransactionCount(source1Audit).Should().Be("2");
		MovedTransactionCount(source2Audit).Should().Be("3");

		AuditLogEntity targetAudit = mergeAudits.Single(a => a.EntityId == target.Id.ToString());
		MovedTransactionCount(targetAudit).Should().Be("5");

		// The whole point of the shape change: the detail is readable by the page's parser.
		source1Audit.GetChanges().Should().NotBeEmpty();
		source1Audit.GetChanges().Single(c => c.FieldName == "mergedIntoAccountId").NewValue
			.Should().Be(target.Id.ToString());

		static string? MovedTransactionCount(AuditLogEntity audit) =>
			audit.GetChanges().Single(c => c.FieldName == "movedTransactionCount").NewValue;
	}

	[Fact]
	public async Task MergeCardsAsync_WithInvalidWinner_Throws()
	{
		AccountEntity target = AccountEntityGenerator.Generate();
		AccountEntity source1 = AccountEntityGenerator.Generate();
		AccountEntity source2 = AccountEntityGenerator.Generate();
		CardEntity card1 = CardEntityGenerator.Generate();
		CardEntity card2 = CardEntityGenerator.Generate();
		card1.AccountId = source1.Id;
		card2.AccountId = source2.Id;

		YnabAccountMappingEntity mapping1 = new()
		{
			Id = Guid.NewGuid(),
			ReceiptsAccountId = source1.Id,
			YnabAccountId = "ynab-1",
			YnabAccountName = "Source1 YNAB",
			YnabBudgetId = "budget-1",
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};
		YnabAccountMappingEntity mapping2 = new()
		{
			Id = Guid.NewGuid(),
			ReceiptsAccountId = source2.Id,
			YnabAccountId = "ynab-2",
			YnabAccountName = "Source2 YNAB",
			YnabBudgetId = "budget-1",
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};

		using (ApplicationDbContext seed = CreateContext())
		{
			seed.Accounts.AddRange(target, source1, source2);
			seed.Cards.AddRange(card1, card2);
			seed.YnabAccountMappings.AddRange(mapping1, mapping2);
			await seed.SaveChangesAsync();
		}

		Func<Task> act = () => _service.MergeCardsAsync(
			target.Id,
			[card1.Id, card2.Id],
			Guid.NewGuid(),
			CancellationToken.None);

		await act.Should().ThrowAsync<ArgumentException>()
			.WithMessage(AccountMergeService.InvalidWinnerAccount + "*");
	}

	private sealed class TestFactory(
		DbContextOptions<ApplicationDbContext> options,
		Application.Interfaces.Services.ICurrentUserAccessor accessor)
		: IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options, accessor);
	}
}
