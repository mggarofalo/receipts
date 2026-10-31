using Application.Commands.Envelope.Create;
using Application.Commands.Envelope.Delete;
using Application.Commands.Envelope.Update;
using Application.Commands.ItemTypeEnvelopeDefault.Delete;
using Application.Commands.ItemTypeEnvelopeDefault.Set;
using Application.Commands.Occasion.Create;
using Application.Commands.Occasion.Delete;
using Application.Commands.Occasion.Update;
using Application.Commands.Receipt.BudgetChoices;
using Application.Commands.ReceiptItem.BudgetChoices;
using Application.Interfaces.Services;
using Application.Queries.Core.Envelope;
using Application.Queries.Core.ItemTypeEnvelopeDefault;
using Application.Queries.Core.Occasion;
using Domain.Envelopes;
using FluentAssertions;
using Moq;

namespace Application.Tests.Envelopes;

public class EnvelopeHandlerTests
{
	private readonly Mock<IEnvelopeService> _envelopes = new();
	private readonly Mock<IOccasionService> _occasions = new();
	private readonly Mock<IItemTypeEnvelopeDefaultService> _defaults = new();
	private readonly Mock<IBudgetChoiceService> _budgetChoices = new();

	[Fact]
	public async Task CreateEnvelope_ReturnsCreatedEnvelope()
	{
		// Arrange
		Envelope request = new(Guid.Empty, "Groceries");
		Envelope expected = new(Guid.NewGuid(), "Groceries");
		_envelopes.Setup(s => s.CreateAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		Envelope actual = await new CreateEnvelopeCommandHandler(_envelopes.Object).Handle(new(request), CancellationToken.None);

		// Assert
		actual.Should().BeSameAs(expected);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task UpdateEnvelope_ReturnsWhetherItExisted(bool expected)
	{
		// Arrange
		Envelope envelope = new(Guid.NewGuid(), "Groceries", false);
		_envelopes.Setup(s => s.UpdateAsync(envelope, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		bool actual = await new UpdateEnvelopeCommandHandler(_envelopes.Object).Handle(new(envelope), CancellationToken.None);

		// Assert
		actual.Should().Be(expected);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task DeleteEnvelope_ReturnsWhetherItWasDeleted(bool expected)
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_envelopes.Setup(s => s.DeleteAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		bool actual = await new DeleteEnvelopeCommandHandler(_envelopes.Object).Handle(new(id), CancellationToken.None);

		// Assert
		actual.Should().Be(expected);
	}

	[Theory]
	[InlineData(null)]
	[InlineData(true)]
	[InlineData(false)]
	public async Task GetAllEnvelopes_PassesActiveFilter(bool? isActive)
	{
		// Arrange
		List<Envelope> expected = [new(Guid.NewGuid(), "Groceries")];
		_envelopes.Setup(s => s.GetAllAsync(isActive, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		List<Envelope> actual = await new GetAllEnvelopesQueryHandler(_envelopes.Object).Handle(new(isActive), CancellationToken.None);

		// Assert
		actual.Should().BeSameAs(expected);
	}

	[Fact]
	public async Task GetEnvelopeById_ReturnsServiceResult()
	{
		// Arrange
		Envelope expected = new(Guid.NewGuid(), "Groceries");
		_envelopes.Setup(s => s.GetByIdAsync(expected.Id, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		Envelope? actual = await new GetEnvelopeByIdQueryHandler(_envelopes.Object).Handle(new(expected.Id), CancellationToken.None);

		// Assert
		actual.Should().BeSameAs(expected);
	}

	[Fact]
	public async Task CreateOccasion_ReturnsCreatedOccasion()
	{
		// Arrange
		Occasion request = new(Guid.Empty, "Birthday");
		Occasion expected = new(Guid.NewGuid(), "Birthday");
		_occasions.Setup(s => s.CreateAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		Occasion actual = await new CreateOccasionCommandHandler(_occasions.Object).Handle(new(request), CancellationToken.None);

		// Assert
		actual.Should().BeSameAs(expected);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task UpdateOccasion_ReturnsWhetherItExisted(bool expected)
	{
		// Arrange
		Occasion occasion = new(Guid.NewGuid(), "Birthday");
		_occasions.Setup(s => s.UpdateAsync(occasion, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		bool actual = await new UpdateOccasionCommandHandler(_occasions.Object).Handle(new(occasion), CancellationToken.None);

		// Assert
		actual.Should().Be(expected);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task DeleteOccasion_ReturnsWhetherItWasDeleted(bool expected)
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_occasions.Setup(s => s.DeleteAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		bool actual = await new DeleteOccasionCommandHandler(_occasions.Object).Handle(new(id), CancellationToken.None);

		// Assert
		actual.Should().Be(expected);
	}

	[Fact]
	public async Task GetAllOccasions_PassesActiveFilter()
	{
		// Arrange
		List<Occasion> expected = [new(Guid.NewGuid(), "Birthday")];
		_occasions.Setup(s => s.GetAllAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		List<Occasion> actual = await new GetAllOccasionsQueryHandler(_occasions.Object).Handle(new(true), CancellationToken.None);

		// Assert
		actual.Should().BeSameAs(expected);
	}

	[Fact]
	public async Task GetOccasionById_ReturnsServiceResult()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_occasions.Setup(s => s.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((Occasion?)null);

		// Act
		Occasion? actual = await new GetOccasionByIdQueryHandler(_occasions.Object).Handle(new(id), CancellationToken.None);

		// Assert
		actual.Should().BeNull();
	}

	[Fact]
	public async Task SetItemTypeDefault_PassesCategoryAndEnvelopeInOrder()
	{
		// Arrange
		Guid categoryId = Guid.NewGuid(), envelopeId = Guid.NewGuid();
		ItemTypeEnvelopeDefault expected = new(categoryId, envelopeId, DateTimeOffset.UtcNow);
		_defaults.Setup(s => s.SetAsync(categoryId, envelopeId, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		ItemTypeEnvelopeDefault actual = await new SetItemTypeEnvelopeDefaultCommandHandler(_defaults.Object)
			.Handle(new(categoryId, envelopeId), CancellationToken.None);

		// Assert
		actual.Should().Be(expected);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task DeleteItemTypeDefault_ReturnsWhetherItWasDeleted(bool expected)
	{
		// Arrange
		Guid categoryId = Guid.NewGuid();
		_defaults.Setup(s => s.DeleteAsync(categoryId, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		bool actual = await new DeleteItemTypeEnvelopeDefaultCommandHandler(_defaults.Object).Handle(new(categoryId), CancellationToken.None);

		// Assert
		actual.Should().Be(expected);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SetReceiptBudgetChoices_PassesChoicesAndReturnsWhetherItExisted(bool expected)
	{
		// Arrange
		Guid receiptId = Guid.NewGuid(), paidFrom = Guid.NewGuid(), occasion = Guid.NewGuid();
		_budgetChoices.Setup(s => s.SetReceiptChoicesAsync(receiptId, paidFrom, occasion, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		bool actual = await new SetReceiptBudgetChoicesCommandHandler(_budgetChoices.Object)
			.Handle(new(receiptId, paidFrom, occasion), CancellationToken.None);

		// Assert
		actual.Should().Be(expected);
		_budgetChoices.Verify(s => s.SetReceiptChoicesAsync(receiptId, paidFrom, occasion, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task SetReceiptBudgetChoices_PassesNullsThrough()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		_budgetChoices.Setup(s => s.SetReceiptChoicesAsync(receiptId, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(true);

		// Act
		bool actual = await new SetReceiptBudgetChoicesCommandHandler(_budgetChoices.Object)
			.Handle(new(receiptId, null, null), CancellationToken.None);

		// Assert
		actual.Should().BeTrue();
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SetReceiptItemBudgetChoices_PassesChoicesAndReturnsWhetherItExisted(bool expected)
	{
		// Arrange
		Guid itemId = Guid.NewGuid(), envelope = Guid.NewGuid(), occasion = Guid.NewGuid();
		_budgetChoices.Setup(s => s.SetItemChoicesAsync(itemId, envelope, occasion, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		bool actual = await new SetReceiptItemBudgetChoicesCommandHandler(_budgetChoices.Object)
			.Handle(new(itemId, envelope, occasion), CancellationToken.None);

		// Assert
		actual.Should().Be(expected);
		_budgetChoices.Verify(s => s.SetItemChoicesAsync(itemId, envelope, occasion, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task SetReceiptItemBudgetChoices_PassesNullsThrough()
	{
		// Arrange
		Guid itemId = Guid.NewGuid();
		_budgetChoices.Setup(s => s.SetItemChoicesAsync(itemId, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(true);

		// Act
		bool actual = await new SetReceiptItemBudgetChoicesCommandHandler(_budgetChoices.Object)
			.Handle(new(itemId, null, null), CancellationToken.None);

		// Assert
		actual.Should().BeTrue();
	}

	[Fact]
	public async Task GetItemTypeDefaults_ReturnsServiceResult()
	{
		// Arrange
		List<ItemTypeEnvelopeDefault> expected = [new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow)];
		_defaults.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		List<ItemTypeEnvelopeDefault> actual = await new GetItemTypeEnvelopeDefaultsQueryHandler(_defaults.Object)
			.Handle(new(), CancellationToken.None);

		// Assert
		actual.Should().BeSameAs(expected);
	}
}
