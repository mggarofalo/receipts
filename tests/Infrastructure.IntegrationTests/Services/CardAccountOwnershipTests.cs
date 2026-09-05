using Application.Models;
using FluentAssertions;
using FluentAssertions.Execution;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
public class CardAccountOwnershipTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task AccountReads_FollowCardsCurrentOwner_ForActiveAndTrashedHistory(bool reassign)
	{
		Guid original = Guid.NewGuid(), replacement = Guid.NewGuid(), card = Guid.NewGuid(), receipt = Guid.NewGuid(), active = Guid.NewGuid(), trashed = Guid.NewGuid();
		DateOnly date = new(2024, 1, reassign ? 2 : 1);
		string originalName = $"Original {original}", replacementName = $"Replacement {replacement}";
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Accounts.AddRange(new() { Id = original, Name = originalName, IsActive = true }, new() { Id = replacement, Name = replacementName, IsActive = true });
			seed.Cards.Add(new() { Id = card, AccountId = original, CardCode = "1234", Name = "Owned card", IsActive = true });
			seed.Receipts.Add(new() { Id = receipt, Location = "Account ownership", Date = date });
			await seed.SaveChangesAsync();
			TransactionEntity live = new() { Id = active, ReceiptId = receipt, CardId = card, Amount = 10, Date = date };
			TransactionEntity deleted = new() { Id = trashed, ReceiptId = receipt, CardId = card, Amount = 20, Date = date, DeletedAt = DateTimeOffset.UtcNow };
			seed.Transactions.AddRange(live, deleted);
			await seed.SaveChangesAsync();
		}
		if (reassign)
		{
			await using ApplicationDbContext edit = fixture.CreateDbContext();
			(await edit.Cards.SingleAsync(row => row.Id == card)).AccountId = replacement;
			await edit.SaveChangesAsync();
		}

		Guid expected = reassign ? replacement : original;
		string expectedName = reassign ? replacementName : originalName;
		Factory factory = new(fixture.CreateOptions());
		DashboardService dashboard = new(factory);
		TransactionService transactions = new(new TransactionRepository(factory), new TransactionMapper(), new AccountMapper(), factory, new ReceiptMapper(), new ReceiptItemMapper(), new AdjustmentMapper());
		AccountRepository accounts = new(factory);
		ReceiptRepository receipts = new(factory);
		var summary = await dashboard.GetSummaryAsync(date, date, CancellationToken.None);
		var spending = await dashboard.GetSpendingByAccountAsync(date, date, CancellationToken.None);
		var transaction = await transactions.GetByIdAsync(active, CancellationToken.None);
		var all = await transactions.GetAllAsync(0, 500, SortParams.Default, CancellationToken.None);
		var byReceipt = await transactions.GetByReceiptIdAsync(receipt, 0, 500, SortParams.Default, CancellationToken.None);
		var transactionAccounts = await transactions.GetTransactionAccountsByReceiptIdAsync(receipt, CancellationToken.None);
		var deletedPage = await transactions.GetDeletedAsync(0, 100, SortParams.Default, CancellationToken.None);
		var account = await accounts.GetByTransactionIdAsync(active, CancellationToken.None);
		var filteredReceipts = await receipts.GetAllAsync(0, 100, SortParams.Default, expected, null, null, null, CancellationToken.None);
		int history = await accounts.GetTransactionCountByAccountIdAsync(expected, CancellationToken.None);

		using AssertionScope assertions = new();
		filteredReceipts.Should().ContainSingle(row => row.Id == receipt, "the receipt account filter already follows the card relationship");
		summary.MostUsedAccount.Name.Should().Be(expectedName);
		summary.TotalSpent.Should().Be(10, "trash stays excluded from dashboard totals");
		spending.Items.Should().ContainSingle(row => row.AccountId == expected && row.AccountName == expectedName && row.Amount == 10);
		transaction!.AccountId.Should().Be(expected);
		all.Data.Single(row => row.Id == active).AccountId.Should().Be(expected);
		byReceipt.Data.Should().ContainSingle().Which.AccountId.Should().Be(expected);
		transactionAccounts.Should().ContainSingle(row => row.Transaction.Id == active && row.Account.Id == expected);
		deletedPage.Data.Single(row => row.Id == trashed).AccountId.Should().Be(expected);
		account!.Id.Should().Be(expected);
		history.Should().Be(2, "account guards must include active and trashed history through the current card owner");
		if (reassign)
		{
			(await accounts.GetTransactionCountByAccountIdAsync(original, CancellationToken.None)).Should().Be(0);
		}
	}

	[Fact]
	public async Task Merge_PreviewsAndMovesActiveAndTrashedHistory_PreservingPerSourceAuditCounts()
	{
		Guid target = Guid.NewGuid(), first = Guid.NewGuid(), second = Guid.NewGuid(), card1 = Guid.NewGuid(), card2 = Guid.NewGuid(), receipt = Guid.NewGuid();
		Guid live = Guid.NewGuid(), deleted1 = Guid.NewGuid(), deleted2 = Guid.NewGuid();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Accounts.AddRange(new() { Id = target, Name = "Target" }, new() { Id = first, Name = "Source one" }, new() { Id = second, Name = "Source two" });
			seed.Cards.AddRange(new() { Id = card1, AccountId = first }, new() { Id = card2, AccountId = second });
			seed.Receipts.Add(new() { Id = receipt, Location = "Merge", Date = new(2025, 1, 1) });
			await seed.SaveChangesAsync();
			seed.Transactions.AddRange(new() { Id = live, ReceiptId = receipt, CardId = card1, Amount = 10, Date = new(2025, 1, 1) }, new() { Id = deleted1, ReceiptId = receipt, CardId = card1, Amount = 20, Date = new(2025, 1, 1), DeletedAt = DateTimeOffset.UtcNow }, new() { Id = deleted2, ReceiptId = receipt, CardId = card2, Amount = 30, Date = new(2025, 1, 1), DeletedAt = DateTimeOffset.UtcNow });
			await seed.SaveChangesAsync();
		}
		AccountMergeService service = new(new Factory(fixture.CreateOptions()), new NullCurrentUserAccessor());
		var preview = await service.PreviewMergeCardsAsync(target, [card1, card2], null, CancellationToken.None);
		preview.TransactionsToRepoint.Should().Be(1);
		preview.TrashedTransactionsToRepoint.Should().Be(2);
		var result = await service.MergeCardsAsync(target, [card1, card2], null, CancellationToken.None);
		result.TransactionsRepointed.Should().Be(3);
		result.AccountsRemoved.Should().Be(2);
		await using ApplicationDbContext read = fixture.CreateDbContext();
		var payments = await read.Transactions.IgnoreQueryFilters().IgnoreAutoIncludes().Include(row => row.Card).Where(row => row.ReceiptId == receipt).ToListAsync();
		payments.Should().HaveCount(3).And.OnlyContain(row => row.Card!.AccountId == target);
		payments.Count(row => row.DeletedAt != null).Should().Be(2);
		foreach ((Guid id, int count) in new[] { (first, 2), (second, 1) })
		{
			var audit = await read.AuditLogs.SingleAsync(row => row.EntityId == id.ToString() && row.Action == Infrastructure.Entities.Audit.AuditAction.Merge);
			audit.GetChanges().Single(change => change.FieldName == "movedTransactionCount").NewValue.Should().Be(count.ToString());
		}
	}

	[Fact]
	public async Task PortableBackup_RoundTripsTransactionsUsingCurrentCardOwnership()
	{
		Guid account = Guid.NewGuid(), card = Guid.NewGuid(), receipt = Guid.NewGuid(), payment = Guid.NewGuid();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Accounts.Add(new() { Id = account, Name = "Portable owner", IsActive = true });
			seed.Cards.Add(new() { Id = card, AccountId = account, Name = "Portable card", CardCode = "1234", IsActive = true });
			seed.Receipts.Add(new() { Id = receipt, Location = "Portable payment", Date = new(2025, 1, 1) });
			seed.Transactions.Add(new() { Id = payment, ReceiptId = receipt, CardId = card, Amount = 10, Date = new(2025, 1, 1) });
			await seed.SaveChangesAsync();
		}
		string path = await new BackupService(new Factory(fixture.CreateOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		try
		{
			await using FileStream stream = File.OpenRead(path);
			PostgresFixture target = new();
			await target.InitializeAsync();
			try
			{
				await new BackupImportService(new Factory(target.CreateOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, CancellationToken.None);
				await using ApplicationDbContext read = target.CreateDbContext();
				var restored = await read.Transactions.SingleAsync(row => row.Id == payment);
				restored.CardId.Should().Be(card);
				new TransactionMapper().ToDomain(restored).AccountId.Should().Be(account);
				restored.Amount.Should().Be(10);
			}
			finally { await target.DisposeAsync(); }
		}
		finally { File.Delete(path); }
	}

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
	}
}
