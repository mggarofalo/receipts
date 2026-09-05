using FluentAssertions;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Audit;

public class SaveChangesContractTests
{
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(5)]
	public async Task EveryOverload_CapturesAuditWithoutInflatingCallerRowCount(int overload)
	{
		await using ApplicationDbContext context = Context();
		ReceiptEntity receipt = new() { Id = Guid.NewGuid(), Location = "Store", Date = new(2024, 1, 1) };
		AuditLogEntity semantic = new() { Id = Guid.NewGuid(), EntityType = "Receipt", EntityId = receipt.Id.ToString(), Action = AuditAction.Merge, ChangedAt = DateTimeOffset.UtcNow };
		context.Receipts.Add(receipt);
		context.AuditLogs.Add(semantic);

		int affected = overload switch
		{
			0 => context.SaveChanges(),
			1 => context.SaveChanges(true),
			2 => context.SaveChanges(false),
			3 => await context.SaveChangesAsync(),
			4 => await context.SaveChangesAsync(true),
			5 => await context.SaveChangesAsync(false),
			_ => throw new ArgumentOutOfRangeException(nameof(overload))
		};

		affected.Should().Be(2);
		bool accepts = overload is not (2 or 5);
		context.Entry(receipt).State.Should().Be(accepts ? EntityState.Unchanged : EntityState.Added);
		context.Entry(semantic).State.Should().Be(accepts ? EntityState.Unchanged : EntityState.Added);
		context.ChangeTracker.Entries<AuditLogEntity>().Count().Should().Be(accepts ? 2 : 1);
		List<AuditLogEntity> persisted = await context.AuditLogs.AsNoTracking().ToListAsync();
		persisted.Should().HaveCount(2);
		persisted.Should().ContainSingle(row => row.Action == AuditAction.Create && row.EntityId == receipt.Id.ToString());
		context.ChangeTracker.AcceptAllChanges();
		(await context.SaveChangesAsync()).Should().Be(0);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SaveFalse_SoftDeleteRetainsPendingOriginalValues_AndPersistsDeleteAudit(bool synchronous)
	{
		await using ApplicationDbContext context = Context();
		ReceiptEntity receipt = new() { Id = Guid.NewGuid(), Location = "Store", Date = new(2024, 1, 1) };
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();
		context.Receipts.Remove(receipt);

		int affected = synchronous ? context.SaveChanges(false) : await context.SaveChangesAsync(false);

		affected.Should().Be(1);
		context.Entry(receipt).State.Should().Be(EntityState.Modified);
		context.Entry(receipt).Property(row => row.DeletedAt).OriginalValue.Should().BeNull();
		receipt.DeletedAt.Should().NotBeNull();
		(await context.Receipts.AsNoTracking().AnyAsync()).Should().BeFalse();
		(await context.AuditLogs.AsNoTracking().CountAsync(row => row.Action == AuditAction.Delete)).Should().Be(1);
		context.ChangeTracker.AcceptAllChanges();
		(await context.SaveChangesAsync()).Should().Be(0);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SaveAsync_PreCancelledToken_LeavesBusinessAndAuditUnwritten(bool explicitAcceptance)
	{
		await using ApplicationDbContext context = Context();
		context.Receipts.Add(new() { Id = Guid.NewGuid(), Location = "Store", Date = new(2024, 1, 1) });
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();
		Func<Task> save = explicitAcceptance ? () => context.SaveChangesAsync(false, cancellation.Token) : () => context.SaveChangesAsync(cancellation.Token);
		await save.Should().ThrowAsync<OperationCanceledException>();
		(await context.Receipts.AsNoTracking().AnyAsync()).Should().BeFalse();
		(await context.AuditLogs.AsNoTracking().AnyAsync()).Should().BeFalse();
		context.ChangeTracker.Entries<AuditLogEntity>().Should().BeEmpty();
	}

	private static ApplicationDbContext Context() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase($"save-contract-{Guid.NewGuid():N}").Options);
}
