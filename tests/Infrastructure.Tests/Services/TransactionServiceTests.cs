using Application.Models;
using Domain;
using Domain.Aggregates;
using Domain.Core;
using FluentAssertions;
using FluentValidation;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Infrastructure.Tests.Repositories;
using Microsoft.EntityFrameworkCore;
using Moq;
using SampleData.Domain.Core;
using SampleData.Entities;

namespace Infrastructure.Tests.Services;

public class TransactionServiceTests
{
	private readonly Mock<ITransactionRepository> _mockRepository;
	private readonly TransactionMapper _mapper;
	private readonly AccountMapper _accountMapper;
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
	private readonly TransactionService _service;

	public TransactionServiceTests()
	{
		_mockRepository = new Mock<ITransactionRepository>();
		_mapper = new TransactionMapper();
		_accountMapper = new AccountMapper();
		_contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		_service = new TransactionService(
			_mockRepository.Object,
			_mapper,
			_accountMapper,
			_contextFactory,
			new ReceiptMapper(),
			new ReceiptItemMapper(),
			new AdjustmentMapper());
	}

	// Balanced no-op validation delegate (the balance rule lives in the Application handlers).
	private static readonly Action<ReceiptBalanceState> NoOpValidate = _ => { };

	private async Task SeedReceiptWithItemAsync(Guid receiptId, bool softDeleted = false)
	{
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.Id = receiptId;
		await context.Receipts.AddAsync(receipt);
		await context.ReceiptItems.AddAsync(ReceiptItemEntityGenerator.Generate(receiptId)); // TotalAmount $5, Tax $10 => ExpectedTotal $15
		await context.SaveChangesAsync(CancellationToken.None);

		if (softDeleted)
		{
			context.Receipts.Remove(receipt); // intercepted as a soft delete
			await context.SaveChangesAsync(CancellationToken.None);
		}
	}

	// Counts with IgnoreQueryFilters (no transaction is ever soft-deleted in these tests, so this
	// is an exact row count) to avoid an InMemory quirk where the DeletedAt==null soft-delete
	// filter is evaluated inconsistently right after a write.
	private async Task<int> CountTransactionsAsync(Guid receiptId)
	{
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		return await context.Transactions.IgnoreQueryFilters().CountAsync(t => t.ReceiptId == receiptId);
	}

	[Fact]
	public async Task CreateAsync_ValidTransactions_CallsRepositoryCreateAsyncAndReturnsCreatedTransactions()
	{
		// Arrange
		List<Transaction> models = TransactionGenerator.GenerateList(2);
		Guid receiptId = Guid.NewGuid();
		List<TransactionEntity> createdEntities = TransactionEntityGenerator.GenerateList(2);

		_mockRepository.Setup(r => r.CreateAsync(It.IsAny<List<TransactionEntity>>(), It.IsAny<CancellationToken>())).ReturnsAsync(createdEntities);

		// Act
		List<Transaction> actual = await _service.CreateAsync(models, receiptId, CancellationToken.None);

		// Assert
		Assert.Equal(createdEntities.Count, actual.Count);
		_mockRepository.Verify(r => r.CreateAsync(It.IsAny<List<TransactionEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task DeleteAsync_ValidIds_CallsRepositoryDeleteAsync()
	{
		// Arrange
		List<Guid> ids = [Guid.NewGuid(), Guid.NewGuid()];

		// Act
		await _service.DeleteAsync(ids, CancellationToken.None);

		// Assert
		_mockRepository.Verify(r => r.DeleteAsync(ids, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task ExistsAsync_ValidId_ReturnsExpectedResult()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		bool expected = true;
		_mockRepository.Setup(r => r.ExistsAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		bool actual = await _service.ExistsAsync(id, CancellationToken.None);

		// Assert
		Assert.Equal(expected, actual);
	}

	[Fact]
	public async Task GetAllAsync_ReturnsAllTransactions()
	{
		// Arrange
		List<TransactionEntity> entities = TransactionEntityGenerator.GenerateList(3);

		_mockRepository.Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entities.Count);
		_mockRepository.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>())).ReturnsAsync(entities);

		// Act
		PagedResult<Transaction> actual = await _service.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		Assert.Equal(entities.Count, actual.Data.Count);
		Assert.Equal(entities.Count, actual.Total);
		Assert.Equal(0, actual.Offset);
		Assert.Equal(50, actual.Limit);
	}

	[Fact]
	public async Task GetByIdAsync_ExistingId_ReturnsTransaction()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		TransactionEntity entity = TransactionEntityGenerator.Generate();

		_mockRepository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);

		// Act
		Transaction? actual = await _service.GetByIdAsync(id, CancellationToken.None);

		// Assert
		Assert.NotNull(actual);
		actual.Id.Should().Be(entity.Id);
	}

	[Fact]
	public async Task GetByIdAsync_NonExistingId_ReturnsNull()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mockRepository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((TransactionEntity?)null);

		// Act
		Transaction? actual = await _service.GetByIdAsync(id, CancellationToken.None);

		// Assert
		Assert.Null(actual);
	}

	[Fact]
	public async Task GetByReceiptIdAsync_ExistingReceiptId_ReturnsTransactions()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<TransactionEntity> entities = TransactionEntityGenerator.GenerateList(2);

		_mockRepository.Setup(r => r.GetByReceiptIdCountAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(entities.Count);
		_mockRepository.Setup(r => r.GetByReceiptIdAsync(receiptId, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>())).ReturnsAsync(entities);

		// Act
		PagedResult<Transaction> actual = await _service.GetByReceiptIdAsync(receiptId, 0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		Assert.Equal(entities.Count, actual.Data.Count);
		Assert.Equal(entities.Count, actual.Total);
		Assert.Equal(0, actual.Offset);
		Assert.Equal(50, actual.Limit);
	}

	[Fact]
	public async Task GetByReceiptIdAsync_NonExistingReceiptId_ReturnsEmptyPagedResult()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		_mockRepository.Setup(r => r.GetByReceiptIdCountAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(0);
		_mockRepository.Setup(r => r.GetByReceiptIdAsync(receiptId, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

		// Act
		PagedResult<Transaction> actual = await _service.GetByReceiptIdAsync(receiptId, 0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		Assert.Empty(actual.Data);
		Assert.Equal(0, actual.Total);
	}

	[Fact]
	public async Task GetCountAsync_ReturnsCorrectCount()
	{
		// Arrange
		int expected = 5;
		_mockRepository.Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		int actual = await _service.GetCountAsync(CancellationToken.None);

		// Assert
		Assert.Equal(expected, actual);
	}

	[Fact]
	public async Task UpdateAsync_ValidTransactions_CallsRepositoryUpdateAsync()
	{
		// Arrange
		List<Transaction> models = TransactionGenerator.GenerateList(2);
		Guid receiptId = Guid.NewGuid();

		// Act
		await _service.UpdateAsync(models, receiptId, CancellationToken.None);

		// Assert
		_mockRepository.Verify(r => r.UpdateAsync(It.Is<List<TransactionEntity>>(e =>
			e.All(t => t.ReceiptId == receiptId)),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetTransactionAccountsByReceiptIdAsync_ReturnsTransactionAccounts()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<AccountEntity> accountEntities = AccountEntityGenerator.GenerateList(3);
		List<TransactionEntity> transactionEntities = TransactionEntityGenerator.GenerateList(3, receiptId);
		for (int i = 0; i < transactionEntities.Count; i++)
		{
			transactionEntities[i].AccountId = accountEntities[i].Id;
			transactionEntities[i].Account = accountEntities[i];
		}

		_mockRepository.Setup(r => r.GetWithAccountByReceiptIdAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(transactionEntities);

		// Act
		List<TransactionAccount> result = await _service.GetTransactionAccountsByReceiptIdAsync(receiptId, CancellationToken.None);

		// Assert
		result.Should().HaveCount(3);
		for (int i = 0; i < result.Count; i++)
		{
			result[i].Transaction.Id.Should().Be(transactionEntities[i].Id);
			result[i].Account.Id.Should().Be(accountEntities[i].Id);
		}
	}

	[Fact]
	public async Task GetTransactionAccountsByReceiptIdAsync_SkipsTransactionsWithNullAccount()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<TransactionEntity> transactionEntities = TransactionEntityGenerator.GenerateList(2, receiptId);
		AccountEntity account = AccountEntityGenerator.Generate();
		transactionEntities[0].Account = account;
		transactionEntities[0].AccountId = account.Id;
		transactionEntities[1].Account = null;

		_mockRepository.Setup(r => r.GetWithAccountByReceiptIdAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(transactionEntities);

		// Act
		List<TransactionAccount> result = await _service.GetTransactionAccountsByReceiptIdAsync(receiptId, CancellationToken.None);

		// Assert
		result.Should().HaveCount(1);
		result[0].Transaction.Id.Should().Be(transactionEntities[0].Id);
	}

	[Fact]
	public async Task GetTransactionAccountsByReceiptIdAsync_EmptyList_ReturnsEmpty()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		_mockRepository.Setup(r => r.GetWithAccountByReceiptIdAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		// Act
		List<TransactionAccount> result = await _service.GetTransactionAccountsByReceiptIdAsync(receiptId, CancellationToken.None);

		// Assert
		result.Should().BeEmpty();
	}

	// ── CreateWithBalanceValidationAsync (RECEIPTS-763 / RECEIPTS-764) ──
	// NOTE: the InMemory provider has no real transactions or row locks, so these tests verify
	// the read-validate-write orchestration and the existence/soft-delete guard, NOT concurrent
	// serialization. Cross-request serialization is exercised against real PostgreSQL in
	// Infrastructure.IntegrationTests.TransactionBalanceConcurrencyTests.

	[Fact]
	public async Task CreateWithBalanceValidationAsync_ExistingReceipt_PersistsTransactionsAndReturnsThem()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		await SeedReceiptWithItemAsync(receiptId);
		List<Transaction> input = [new(Guid.NewGuid(), Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }];

		// Act
		List<Transaction> result = await _service.CreateWithBalanceValidationAsync(input, receiptId, NoOpValidate, CancellationToken.None);

		// Assert
		result.Should().HaveCount(1);
		(await CountTransactionsAsync(receiptId)).Should().Be(1);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task CreateWithBalanceValidationAsync_PassesFreshReceiptSnapshotToValidator()
	{
		// Arrange — the validator must see the receipt, its items, and its existing transactions
		// as re-read INSIDE the guarded scope.
		Guid receiptId = Guid.NewGuid();
		await SeedReceiptWithItemAsync(receiptId);
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			await seed.Transactions.AddAsync(TransactionEntityGenerator.Generate(receiptId));
			await seed.SaveChangesAsync(CancellationToken.None);
		}

		ReceiptBalanceState? captured = null;
		List<Transaction> input = [new(Guid.NewGuid(), Guid.NewGuid(), new Money(1), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }];

		// Act
		await _service.CreateWithBalanceValidationAsync(input, receiptId, state => captured = state, CancellationToken.None);

		// Assert
		captured.Should().NotBeNull();
		captured!.Receipt.Id.Should().Be(receiptId);
		captured.Items.Should().HaveCount(1);
		captured.ExistingTransactions.Should().HaveCount(1);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task CreateWithBalanceValidationAsync_MissingReceipt_ThrowsKeyNotFoundAndPersistsNothing()
	{
		// Arrange — no receipt seeded (RECEIPTS-763).
		Guid receiptId = Guid.NewGuid();
		List<Transaction> input = [new(Guid.NewGuid(), Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }];

		// Act
		Func<Task> act = async () => await _service.CreateWithBalanceValidationAsync(input, receiptId, NoOpValidate, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<KeyNotFoundException>();
		(await CountTransactionsAsync(receiptId)).Should().Be(0);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task CreateWithBalanceValidationAsync_SoftDeletedReceipt_ThrowsKeyNotFoundAndCreatesNoOrphan()
	{
		// Arrange — receipt exists in the table but is soft-deleted; the FK row still exists, so
		// without the guard an ACTIVE transaction would be orphaned under a trashed receipt.
		Guid receiptId = Guid.NewGuid();
		await SeedReceiptWithItemAsync(receiptId, softDeleted: true);
		List<Transaction> input = [new(Guid.NewGuid(), Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }];

		// Act
		Func<Task> act = async () => await _service.CreateWithBalanceValidationAsync(input, receiptId, NoOpValidate, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<KeyNotFoundException>();
		(await CountTransactionsAsync(receiptId)).Should().Be(0);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task CreateWithBalanceValidationAsync_ValidatorThrows_PersistsNothing()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		await SeedReceiptWithItemAsync(receiptId);
		List<Transaction> input = [new(Guid.NewGuid(), Guid.NewGuid(), new Money(999), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }];

		// Act — validator rejects (mirrors the handler's balance-equation failure)
		Func<Task> act = async () => await _service.CreateWithBalanceValidationAsync(
			input, receiptId,
			_ => throw new ValidationException("unbalanced"),
			CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<ValidationException>();
		(await CountTransactionsAsync(receiptId)).Should().Be(0);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task UpdateWithBalanceValidationAsync_ExistingReceipt_UpdatesTransaction()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		await SeedReceiptWithItemAsync(receiptId);
		TransactionEntity seeded = TransactionEntityGenerator.Generate(receiptId);
		Guid seededAccountId = seeded.AccountId;
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			await seed.Transactions.AddAsync(seeded);
			await seed.SaveChangesAsync(CancellationToken.None);
		}

		// Read the id actually assigned by the store (the InMemory key generator can reassign a
		// ValueGeneratedOnAdd Guid on save; PostgreSQL keeps the client value). IgnoreAutoIncludes
		// is required: the Account / Card navs are REQUIRED (non-nullable FK), so materializing the
		// full entity would INNER JOIN and drop the row (no Account/Card is seeded here).
		Guid seededId;
		using (ApplicationDbContext read = _contextFactory.CreateDbContext())
		{
			seededId = (await read.Transactions.IgnoreQueryFilters().IgnoreAutoIncludes().ToListAsync()).Single().Id;
		}

		List<Transaction> update = [new(seededId, Guid.NewGuid(), new Money(42), DateOnly.FromDateTime(DateTime.Now)) { AccountId = seededAccountId }];

		// Act
		await _service.UpdateWithBalanceValidationAsync(update, receiptId, NoOpValidate, CancellationToken.None);

		// Assert — the transaction was updated in place (amount changed) and NOT soft-deleted.
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		TransactionEntity persisted = (await verify.Transactions.IgnoreQueryFilters().IgnoreAutoIncludes().ToListAsync())
			.Single(t => t.Id == seededId);
		persisted.Amount.Should().Be(42m);
		persisted.DeletedAt.Should().BeNull();

		_contextFactory.ResetDatabase();
	}
}
