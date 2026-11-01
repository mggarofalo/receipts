using Application.Envelopes;
using FluentAssertions;

namespace Application.Tests.Envelopes;

public class EnvelopeBaselineTests
{
	private static readonly Guid ItemA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
	private static readonly Guid ItemB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
	private static readonly Guid ItemC = Guid.Parse("00000000-0000-0000-0000-00000000000c");
	private static readonly Guid Food = Guid.NewGuid();
	private static readonly Guid Fuel = Guid.NewGuid();

	[Fact]
	public void Routes_GivesEveryItemOneRoutePerLinkedBudget_NullWhenUnlinkedOrWithoutAnEnvelope()
	{
		// Arrange
		Dictionary<(Guid EnvelopeId, string BudgetId), string> links = new()
		{
			[(Food, "budget-b")] = "ynab-food-b",
			[(Food, "budget-a")] = "ynab-food-a",
			[(Fuel, "budget-a")] = "ynab-fuel-a",
		};

		// Act
		IReadOnlyList<EnvelopeYnabRoute> routes = EnvelopeBaseline.Routes(
			[(ItemC, null), (ItemB, Fuel), (ItemA, Food)], links);

		// Assert — ordered by item, then budget
		routes.Should().Equal(
			new EnvelopeYnabRoute(ItemA, "budget-a", "ynab-food-a"),
			new EnvelopeYnabRoute(ItemA, "budget-b", "ynab-food-b"),
			new EnvelopeYnabRoute(ItemB, "budget-a", "ynab-fuel-a"),
			new EnvelopeYnabRoute(ItemB, "budget-b", null),
			new EnvelopeYnabRoute(ItemC, "budget-a", null),
			new EnvelopeYnabRoute(ItemC, "budget-b", null));
	}

	[Fact]
	public void Routes_WithoutAnyLinkedEnvelope_IsEmpty()
	{
		EnvelopeBaseline.Routes([(ItemA, Food)], new Dictionary<(Guid, string), string>()).Should().BeEmpty();
	}

	[Fact]
	public void Compare_IdenticalRoutes_AreUnchangedAndHold()
	{
		// Arrange
		EnvelopeYnabRoute[] routes =
		[
			new(ItemA, "budget-a", "food"),
			new(ItemA, "budget-b", null),
			new(ItemB, "budget-a", "fuel"),
		];

		// Act
		EnvelopeBaselineReport report = EnvelopeBaseline.Compare(routes, [.. routes.Reverse()]);

		// Assert
		report.Should().Be(new EnvelopeBaselineReport(2, 2, 0, 0, 0));
		report.Holds.Should().BeTrue();
	}

	[Fact]
	public void Compare_ADifferentCategoryInAnyBudget_ChangesTheItem()
	{
		// Act
		EnvelopeBaselineReport report = EnvelopeBaseline.Compare(
			[new(ItemA, "budget-a", "food"), new(ItemA, "budget-b", "food-b")],
			[new(ItemA, "budget-a", "food"), new(ItemA, "budget-b", "other-b")]);

		// Assert
		report.Should().Be(new EnvelopeBaselineReport(1, 0, 1, 0, 0));
		report.Holds.Should().BeFalse();
	}

	[Fact]
	public void Compare_MappedBecomingUnmapped_IsAChange()
	{
		EnvelopeBaselineReport report = EnvelopeBaseline.Compare(
			[new(ItemA, "budget-a", "food")],
			[new(ItemA, "budget-a", null)]);

		report.Changed.Should().Be(1);
		report.Holds.Should().BeFalse();
	}

	[Fact]
	public void Compare_UnmappedBecomingMapped_IsAChange()
	{
		EnvelopeBaselineReport report = EnvelopeBaseline.Compare(
			[new(ItemA, "budget-a", null)],
			[new(ItemA, "budget-a", "food")]);

		report.Changed.Should().Be(1);
		report.Holds.Should().BeFalse();
	}

	[Fact]
	public void Compare_ABudgetMappedOnlyInTheBaseline_IsAChange()
	{
		// The current side no longer lists budget-b at all: it counts as unmapped there.
		EnvelopeBaselineReport report = EnvelopeBaseline.Compare(
			[new(ItemA, "budget-a", "food"), new(ItemA, "budget-b", "food-b")],
			[new(ItemA, "budget-a", "food")]);

		report.Should().Be(new EnvelopeBaselineReport(1, 0, 1, 0, 0));
	}

	[Fact]
	public void Compare_ABudgetMappedOnlyNow_IsAChange()
	{
		EnvelopeBaselineReport report = EnvelopeBaseline.Compare(
			[new(ItemA, "budget-a", "food")],
			[new(ItemA, "budget-a", "food"), new(ItemA, "budget-b", "food-b")]);

		report.Should().Be(new EnvelopeBaselineReport(1, 0, 1, 0, 0));
	}

	[Fact]
	public void Compare_ABudgetUnmappedOnBothSidesButListedOnlyOnOne_IsUnchanged()
	{
		EnvelopeBaselineReport report = EnvelopeBaseline.Compare(
			[new(ItemA, "budget-a", "food"), new(ItemA, "budget-b", null)],
			[new(ItemA, "budget-a", "food")]);

		report.Should().Be(new EnvelopeBaselineReport(1, 1, 0, 0, 0));
		report.Holds.Should().BeTrue();
	}

	[Fact]
	public void Compare_CategoryIdsAreCaseSensitive()
	{
		EnvelopeBaselineReport report = EnvelopeBaseline.Compare(
			[new(ItemA, "budget-a", "food")],
			[new(ItemA, "budget-a", "FOOD")]);

		report.Changed.Should().Be(1);
	}

	[Fact]
	public void Compare_AnItemAddedSinceTheBaseline_IsCountedAndStillHolds()
	{
		// Act
		EnvelopeBaselineReport report = EnvelopeBaseline.Compare(
			[new(ItemA, "budget-a", "food")],
			[new(ItemA, "budget-a", "food"), new(ItemB, "budget-a", "fuel")]);

		// Assert
		report.Should().Be(new EnvelopeBaselineReport(2, 1, 0, 1, 0));
		report.Holds.Should().BeTrue("new purchases do not break parity");
	}

	[Fact]
	public void Compare_AnItemMissingSinceTheBaseline_IsRemovedAndDoesNotHold()
	{
		// Act
		EnvelopeBaselineReport report = EnvelopeBaseline.Compare(
			[new(ItemA, "budget-a", "food"), new(ItemB, "budget-a", "fuel")],
			[new(ItemA, "budget-a", "food")]);

		// Assert
		report.Should().Be(new EnvelopeBaselineReport(1, 1, 0, 0, 1));
		report.Holds.Should().BeFalse();
	}

	[Fact]
	public void Compare_MixedOutcomes_AreCountedPerItem()
	{
		// Act
		EnvelopeBaselineReport report = EnvelopeBaseline.Compare(
			[new(ItemA, "budget-a", "food"), new(ItemB, "budget-a", "fuel"), new(ItemC, "budget-a", "gifts")],
			[new(ItemA, "budget-a", "food"), new(ItemB, "budget-a", "food"), new(Guid.NewGuid(), "budget-a", null)]);

		// Assert
		report.Should().Be(new EnvelopeBaselineReport(3, 1, 1, 1, 1));
		report.Holds.Should().BeFalse();
	}

	[Theory]
	[InlineData(0, 0, true)]
	[InlineData(1, 0, false)]
	[InlineData(0, 1, false)]
	public void Holds_OnlyWithoutChangedOrRemovedItems(int changed, int removed, bool holds)
	{
		new EnvelopeBaselineReport(5, 5 - changed, changed, 3, removed).Holds.Should().Be(holds);
	}
}
