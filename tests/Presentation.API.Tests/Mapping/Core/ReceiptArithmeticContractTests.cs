using API.Generated.Dtos;
using API.Mapping.Core;
using Application.Commands.Receipt.CreateComplete;
using Application.Interfaces.Services;
using Common;
using Domain;
using Domain.Aggregates;
using Domain.Core;
using FluentAssertions;
using FluentValidation;
using Moq;
using SampleData;

namespace Presentation.API.Tests.Mapping.Core;

public class ReceiptArithmeticContractTests
{
	public static TheoryData<string> VectorIds
	{
		get { TheoryData<string> result = []; foreach (var vector in ReceiptArithmeticVectors.Cases) { result.Add(vector.Id); } return result; }
	}

	[Theory]
	[MemberData(nameof(VectorIds))]
	public async Task SharedLiterals_MatchActualApiMappingAggregateAndCreationPolicy(string id)
	{
		ReceiptArithmeticVector vector = ReceiptArithmeticVectors.Find(id);
		ReceiptItemMapper itemMapper = new();
		List<ReceiptItem> items = [];
		foreach (var line in vector.Lines)
		{
			ReceiptItem created = itemMapper.ToDomain(new CreateReceiptItemRequest { Description = "Vector item", Category = "Food", Quantity = line.Quantity, UnitPrice = line.UnitPrice });
			ReceiptItem updated = itemMapper.ToDomain(new UpdateReceiptItemRequest { Id = Guid.NewGuid(), Description = "Vector item", Category = "Food", Quantity = line.Quantity, UnitPrice = line.UnitPrice });
			created.TotalAmount.Amount.Should().Be(line.Total);
			updated.TotalAmount.Amount.Should().Be(line.Total);
			items.Add(created);
		}
		Receipt receipt = new ReceiptMapper().ToDomain(new CreateReceiptRequest { Location = id, Date = new DateOnly(2024, 1, 1), TaxAmount = vector.Tax });
		List<Adjustment> adjustments = [.. vector.Adjustments.Select(amount => new AdjustmentMapper().ToDomain(new CreateAdjustmentRequest { Type = amount < 0 ? "Discount" : "Other", Description = "Vector adjustment", Amount = amount }))];
		Guid card = Guid.NewGuid();
		List<Transaction> transactions = [.. vector.Payments.Select(amount => new TransactionMapper().ToDomain(new CreateTransactionRequest { CardId = card, Date = receipt.Date, Amount = amount }))];
		ReceiptWithItems aggregate = new() { Receipt = receipt, Items = items, Adjustments = adjustments };
		Trip trip = new() { Receipt = aggregate, Transactions = [.. transactions.Select(transaction => new TransactionAccount { Transaction = transaction, Account = new Account(Guid.NewGuid(), "Vector account") })] };
		aggregate.Subtotal.Amount.Should().Be(vector.Subtotal);
		aggregate.AdjustmentTotal.Amount.Should().Be(vector.AdjustmentTotal);
		aggregate.ExpectedTotal.Amount.Should().Be(vector.ExpectedTotal);
		trip.TransactionTotal.Amount.Should().Be(vector.PaymentTotal);
		trip.Validate().Should().HaveCount(vector.IsWithinCreationTolerance ? 0 : 1);
		Mock<ICompleteReceiptWriter> service = new();
		CreateCompleteReceiptResult saved = new(receipt, transactions, items, adjustments);
		service.Setup(instance => instance.CreateAsync(receipt, transactions, items, adjustments, It.IsAny<CancellationToken>())).ReturnsAsync(saved);
		CreateCompleteReceiptCommandHandler handler = new(service.Object);
		Func<Task> create = () => handler.Handle(new(receipt, transactions, items, adjustments), CancellationToken.None).AsTask();
		if (vector.IsWithinCreationTolerance)
		{
			await create.Should().NotThrowAsync();
		}
		else
		{
			await create.Should().ThrowAsync<ValidationException>();
		}

		service.Verify(instance => instance.CreateAsync(receipt, transactions, items, adjustments, It.IsAny<CancellationToken>()), vector.IsWithinCreationTolerance ? Times.Once : Times.Never);
	}

	[Fact]
	public async Task HistoricalStoredLineTotalsAndNoTransactionReceipts_KeepExistingServerAuthority()
	{
		Receipt receipt = new(Guid.NewGuid(), "Historical", new DateOnly(2024, 1, 1), Money.Zero);
		List<ReceiptItem> items = [new(Guid.NewGuid(), null, "Historical line", 0.5m, new Money(2.01m), new Money(1), "Food", null), new(Guid.NewGuid(), null, "Historical line", 0.5m, new Money(2.01m), new Money(1), "Food", null)];
		ReceiptWithItems aggregate = new() { Receipt = receipt, Items = items, Adjustments = [] };
		aggregate.Subtotal.Amount.Should().Be(2m, "persisted line totals remain authoritative even when a fresh API mapping would round each to1.01");
		new Trip { Receipt = aggregate, Transactions = [] }.Validate().Should().BeEmpty();
		Mock<ICompleteReceiptWriter> service = new();
		service.Setup(instance => instance.CreateAsync(receipt, It.IsAny<List<Transaction>>(), items, It.IsAny<List<Adjustment>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new CreateCompleteReceiptResult(receipt, [], items, []));
		await new CreateCompleteReceiptCommandHandler(service.Object).Handle(new(receipt, [], items, []), CancellationToken.None);
		service.Verify(instance => instance.CreateAsync(receipt, It.Is<List<Transaction>>(rows => rows.Count == 0), items, It.IsAny<List<Adjustment>>(), It.IsAny<CancellationToken>()), Times.Once);
	}
}
