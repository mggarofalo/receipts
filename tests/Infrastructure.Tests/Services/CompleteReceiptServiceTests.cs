using Application.Commands.Receipt.CreateComplete;
using Common;
using Domain;
using Domain.Core;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Infrastructure.Tests.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.Tests.Services;

public class CompleteReceiptServiceTests
{
	private sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
	{
		public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
			DbContextEventData eventData,
			InterceptionResult<int> result,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<InterceptionResult<int>>(new InvalidOperationException("Simulated persistence failure"));
	}

	[Fact]
	public async Task CreateAsync_PersistsAdjustmentWithSameReceiptAsEntireAggregate()
	{
		IDbContextFactory<ApplicationDbContext> factory = DbContextHelpers.CreateInMemoryContextFactory();
		CompleteReceiptService service = new(
			factory,
			new ReceiptMapper(),
			new TransactionMapper(),
			new ReceiptItemMapper(),
			new AdjustmentMapper());
		Receipt receipt = new(Guid.NewGuid(), "Store", new DateOnly(2026, 8, 31), new Money(0));
		ReceiptItem item = new(Guid.NewGuid(), null, "Item", 1, new Money(10), new Money(10), "Food", null);
		Adjustment adjustment = new(Guid.NewGuid(), AdjustmentType.Discount, new Money(-2));

		CreateCompleteReceiptResult result = await service.CreateAsync(receipt, [], [item], [adjustment], CancellationToken.None);

		result.Adjustments.Should().ContainSingle();
		result.Adjustments[0].ReceiptId.Should().Be(result.Receipt.Id);
		await using ApplicationDbContext context = await factory.CreateDbContextAsync();
		(await context.Set<ReceiptEntity>().CountAsync()).Should().Be(1);
		ReceiptItemEntity persistedItem = await context.Set<ReceiptItemEntity>().SingleAsync();
		AdjustmentEntity persistedAdjustment = await context.Set<AdjustmentEntity>().SingleAsync();
		persistedItem.ReceiptId.Should().Be(result.Receipt.Id);
		persistedAdjustment.ReceiptId.Should().Be(result.Receipt.Id);
		persistedAdjustment.Amount.Should().Be(-2m);
	}

	[Fact]
	public async Task CreateAsync_WhenSaveFails_PersistsNoneOfTheAggregate()
	{
		string databaseName = $"CompleteReceiptFailure_{Guid.NewGuid()}";
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase(databaseName)
			.AddInterceptors(new ThrowingSaveChangesInterceptor())
			.Options;
		IDbContextFactory<ApplicationDbContext> throwingFactory = new TestDbContextFactory(options);
		CompleteReceiptService service = new(
			throwingFactory,
			new ReceiptMapper(),
			new TransactionMapper(),
			new ReceiptItemMapper(),
			new AdjustmentMapper());
		Receipt receipt = new(Guid.NewGuid(), "Store", new DateOnly(2026, 8, 31), new Money(0));
		Transaction transaction = new(Guid.NewGuid(), Guid.NewGuid(), new Money(8), new DateOnly(2026, 8, 31));
		ReceiptItem item = new(Guid.NewGuid(), null, "Item", 1, new Money(10), new Money(10), "Food", null);
		Adjustment adjustment = new(Guid.NewGuid(), AdjustmentType.Discount, new Money(-2));

		Func<Task> act = () => service.CreateAsync(receipt, [transaction], [item], [adjustment], CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Simulated persistence failure");
		DbContextOptions<ApplicationDbContext> verificationOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase(databaseName)
			.Options;
		await using ApplicationDbContext context = new(verificationOptions);
		(await context.Set<ReceiptEntity>().CountAsync()).Should().Be(0);
		(await context.Set<TransactionEntity>().CountAsync()).Should().Be(0);
		(await context.Set<ReceiptItemEntity>().CountAsync()).Should().Be(0);
		(await context.Set<AdjustmentEntity>().CountAsync()).Should().Be(0);
	}
}
