using Application.Envelopes;
using Domain.Taxonomy;
using FluentAssertions;

namespace Application.Tests.Envelopes;

public class EnvelopeBackfillPlannerTests
{
	private const string BudgetA = "budget-a";
	private const string BudgetB = "budget-b";
	private static readonly Guid Revision = Guid.NewGuid();
	private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Plan_ManyRootsMappedToOneYnabCategory_ShareOneEnvelope()
	{
		// Arrange
		TaxonomyNode produce = Node("Produce"), bakery = Node("Bakery");
		EnvelopeBackfillItem first = Item(produce), second = Item(bakery);
		LegacyYnabCategoryMapping[] mappings = [Mapping(produce.Id, BudgetA, "food", "Food"), Mapping(bakery.Id, BudgetA, "food", "Food")];

		// Act
		EnvelopeBackfillPlan actual = Plan([first, second], mappings, [produce, bakery]);

		// Assert
		PlannedEnvelope envelope = actual.Envelopes.Should().ContainSingle().Subject;
		envelope.Name.Should().Be("Food");
		envelope.IsActive.Should().BeTrue();
		envelope.Links.Should().ContainSingle().Which.Should().Be(new YnabEnvelopeLink(BudgetA, "food", "Food", "Group"));
		actual.ItemEnvelopes[first.ItemId].Should().Be(envelope.Id);
		actual.ItemEnvelopes[second.ItemId].Should().Be(envelope.Id);
		AssertParity(actual, [first, second], mappings);
	}

	[Fact]
	public void Plan_DistinctYnabCategories_GetOneEnvelopeEach()
	{
		// Arrange
		TaxonomyNode produce = Node("Produce"), fuel = Node("Fuel");
		EnvelopeBackfillItem food = Item(produce), gas = Item(fuel);
		LegacyYnabCategoryMapping[] mappings = [Mapping(produce.Id, BudgetA, "food", "Food"), Mapping(fuel.Id, BudgetA, "gas", "Gas")];

		// Act
		EnvelopeBackfillPlan actual = Plan([food, gas], mappings, [produce, fuel]);

		// Assert
		actual.Envelopes.Select(envelope => envelope.Name).Should().BeEquivalentTo(["Food", "Gas"]);
		actual.ItemEnvelopes[food.ItemId]!.Value.Should().NotBe(actual.ItemEnvelopes[gas.ItemId]!.Value);
		AssertParity(actual, [food, gas], mappings);
	}

	[Fact]
	public void Plan_UnmappedRootWithItems_GetsArchivedUnlinkedEnvelopeNamedForTheRoot()
	{
		// Arrange
		TaxonomyNode hobbies = Node("Hobbies");
		TaxonomyNode paint = Node("Paint", hobbies.Id);
		TaxonomyNode unused = Node("Unused");
		EnvelopeBackfillItem item = Item(hobbies, paint);

		// Act
		EnvelopeBackfillPlan actual = Plan([item], [], [hobbies, paint, unused]);

		// Assert
		PlannedEnvelope envelope = actual.Envelopes.Should().ContainSingle().Subject;
		envelope.Name.Should().Be("Hobbies");
		envelope.IsActive.Should().BeFalse();
		envelope.Links.Should().BeEmpty();
		actual.ItemEnvelopes[item.ItemId].Should().Be(envelope.Id);
		actual.Defaults.Should().BeEmpty();
	}

	[Fact]
	public void Plan_UnmappedRenamedRoot_UsesCurrentRootName()
	{
		// Arrange
		Guid rootId = Guid.NewGuid();
		TaxonomyNode recorded = new(rootId, null, "Old name", null, false);
		TaxonomyNode current = new(rootId, null, "New name", null, false);
		EnvelopeBackfillItem item = Item(recorded);

		// Act
		EnvelopeBackfillPlan actual = Plan([item], [], [current]);

		// Assert
		actual.Envelopes.Should().ContainSingle().Which.Name.Should().Be("New name");
	}

	[Fact]
	public void Plan_UnmappedRootRemovedFromCurrentTree_UsesRecordedRootName()
	{
		// Arrange
		TaxonomyNode recorded = Node("Recorded only");
		EnvelopeBackfillItem item = Item(recorded);

		// Act
		EnvelopeBackfillPlan actual = Plan([item], [], []);

		// Assert
		actual.Envelopes.Should().ContainSingle().Which.Name.Should().Be("Recorded only");
	}

	[Fact]
	public void Plan_UnclassifiedItems_GetNoEnvelope()
	{
		// Arrange
		TaxonomyNode produce = Node("Produce");
		EnvelopeBackfillItem neverClassified = new(Guid.NewGuid(), null);
		EnvelopeBackfillItem cleared = new(Guid.NewGuid(), new CurrentClassification(Guid.NewGuid(), null, Revision, []));
		LegacyYnabCategoryMapping[] mappings = [Mapping(produce.Id, BudgetA, "food", "Food")];

		// Act
		EnvelopeBackfillPlan actual = Plan([neverClassified, cleared], mappings, [produce]);

		// Assert
		actual.ItemEnvelopes.Should().HaveCount(2);
		actual.ItemEnvelopes[neverClassified.ItemId].Should().BeNull();
		actual.ItemEnvelopes[cleared.ItemId].Should().BeNull();
		AssertParity(actual, [neverClassified, cleared], mappings);
	}

	[Fact]
	public void Plan_NameCollisions_AreSuffixedDeterministically()
	{
		// Arrange
		TaxonomyNode produce = Node("Produce"), snacks = Node("Snacks"), food = Node("Food");
		EnvelopeBackfillItem first = Item(produce), second = Item(snacks), third = Item(food);
		LegacyYnabCategoryMapping[] mappings = [Mapping(produce.Id, BudgetA, "food-1", "Food"), Mapping(snacks.Id, BudgetA, "food-2", "Food")];

		// Act
		EnvelopeBackfillPlan actual = Plan([first, second, third], mappings, [produce, snacks, food]);

		// Assert
		actual.Envelopes.Select(envelope => envelope.Name).Should().Equal("Food", "Food (2)", "Food (3)");
		actual.Envelopes[2].IsActive.Should().BeFalse("the unmapped root's envelope is created after the linked ones");
		AssertParity(actual, [first, second, third], mappings);
	}

	[Fact]
	public void Plan_BlankYnabCategoryName_FallsBackToTheYnabCategoryId()
	{
		// Arrange
		TaxonomyNode produce = Node("Produce");
		EnvelopeBackfillItem item = Item(produce);

		// Act
		EnvelopeBackfillPlan actual = Plan([item], [Mapping(produce.Id, BudgetA, "food-id", " ")], [produce]);

		// Assert
		actual.Envelopes.Should().ContainSingle().Which.Name.Should().Be("food-id");
	}

	[Fact]
	public void Plan_OverlongName_IsTruncatedToFitWithSuffix()
	{
		// Arrange
		string longName = new('x', EnvelopeBackfillPlanner.MaxNameLength + 20);
		TaxonomyNode a = Node("A"), b = Node("B");
		LegacyYnabCategoryMapping[] mappings = [Mapping(a.Id, BudgetA, "one", longName), Mapping(b.Id, BudgetA, "two", longName)];

		// Act
		EnvelopeBackfillPlan actual = Plan([Item(a), Item(b)], mappings, [a, b]);

		// Assert
		actual.Envelopes.Should().AllSatisfy(envelope => envelope.Name.Length.Should().Be(EnvelopeBackfillPlanner.MaxNameLength));
		actual.Envelopes[1].Name.Should().EndWith(" (2)");
	}

	[Fact]
	public void Plan_MappedNodes_SeedDefaultsSharingTheItemsEnvelope()
	{
		// Arrange
		TaxonomyNode groceries = Node("Groceries");
		TaxonomyNode produce = Node("Produce", groceries.Id);
		TaxonomyNode archived = new(Guid.NewGuid(), null, "Archived", null, true);
		EnvelopeBackfillItem item = Item(groceries, produce);
		LegacyYnabCategoryMapping[] mappings = [Mapping(groceries.Id, BudgetA, "food", "Food"), Mapping(archived.Id, BudgetA, "old", "Old")];

		// Act
		EnvelopeBackfillPlan actual = Plan([item], mappings, [groceries, produce, archived]);

		// Assert
		actual.Defaults.Should().ContainSingle();
		actual.Defaults[groceries.Id].Should().Be(actual.ItemEnvelopes[item.ItemId]!.Value);
		actual.Defaults.Should().NotContainKey(produce.Id, "unmapped children resolve through their nearest mapped ancestor");
		actual.Defaults.Should().NotContainKey(archived.Id);
	}

	[Fact]
	public void Plan_MappedNodeWithoutItems_StillGetsADefaultAndEnvelope()
	{
		// Arrange
		TaxonomyNode travel = Node("Travel");

		// Act
		EnvelopeBackfillPlan actual = Plan([], [Mapping(travel.Id, BudgetA, "trips", "Trips")], [travel]);

		// Assert
		PlannedEnvelope envelope = actual.Envelopes.Should().ContainSingle().Subject;
		envelope.Name.Should().Be("Trips");
		actual.Defaults[travel.Id].Should().Be(envelope.Id);
	}

	[Fact]
	public void Plan_UnboundLegacyRows_BecomeLinkedEnvelopesOnceEach()
	{
		// Arrange
		TaxonomyNode produce = Node("Produce");
		EnvelopeBackfillItem item = Item(produce);
		LegacyYnabCategoryMapping[] mappings =
		[
			Mapping(produce.Id, BudgetA, "food", "Food"),
			Mapping(null, BudgetA, "food", "Food"),
			Mapping(null, BudgetA, "gifts", "Gifts"),
			Mapping(null, BudgetB, "gifts", "Gifts"),
		];

		// Act
		EnvelopeBackfillPlan actual = Plan([item], mappings, [produce]);

		// Assert
		actual.Envelopes.Should().HaveCount(3);
		actual.Envelopes.Should().ContainSingle(envelope => envelope.Name == "Food");
		actual.Envelopes.Where(envelope => envelope.Name.StartsWith("Gifts")).Should().HaveCount(2)
			.And.AllSatisfy(envelope =>
			{
				envelope.IsActive.Should().BeTrue();
				envelope.Links.Should().ContainSingle().Which.YnabCategoryId.Should().Be("gifts");
			});
		AssertParity(actual, [item], mappings);
	}

	[Fact]
	public void Plan_TwoBudgets_EnvelopeCarriesEachBudgetsCategoryAndPrefersSelectedBudgetName()
	{
		// Arrange
		TaxonomyNode produce = Node("Produce"), fuel = Node("Fuel");
		EnvelopeBackfillItem food = Item(produce), gas = Item(fuel);
		LegacyYnabCategoryMapping[] mappings =
		[
			Mapping(produce.Id, BudgetA, "a-food", "Food A"),
			Mapping(produce.Id, BudgetB, "b-food", "Food B"),
			Mapping(fuel.Id, BudgetA, "a-gas", "Gas A"),
		];

		// Act
		EnvelopeBackfillPlan actual = EnvelopeBackfillPlanner.Plan([food, gas], mappings, Revision, [produce, fuel], BudgetB, Guid.NewGuid);

		// Assert
		PlannedEnvelope foodEnvelope = actual.Envelopes.Single(envelope => envelope.Id == actual.ItemEnvelopes[food.ItemId]);
		foodEnvelope.Name.Should().Be("Food B");
		foodEnvelope.Links.Select(link => (link.BudgetId, link.YnabCategoryId)).Should()
			.BeEquivalentTo([(BudgetA, "a-food"), (BudgetB, "b-food")]);
		PlannedEnvelope gasEnvelope = actual.Envelopes.Single(envelope => envelope.Id == actual.ItemEnvelopes[gas.ItemId]);
		gasEnvelope.Name.Should().Be("Gas A");
		gasEnvelope.Links.Should().ContainSingle();
		AssertParity(actual, [food, gas], mappings);
	}

	[Fact]
	public void Plan_RecordedAncestryWinsOverCurrentTree()
	{
		// Arrange
		TaxonomyNode oldParent = Node("Old parent"), newParent = Node("New parent");
		Guid leafId = Guid.NewGuid();
		TaxonomyNode recordedLeaf = new(leafId, oldParent.Id, "Leaf", null, false);
		TaxonomyNode currentLeaf = new(leafId, newParent.Id, "Leaf", null, false);
		EnvelopeBackfillItem item = Item(oldParent, recordedLeaf);
		LegacyYnabCategoryMapping[] mappings = [Mapping(oldParent.Id, BudgetA, "old", "Old"), Mapping(newParent.Id, BudgetA, "new", "New")];

		// Act
		EnvelopeBackfillPlan actual = Plan([item], mappings, [oldParent, newParent, currentLeaf]);

		// Assert
		actual.Envelopes.Single(envelope => envelope.Id == actual.ItemEnvelopes[item.ItemId]).Name.Should().Be("Old");
		AssertParity(actual, [item], mappings);
	}

	[Fact]
	public void Plan_LinkUsesLatestLegacyRowForThatYnabCategory()
	{
		// Arrange
		TaxonomyNode a = Node("A"), b = Node("B");
		LegacyYnabCategoryMapping older = Mapping(a.Id, BudgetA, "food", "Old food name") with { UpdatedAt = Now.AddDays(-5) };
		LegacyYnabCategoryMapping newer = Mapping(b.Id, BudgetA, "food", "New food name") with { UpdatedAt = Now };

		// Act
		EnvelopeBackfillPlan actual = Plan([Item(a), Item(b)], [older, newer], [a, b]);

		// Assert
		actual.Envelopes.Should().ContainSingle().Which.Links.Should().ContainSingle()
			.Which.YnabCategoryName.Should().Be("New food name");
	}

	[Fact]
	public void Parity_TamperedItemEnvelope_IsCountedAsMismatch()
	{
		// Arrange
		TaxonomyNode produce = Node("Produce"), fuel = Node("Fuel");
		EnvelopeBackfillItem food = Item(produce), gas = Item(fuel);
		LegacyYnabCategoryMapping[] mappings = [Mapping(produce.Id, BudgetA, "food", "Food"), Mapping(fuel.Id, BudgetA, "gas", "Gas")];
		EnvelopeBackfillPlan plan = Plan([food, gas], mappings, [produce, fuel]);
		List<EnvelopeParityItem> items =
		[
			new(food.ItemId, food.Classification, plan.ItemEnvelopes[gas.ItemId]),
			new(gas.ItemId, gas.Classification, plan.ItemEnvelopes[gas.ItemId]),
		];

		// Act
		EnvelopeParityReport actual = EnvelopeParity.Check(items, mappings, plan.Envelopes.Count, Links(plan));

		// Assert
		actual.Mismatches.Should().Be(1);
		actual.Holds.Should().BeFalse();
		actual.ItemsMappedByOldPath.Should().Be(2);
		actual.ItemsMappedByNewPath.Should().Be(2);
	}

	[Fact]
	public void Parity_TamperedEnvelopeLink_IsCountedAsMismatch()
	{
		// Arrange
		TaxonomyNode produce = Node("Produce");
		EnvelopeBackfillItem food = Item(produce);
		LegacyYnabCategoryMapping[] mappings = [Mapping(produce.Id, BudgetA, "food", "Food")];
		EnvelopeBackfillPlan plan = Plan([food], mappings, [produce]);
		Dictionary<(Guid EnvelopeId, string BudgetId), string> links = Links(plan);
		links[(plan.ItemEnvelopes[food.ItemId]!.Value, BudgetA)] = "something-else";

		// Act
		EnvelopeParityReport actual = EnvelopeParity.Check(ParityItems(plan, [food]), mappings, plan.Envelopes.Count, links);

		// Assert
		actual.Mismatches.Should().Be(1);
	}

	[Fact]
	public void Parity_ExtraLinkInBudgetTheItemWasNeverMappedIn_IsCountedAsMismatch()
	{
		// Arrange
		TaxonomyNode produce = Node("Produce");
		EnvelopeBackfillItem food = Item(produce);
		LegacyYnabCategoryMapping[] mappings = [Mapping(produce.Id, BudgetA, "food", "Food")];
		EnvelopeBackfillPlan plan = Plan([food], mappings, [produce]);
		Dictionary<(Guid EnvelopeId, string BudgetId), string> links = Links(plan);
		links[(plan.ItemEnvelopes[food.ItemId]!.Value, BudgetB)] = "b-food";

		// Act
		EnvelopeParityReport actual = EnvelopeParity.Check(ParityItems(plan, [food]), mappings, plan.Envelopes.Count, links);

		// Assert
		actual.Mismatches.Should().Be(1);
		actual.Budgets.Should().Be(2);
	}

	[Fact]
	public void Parity_ReportsCounts()
	{
		// Arrange
		TaxonomyNode produce = Node("Produce"), unmapped = Node("Unmapped");
		EnvelopeBackfillItem food = Item(produce), other = Item(unmapped), none = new(Guid.NewGuid(), null);
		LegacyYnabCategoryMapping[] mappings = [Mapping(produce.Id, BudgetA, "food", "Food"), Mapping(produce.Id, BudgetB, "b-food", "Food")];
		EnvelopeBackfillPlan plan = Plan([food, other, none], mappings, [produce, unmapped]);

		// Act
		EnvelopeParityReport actual = EnvelopeParity.Check(ParityItems(plan, [food, other, none]), mappings, plan.Envelopes.Count, Links(plan));

		// Assert
		actual.Should().Be(new EnvelopeParityReport(3, 2, 2, 1, 1, 0));
		actual.Holds.Should().BeTrue();
	}

	/// <summary>
	/// The core guarantee: whatever the taxonomy, mappings and history look like, every item's envelope
	/// pushes to the same YNAB category in every budget as its recorded classification did.
	/// </summary>
	[Fact]
	public void Plan_RandomTaxonomiesAndMappings_AlwaysHoldsParity()
	{
		int mappedItems = 0, multiBudgetScenarios = 0, archivedEnvelopes = 0;
		for (int seed = 0; seed < 400; seed++)
		{
			// Arrange
			Random random = new(seed);
			Scenario scenario = RandomScenario(random);

			// Act
			EnvelopeBackfillPlan plan = EnvelopeBackfillPlanner.Plan(scenario.Items, scenario.Mappings, Revision,
				scenario.CurrentNodes, scenario.SelectedBudget, Guid.NewGuid);
			EnvelopeParityReport actual = EnvelopeParity.Check(ParityItems(plan, scenario.Items), scenario.Mappings,
				plan.Envelopes.Count, Links(plan));

			// Assert
			actual.Mismatches.Should().Be(0, "seed {0} must hold parity", seed);
			actual.Items.Should().Be(scenario.Items.Count);
			plan.ItemEnvelopes.Keys.Should().BeEquivalentTo(scenario.Items.Select(item => item.ItemId));
			plan.Envelopes.Select(envelope => envelope.Name).Should().OnlyHaveUniqueItems("seed {0} names must be unique", seed);
			plan.Envelopes.Should().AllSatisfy(envelope =>
				envelope.Links.Select(link => link.BudgetId).Should().OnlyHaveUniqueItems());
			plan.Defaults.Values.Should().BeSubsetOf(plan.Envelopes.Select(envelope => envelope.Id));
			mappedItems += actual.ItemsMappedByOldPath;
			multiBudgetScenarios += actual.Budgets > 1 ? 1 : 0;
			archivedEnvelopes += plan.Envelopes.Count(envelope => !envelope.IsActive);
		}

		// The generator must actually exercise mapped, multi-budget and unmapped-root histories.
		mappedItems.Should().BeGreaterThan(500);
		multiBudgetScenarios.Should().BeGreaterThan(100);
		archivedEnvelopes.Should().BeGreaterThan(50);
	}

	private sealed record Scenario(List<EnvelopeBackfillItem> Items, List<LegacyYnabCategoryMapping> Mappings,
		List<TaxonomyNode> CurrentNodes, string? SelectedBudget);

	private static Scenario RandomScenario(Random random)
	{
		// Recorded tree: parents always precede children, so recorded paths are acyclic.
		int nodeCount = random.Next(1, 14);
		List<TaxonomyNode> recorded = [];
		for (int index = 0; index < nodeCount; index++)
		{
			Guid? parent = index == 0 || random.NextDouble() < 0.35 ? null : recorded[random.Next(index)].Id;
			recorded.Add(new(Guid.NewGuid(), parent, Pick(random, ["Food", "Home", "Gifts", "Fun", "Car"]), null, false));
		}

		// Current tree: some nodes moved, renamed, archived or removed since they were recorded.
		List<TaxonomyNode> current = [];
		foreach (TaxonomyNode node in recorded)
		{
			if (random.NextDouble() < 0.1)
			{
				continue;
			}
			int index = recorded.IndexOf(node);
			Guid? parent = random.NextDouble() < 0.25
				? (index == 0 || random.NextDouble() < 0.4 ? null : recorded[random.Next(index)].Id)
				: node.ParentId;
			current.Add(node with
			{
				ParentId = parent,
				Name = random.NextDouble() < 0.2 ? node.Name + " renamed" : node.Name,
				Archived = random.NextDouble() < 0.1,
			});
		}

		// One or two live budgets, sometimes a stale one, and categories with colliding names.
		List<string> budgets = random.NextDouble() < 0.5 ? ["b1"] : ["b1", "b2"];
		if (random.NextDouble() < 0.4)
		{
			budgets.Add("stale");
		}
		List<LegacyYnabCategoryMapping> mappings = [];
		foreach (string budget in budgets)
		{
			int categories = random.Next(1, 5);
			foreach (TaxonomyNode node in recorded.Where(_ => random.NextDouble() < 0.35))
			{
				mappings.Add(RandomMapping(random, node.Id, budget, categories));
				if (random.NextDouble() < 0.05)
				{
					mappings.Add(RandomMapping(random, node.Id, budget, categories));
				}
			}
			if (random.NextDouble() < 0.3)
			{
				mappings.Add(RandomMapping(random, null, budget, categories + 2));
			}
		}

		List<EnvelopeBackfillItem> items = [];
		int itemCount = random.Next(0, 30);
		for (int index = 0; index < itemCount; index++)
		{
			double roll = random.NextDouble();
			if (roll < 0.1)
			{
				items.Add(new(Guid.NewGuid(), null));
				continue;
			}
			if (roll < 0.2)
			{
				items.Add(new(Guid.NewGuid(), new CurrentClassification(Guid.NewGuid(), null, Revision, [])));
				continue;
			}
			TaxonomyNode leaf = recorded[random.Next(recorded.Count)];
			List<TaxonomyNode> path = PathTo(leaf, recorded);
			if (roll < 0.25)
			{
				// Corrupt evidence: a recorded path that does not end at the assigned category.
				path = [.. path.Take(Math.Max(0, path.Count - 1))];
			}
			items.Add(new(Guid.NewGuid(), new CurrentClassification(Guid.NewGuid(), leaf.Id, Guid.NewGuid(), path)));
		}

		string? selected = random.NextDouble() < 0.2 ? null : Pick(random, budgets);
		return new(items, mappings, current, selected);
	}

	private static LegacyYnabCategoryMapping RandomMapping(Random random, Guid? categoryId, string budget, int categories)
	{
		int category = random.Next(categories);
		string name = random.NextDouble() < 0.05 ? "" : Pick(random, ["Food", "Home", "Gifts", "Fun"]);
		return new(Guid.NewGuid(), categoryId, budget, $"{budget}-cat-{category}", name, "Group",
			Now.AddMinutes(-random.Next(1000)));
	}

	private static List<TaxonomyNode> PathTo(TaxonomyNode leaf, List<TaxonomyNode> nodes)
	{
		Dictionary<Guid, TaxonomyNode> byId = nodes.ToDictionary(node => node.Id);
		List<TaxonomyNode> path = [];
		for (TaxonomyNode? cursor = leaf; cursor is not null; cursor = cursor.ParentId is Guid parent ? byId[parent] : null)
		{
			path.Insert(0, cursor);
		}
		return path;
	}

	private static T Pick<T>(Random random, IReadOnlyList<T> values) => values[random.Next(values.Count)];

	private static EnvelopeBackfillPlan Plan(IReadOnlyList<EnvelopeBackfillItem> items,
		IReadOnlyList<LegacyYnabCategoryMapping> mappings, IReadOnlyList<TaxonomyNode> currentNodes)
		=> EnvelopeBackfillPlanner.Plan(items, mappings, Revision, currentNodes, BudgetA, Guid.NewGuid);

	private static void AssertParity(EnvelopeBackfillPlan plan, IReadOnlyList<EnvelopeBackfillItem> items,
		IReadOnlyList<LegacyYnabCategoryMapping> mappings)
		=> EnvelopeParity.Check(ParityItems(plan, items), mappings, plan.Envelopes.Count, Links(plan)).Mismatches.Should().Be(0);

	private static List<EnvelopeParityItem> ParityItems(EnvelopeBackfillPlan plan, IEnumerable<EnvelopeBackfillItem> items)
		=> items.Select(item => new EnvelopeParityItem(item.ItemId, item.Classification, plan.ItemEnvelopes[item.ItemId])).ToList();

	private static Dictionary<(Guid EnvelopeId, string BudgetId), string> Links(EnvelopeBackfillPlan plan)
		=> plan.Envelopes.SelectMany(envelope => envelope.Links.Select(link => (envelope.Id, link)))
			.ToDictionary(pair => (pair.Id, pair.link.BudgetId), pair => pair.link.YnabCategoryId);

	private static TaxonomyNode Node(string name, Guid? parentId = null) => new(Guid.NewGuid(), parentId, name, null, false);

	private static EnvelopeBackfillItem Item(params TaxonomyNode[] recordedPath)
		=> new(Guid.NewGuid(), new CurrentClassification(Guid.NewGuid(), recordedPath[^1].Id, Revision, recordedPath));

	private static LegacyYnabCategoryMapping Mapping(Guid? categoryId, string budget, string ynabCategoryId, string ynabCategoryName)
		=> new(Guid.NewGuid(), categoryId, budget, ynabCategoryId, ynabCategoryName, "Group", Now);
}
