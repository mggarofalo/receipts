using System.Data.Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class DuplicateAcceptanceInvalidationTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	[Theory]
	[InlineData("date")]
	[InlineData("location")]
	[InlineData("payment")]
	[InlineData("add-payment")]
	[InlineData("delete-payment")]
	public async Task ChangedDuplicateFact_InvalidatesOnlyPairsTouchingChangedReceipt(string change)
	{
		Seeded data = await SeedAsync();
		await using (ApplicationDbContext write = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = await write.Receipts.SingleAsync(r => r.Id == data.A);
			TransactionEntity payment = await write.Transactions.SingleAsync(t => t.ReceiptId == data.A);
			switch (change)
			{
				case "date": receipt.Date = receipt.Date.AddDays(1); break;
				case "location": receipt.Location = "Changed location"; break;
				case "payment": payment.Amount = 11m; break;
				case "add-payment":
					TransactionEntity added = TransactionEntityGenerator.Generate(data.A, data.AccountId, data.CardId);
					added.Amount = 1m;
					write.Transactions.Add(added);
					break;
				case "delete-payment": write.Transactions.Remove(payment); break;
			}
			await write.SaveChangesAsync();
		}

		await using ApplicationDbContext read = fixture.CreateDbContext();
		List<AcceptedDuplicatePairEntity> active = await read.AcceptedDuplicatePairs.ToListAsync();
		active.Should().ContainSingle().Which.Id.Should().Be(data.UnrelatedPair);
		(await read.AcceptedDuplicatePairs.IgnoreQueryFilters().SingleAsync(p => p.Id == data.ChangedPair))
			.DeletedAt.Should().NotBeNull("invalidation must retain acceptance history");
	}

	[Fact]
	public async Task MovingPayment_InvalidatesBothOldAndNewReceiptPairs()
	{
		Seeded data = await SeedAsync();
		await using (ApplicationDbContext write = fixture.CreateDbContext())
		{
			TransactionEntity payment = await write.Transactions.SingleAsync(t => t.ReceiptId == data.A);
			payment.ReceiptId = data.C;
			await write.SaveChangesAsync();
		}
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.AcceptedDuplicatePairs.CountAsync()).Should().Be(0);
	}

	[Theory]
	[InlineData("no-op")]
	[InlineData("tax")]
	[InlineData("payment-date")]
	[InlineData("net-zero")]
	public async Task UnchangedDuplicateFacts_PreserveAcceptance(string change)
	{
		Seeded data = await SeedAsync();
		await using (ApplicationDbContext write = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = await write.Receipts.SingleAsync(r => r.Id == data.A);
			TransactionEntity payment = await write.Transactions.SingleAsync(t => t.ReceiptId == data.A);
			switch (change)
			{
				case "no-op": write.Entry(receipt).Property(r => r.Location).IsModified = true; break;
				case "tax": receipt.TaxAmount = 3m; break;
				case "payment-date": payment.Date = payment.Date.AddDays(1); break;
				case "net-zero":
					payment.Amount = 9m;
					TransactionEntity added = TransactionEntityGenerator.Generate(data.A, data.AccountId, data.CardId);
					added.Amount = 1m;
					write.Transactions.Add(added);
					break;
			}
			await write.SaveChangesAsync();
		}
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.AcceptedDuplicatePairs.CountAsync()).Should().Be(2);
	}

	[Fact]
	public async Task ReceiptTrashAndRestore_WithCascadedPayments_PreservesAcceptance()
	{
		Seeded data = await SeedAsync();
		await using (ApplicationDbContext write = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = await write.Receipts.SingleAsync(r => r.Id == data.A);
			await write.Transactions.Where(t => t.ReceiptId == data.A).LoadAsync();
			write.Receipts.Remove(receipt);
			await write.SaveChangesAsync();
		}
		await using (ApplicationDbContext restore = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = await restore.Receipts.IgnoreQueryFilters().SingleAsync(r => r.Id == data.A);
			receipt.DeletedAt = null;
			List<TransactionEntity> payments = await restore.Transactions.IgnoreQueryFilters().Where(t => t.ReceiptId == data.A).ToListAsync();
			payments.Should().OnlyContain(t => t.DeletedAt != null);
			foreach (TransactionEntity payment in payments)
			{
				payment.DeletedAt = null;
				payment.CascadeDeletedByParentId = null;
			}
			await restore.SaveChangesAsync();
		}
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.AcceptedDuplicatePairs.CountAsync()).Should().Be(2);
		(await read.Transactions.SingleAsync(t => t.ReceiptId == data.A)).Amount.Should().Be(10m);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task AuditFailure_RollsBackFactAndInvalidation_EvenWhenCallerCommits(bool callerTransaction)
	{
		Seeded data = await SeedAsync();
		await using ApplicationDbContext before = fixture.CreateDbContext();
		int auditCount = await before.AuditLogs.CountAsync(a => a.EntityId == data.A.ToString());
		RejectAuditInsert fault = new();
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
			.AddInterceptors(fault).Options;
		await using (ApplicationDbContext write = new(options))
		{
			await using var transaction = callerTransaction ? await write.Database.BeginTransactionAsync() : null;
			ReceiptEntity receipt = await write.Receipts.SingleAsync(r => r.Id == data.A);
			receipt.Location = "Must roll back";
			Func<Task> save = () => write.SaveChangesAsync();
			await save.Should().ThrowAsync<DbUpdateException>();
			fault.Fired.Should().BeTrue();
			if (transaction is not null)
			{
				await transaction.CommitAsync();
			}
		}
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.Receipts.SingleAsync(r => r.Id == data.A)).Location.Should().Be("Shared location");
		(await read.AcceptedDuplicatePairs.CountAsync()).Should().Be(2);
		(await read.AuditLogs.CountAsync(a => a.EntityId == data.A.ToString())).Should().Be(auditCount);
	}

	[Fact]
	public async Task EditingCascadeDeletedPaymentWhileReceiptTrashed_InvalidatesAcceptance()
	{
		Seeded data = await SeedAsync();
		await using (ApplicationDbContext trash = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = await trash.Receipts.SingleAsync(r => r.Id == data.A);
			await trash.Transactions.Where(t => t.ReceiptId == data.A).LoadAsync();
			trash.Receipts.Remove(receipt);
			await trash.SaveChangesAsync();
		}
		await using (ApplicationDbContext write = fixture.CreateDbContext())
		{
			TransactionEntity payment = await write.Transactions.IgnoreQueryFilters().SingleAsync(t => t.ReceiptId == data.A);
			payment.CascadeDeletedByParentId.Should().Be(data.A);
			payment.Amount = 12m;
			await write.SaveChangesAsync();
		}
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.AcceptedDuplicatePairs.ToListAsync()).Should().ContainSingle().Which.Id.Should().Be(data.UnrelatedPair);
	}

	[Fact]
	public async Task EditingIndependentlyDeletedPayment_PreservesAcceptance()
	{
		Seeded data = await SeedAsync(independentlyDeletedPayment: true);
		await using (ApplicationDbContext write = fixture.CreateDbContext())
		{
			TransactionEntity payment = await write.Transactions.IgnoreQueryFilters().SingleAsync(t => t.ReceiptId == data.A);
			payment.DeletedAt.Should().NotBeNull();
			payment.CascadeDeletedByParentId.Should().BeNull();
			payment.Amount = 12m;
			await write.SaveChangesAsync();
		}
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.AcceptedDuplicatePairs.CountAsync()).Should().Be(2);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task SaveOverloads_InvalidateAtomically_AndHonorAcceptAllChanges(bool synchronous, bool acceptAllChanges)
	{
		Seeded data = await SeedAsync();
		await using (ApplicationDbContext write = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = await write.Receipts.SingleAsync(r => r.Id == data.A);
			receipt.Location = "Updated by overload";
			int changed = synchronous ? write.SaveChanges(acceptAllChanges) : await write.SaveChangesAsync(acceptAllChanges);
			changed.Should().Be(1, "derived acceptance invalidation must not change the business-row result");
			write.Entry(receipt).State.Should().Be(acceptAllChanges ? EntityState.Unchanged : EntityState.Modified);
		}
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.Receipts.SingleAsync(r => r.Id == data.A)).Location.Should().Be("Updated by overload");
		(await read.AcceptedDuplicatePairs.ToListAsync()).Should().ContainSingle().Which.Id.Should().Be(data.UnrelatedPair);
	}

	[Fact]
	public async Task StalePaymentParent_RejectsWriteAndPreservesAcceptanceOfCurrentParent()
	{
		Seeded data = await SeedAsync();
		await using ApplicationDbContext stale = fixture.CreateDbContext();
		TransactionEntity stalePayment = await stale.Transactions.SingleAsync(t => t.ReceiptId == data.A);
		await using (ApplicationDbContext move = fixture.CreateDbContext())
		{
			TransactionEntity payment = await move.Transactions.SingleAsync(t => t.Id == stalePayment.Id);
			payment.ReceiptId = data.C;
			await move.SaveChangesAsync();
			move.AcceptedDuplicatePairs.Add(new AcceptedDuplicatePairEntity
			{
				Id = Guid.NewGuid(),
				ReceiptIdA = data.B,
				ReceiptIdB = data.C,
				AcceptedAt = DateTimeOffset.UtcNow,
			});
			await move.SaveChangesAsync();
		}
		stalePayment.Amount = 99m;
		Func<Task> save = () => stale.SaveChangesAsync();
		await save.Should().ThrowAsync<Application.Exceptions.ConcurrencyConflictException>();

		await using ApplicationDbContext read = fixture.CreateDbContext();
		TransactionEntity stored = await read.Transactions.SingleAsync(t => t.Id == stalePayment.Id);
		stored.ReceiptId.Should().Be(data.C);
		stored.Amount.Should().Be(10m);
		(await read.AcceptedDuplicatePairs.CountAsync()).Should().Be(1);
	}

	[Fact]
	public async Task EditingThenRevertingFacts_DoesNotResurrectAcceptance()
	{
		Seeded data = await SeedAsync();
		await using (ApplicationDbContext write = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = await write.Receipts.SingleAsync(r => r.Id == data.A);
			receipt.Location = "Temporary changed location";
			await write.SaveChangesAsync();
			receipt.Location = "Shared location";
			await write.SaveChangesAsync();
		}
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.Receipts.SingleAsync(r => r.Id == data.A)).Location.Should().Be("Shared location");
		(await read.AcceptedDuplicatePairs.ToListAsync()).Should().ContainSingle().Which.Id.Should().Be(data.UnrelatedPair);
	}

	[Fact]
	public async Task RepeatedReparentSaveWithoutAcceptingChanges_PreservesFreshAcceptance()
	{
		Seeded data = await SeedAsync();
		await using ApplicationDbContext write = fixture.CreateDbContext();
		TransactionEntity payment = await write.Transactions.SingleAsync(t => t.ReceiptId == data.A);
		payment.ReceiptId = data.C;
		await write.SaveChangesAsync(false);
		write.Entry(payment).Property(t => t.ReceiptId).OriginalValue.Should().Be(data.A);

		Guid freshAcceptance = Guid.NewGuid();
		await using (ApplicationDbContext accept = fixture.CreateDbContext())
		{
			(await accept.AcceptedDuplicatePairs.CountAsync()).Should().Be(0);
			accept.AcceptedDuplicatePairs.Add(new AcceptedDuplicatePairEntity
			{
				Id = freshAcceptance,
				ReceiptIdA = data.B,
				ReceiptIdB = data.C,
				AcceptedAt = DateTimeOffset.UtcNow,
			});
			await accept.SaveChangesAsync();
		}

		await write.SaveChangesAsync(true);
		write.Entry(payment).State.Should().Be(EntityState.Unchanged);
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.Transactions.SingleAsync(t => t.Id == payment.Id)).ReceiptId.Should().Be(data.C);
		(await read.AcceptedDuplicatePairs.ToListAsync()).Should().ContainSingle().Which.Id.Should().Be(freshAcceptance);
	}

	private async Task<Seeded> SeedAsync(bool independentlyDeletedPayment = false)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		context.Accounts.Add(account);
		context.Cards.Add(card);
		Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
		Array.Sort(ids);
		foreach (Guid id in ids)
		{
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			receipt.Id = id;
			receipt.Location = "Shared location";
			receipt.Date = new(2025, 3, 1);
			context.Receipts.Add(receipt);
			TransactionEntity payment = TransactionEntityGenerator.Generate(id, account.Id, card.Id);
			payment.Amount = 10m;
			if (independentlyDeletedPayment && id == ids[0])
			{
				payment.DeletedAt = DateTimeOffset.UtcNow;
			}
			context.Transactions.Add(payment);
		}
		await context.SaveChangesAsync();
		AcceptedDuplicatePairEntity ab = new() { Id = Guid.NewGuid(), ReceiptIdA = ids[0], ReceiptIdB = ids[1], AcceptedAt = DateTimeOffset.UtcNow };
		AcceptedDuplicatePairEntity bc = new() { Id = Guid.NewGuid(), ReceiptIdA = ids[1], ReceiptIdB = ids[2], AcceptedAt = DateTimeOffset.UtcNow };
		context.AcceptedDuplicatePairs.AddRange(ab, bc);
		await context.SaveChangesAsync();
		return new(ids[0], ids[1], ids[2], account.Id, card.Id, ab.Id, bc.Id);
	}

	private sealed record Seeded(Guid A, Guid B, Guid C, Guid AccountId, Guid CardId, Guid ChangedPair, Guid UnrelatedPair);

	private sealed class RejectAuditInsert : DbCommandInterceptor
	{
		public bool Fired { get; private set; }
		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
			CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("INSERT INTO audit.\"AuditLogs\"", StringComparison.Ordinal)
				|| command.CommandText.Contains("INSERT INTO \"audit\".\"AuditLogs\"", StringComparison.Ordinal))
			{
				Fired = true;
				throw new InvalidOperationException("Injected audit insert failure");
			}
			return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
		}
	}
}
