using Domain;
using Domain.Core;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;

namespace Infrastructure.Tests.Mapping;

public class TransactionMapperTests
{
	[Fact]
	public void Read_DerivesAccountFromOriginatingCard()
	{
		Guid account = Guid.NewGuid(), card = Guid.NewGuid();
		TransactionEntity source = new() { Id = Guid.NewGuid(), ReceiptId = Guid.NewGuid(), CardId = card, Amount = 10, Date = new(2025, 1, 1), Card = new() { Id = card, AccountId = account } };
		Transaction result = new TransactionMapper().ToDomain(source);
		result.AccountId.Should().Be(account);
		result.CardId.Should().Be(card);
		result.ReceiptId.Should().Be(source.ReceiptId);
	}

	[Fact]
	public void Read_WithoutCard_FailsInsteadOfInventingAnAccount()
	{
		TransactionEntity source = new() { CardId = Guid.NewGuid(), Amount = 10, Date = new(2025, 1, 1) };
		Action read = () => new TransactionMapper().ToDomain(source);
		read.Should().Throw<InvalidOperationException>().WithMessage("Transaction reads must include the originating card.");
	}

	[Fact]
	public void Write_IgnoresDerivedAccountAndRetainsOriginatingCard()
	{
		Transaction source = new(Guid.NewGuid(), Guid.NewGuid(), new Money(10), new(2025, 1, 1)) { AccountId = Guid.NewGuid() };
		TransactionEntity result = new TransactionMapper().ToEntity(source);
		result.CardId.Should().Be(source.CardId);
		result.Card.Should().BeNull("a read-only account value must never construct or reparent a card");
		result.Amount.Should().Be(10);
	}
}
