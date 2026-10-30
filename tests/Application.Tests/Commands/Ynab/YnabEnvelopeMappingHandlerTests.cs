using Application.Commands.Ynab.EnvelopeMapping;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using Domain.Envelopes;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class YnabEnvelopeMappingHandlerTests
{
	private const string SelectedBudget = "11111111-1111-1111-1111-111111111111";
	private readonly Mock<IYnabEnvelopeMappingService> _service = new();
	private readonly Mock<IYnabBudgetSelectionService> _selection = new();

	private void SelectBudget(string? budgetId)
		=> _selection.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(budgetId);

	[Fact]
	public async Task Set_SelectedBudget_PassesEveryFieldToService()
	{
		// Arrange
		SelectBudget(SelectedBudget);
		Guid envelopeId = Guid.NewGuid();
		YnabEnvelopeMappingDto expected = new(Guid.NewGuid(), envelopeId, "Groceries", "cat-1", "Groceries", "Needs",
			SelectedBudget, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
		_service.Setup(s => s.SetAsync(envelopeId, SelectedBudget, "cat-1", "Groceries", "Needs", It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);
		SetYnabEnvelopeMappingCommandHandler handler = new(_service.Object, _selection.Object);

		// Act
		YnabEnvelopeMappingDto actual = await handler.Handle(
			new(envelopeId, SelectedBudget, "cat-1", "Groceries", "Needs"), CancellationToken.None);

		// Assert
		actual.Should().Be(expected);
	}

	[Fact]
	public async Task Set_NonCanonicalBudgetSpelling_IsCanonicalizedBeforeComparison()
	{
		// Arrange
		SelectBudget(SelectedBudget);
		Guid envelopeId = Guid.NewGuid();
		SetYnabEnvelopeMappingCommandHandler handler = new(_service.Object, _selection.Object);

		// Act
		await handler.Handle(new(envelopeId, "{" + SelectedBudget.ToUpperInvariant() + "}", "cat-1", "Groceries", "Needs"),
			CancellationToken.None);

		// Assert
		_service.Verify(s => s.SetAsync(envelopeId, SelectedBudget, "cat-1", "Groceries", "Needs", It.IsAny<CancellationToken>()), Times.Once);
	}

	[Theory]
	[InlineData("22222222-2222-2222-2222-222222222222")]
	[InlineData(null)]
	public async Task Set_BudgetIsNotTheSelectedBudget_ThrowsWithoutWriting(string? selected)
	{
		// Arrange
		SelectBudget(selected);
		SetYnabEnvelopeMappingCommandHandler handler = new(_service.Object, _selection.Object);

		// Act
		Func<Task> act = async () => await handler.Handle(
			new(Guid.NewGuid(), SelectedBudget, "cat-1", "Groceries", "Needs"), CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*selected YNAB budget*");
		_service.Verify(s => s.SetAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
			It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Delete_SelectedBudget_DeletesInThatBudget(bool expected)
	{
		// Arrange
		SelectBudget(SelectedBudget);
		Guid envelopeId = Guid.NewGuid();
		_service.Setup(s => s.DeleteAsync(envelopeId, SelectedBudget, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		bool actual = await new DeleteYnabEnvelopeMappingCommandHandler(_service.Object, _selection.Object)
			.Handle(new(envelopeId), CancellationToken.None);

		// Assert
		actual.Should().Be(expected);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public async Task Delete_NoSelectedBudget_ReturnsFalseWithoutDeleting(string? selected)
	{
		// Arrange
		SelectBudget(selected);

		// Act
		bool actual = await new DeleteYnabEnvelopeMappingCommandHandler(_service.Object, _selection.Object)
			.Handle(new(Guid.NewGuid()), CancellationToken.None);

		// Assert
		actual.Should().BeFalse();
		_service.Verify(s => s.DeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task GetMappings_SelectedBudget_ReturnsThatBudgetsMappings()
	{
		// Arrange
		SelectBudget(SelectedBudget);
		List<YnabEnvelopeMappingDto> expected = [new(Guid.NewGuid(), Guid.NewGuid(), "Groceries", "cat-1", "Groceries", "Needs",
			SelectedBudget, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)];
		_service.Setup(s => s.GetByBudgetIdAsync(SelectedBudget, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		List<YnabEnvelopeMappingDto> actual = await new GetYnabEnvelopeMappingsQueryHandler(_service.Object, _selection.Object)
			.Handle(new(), CancellationToken.None);

		// Assert
		actual.Should().BeSameAs(expected);
	}

	[Fact]
	public async Task GetMappings_NoSelectedBudget_ReturnsEmpty()
	{
		// Arrange
		SelectBudget(null);

		// Act
		List<YnabEnvelopeMappingDto> actual = await new GetYnabEnvelopeMappingsQueryHandler(_service.Object, _selection.Object)
			.Handle(new(), CancellationToken.None);

		// Assert
		actual.Should().BeEmpty();
		_service.Verify(s => s.GetByBudgetIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task GetUnmapped_SelectedBudget_ReturnsThatBudgetsUnmappedEnvelopes()
	{
		// Arrange
		SelectBudget(SelectedBudget);
		List<Envelope> expected = [new(Guid.NewGuid(), "Gifts")];
		_service.Setup(s => s.GetUnmappedEnvelopesAsync(SelectedBudget, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

		// Act
		List<Envelope> actual = await new GetUnmappedEnvelopesQueryHandler(_service.Object, _selection.Object)
			.Handle(new(), CancellationToken.None);

		// Assert
		actual.Should().BeSameAs(expected);
	}

	[Fact]
	public async Task GetUnmapped_NoSelectedBudget_ReturnsEmpty()
	{
		// Arrange
		SelectBudget(null);

		// Act
		List<Envelope> actual = await new GetUnmappedEnvelopesQueryHandler(_service.Object, _selection.Object)
			.Handle(new(), CancellationToken.None);

		// Assert
		actual.Should().BeEmpty();
	}
}
