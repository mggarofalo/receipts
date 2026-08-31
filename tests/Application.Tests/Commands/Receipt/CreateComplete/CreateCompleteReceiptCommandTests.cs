using Application.Commands.Receipt.CreateComplete;
using FluentAssertions;
using SampleData.Domain.Core;

namespace Application.Tests.Commands.Receipt.CreateComplete;

public class CreateCompleteReceiptCommandTests
{
	[Fact]
	public void Command_WithNullReceipt_ThrowsArgumentNullException()
	{
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
		Domain.Core.Receipt receipt = null;
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning disable CS8604 // Possible null reference argument.
		Assert.Throws<ArgumentNullException>(() =>
			new CreateCompleteReceiptCommand(receipt, [], [], []));
#pragma warning restore CS8604 // Possible null reference argument.
	}

	[Fact]
	public void Command_WithNullTransactions_ThrowsArgumentNullException()
	{
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
		List<Domain.Core.Transaction> transactions = null;
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
		Domain.Core.Receipt receipt = ReceiptGenerator.Generate();
#pragma warning disable CS8604 // Possible null reference argument.
		Assert.Throws<ArgumentNullException>(() =>
			new CreateCompleteReceiptCommand(receipt, transactions, [], []));
#pragma warning restore CS8604 // Possible null reference argument.
	}

	[Fact]
	public void Command_WithNullItems_ThrowsArgumentNullException()
	{
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
		List<Domain.Core.ReceiptItem> items = null;
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
		Domain.Core.Receipt receipt = ReceiptGenerator.Generate();
#pragma warning disable CS8604 // Possible null reference argument.
		Assert.Throws<ArgumentNullException>(() =>
			new CreateCompleteReceiptCommand(receipt, [], items, []));
#pragma warning restore CS8604 // Possible null reference argument.
	}

	[Fact]
	public void Command_WithNullAdjustments_NormalizesToEmptyCollection()
	{
		Domain.Core.Receipt receipt = ReceiptGenerator.Generate();
		List<Domain.Core.Adjustment> adjustments = null!;

		CreateCompleteReceiptCommand command = new(receipt, [], [], adjustments);

		command.Adjustments.Should().BeEmpty();
	}

	[Fact]
	public void Command_WithAdjustments_ExposesImmutableAdjustments()
	{
		Domain.Core.Receipt receipt = ReceiptGenerator.Generate();
		List<Domain.Core.Adjustment> adjustments =
		[
			new(Guid.NewGuid(), Common.AdjustmentType.Discount, new Domain.Money(-2.50m))
		];

		CreateCompleteReceiptCommand command = new(receipt, [], [], adjustments);

		command.Adjustments.Should().BeEquivalentTo(adjustments);
		command.Adjustments.Should().BeAssignableTo<IReadOnlyList<Domain.Core.Adjustment>>();
	}

	[Fact]
	public void Command_WithValidData_SetsProperties()
	{
		Domain.Core.Receipt receipt = ReceiptGenerator.Generate();
		List<Domain.Core.Transaction> transactions = TransactionGenerator.GenerateList(1);
		List<Domain.Core.ReceiptItem> items = ReceiptItemGenerator.GenerateList(1);

		CreateCompleteReceiptCommand command = new(receipt, transactions, items, []);

		command.Receipt.Should().BeSameAs(receipt);
		command.Transactions.Should().BeEquivalentTo(transactions);
		command.Items.Should().BeEquivalentTo(items);
	}

	[Fact]
	public void Command_WithEmptyTransactionsAndItems_Succeeds()
	{
		Domain.Core.Receipt receipt = ReceiptGenerator.Generate();

		CreateCompleteReceiptCommand command = new(receipt, [], [], []);

		command.Receipt.Should().BeSameAs(receipt);
		command.Transactions.Should().BeEmpty();
		command.Items.Should().BeEmpty();
	}

	[Fact]
	public void Transactions_ShouldBeImmutable()
	{
		Domain.Core.Receipt receipt = ReceiptGenerator.Generate();
		List<Domain.Core.Transaction> transactions = TransactionGenerator.GenerateList(2);

		CreateCompleteReceiptCommand command = new(receipt, transactions, [], []);

		Assert.IsAssignableFrom<IReadOnlyList<Domain.Core.Transaction>>(command.Transactions);
	}

	[Fact]
	public void Items_ShouldBeImmutable()
	{
		Domain.Core.Receipt receipt = ReceiptGenerator.Generate();
		List<Domain.Core.ReceiptItem> items = ReceiptItemGenerator.GenerateList(2);

		CreateCompleteReceiptCommand command = new(receipt, [], items, []);

		Assert.IsAssignableFrom<IReadOnlyList<Domain.Core.ReceiptItem>>(command.Items);
	}
}
