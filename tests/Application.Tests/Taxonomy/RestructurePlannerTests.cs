using System.Text.Json;
using Application.Taxonomy;
using Application.Taxonomy.Restructure;
using Domain.Taxonomy;
using FluentAssertions;

namespace Application.Tests.Taxonomy;

public class RestructurePlannerTests
{
	private static readonly DateTimeOffset Snapshot = new(2026, 10, 29, 18, 0, 0, TimeSpan.Zero);
	private static readonly Guid Base = Guid.NewGuid();

	// Groceries / Dairy, Groceries / Placeholder, Event (envelope root) / Dairy.
	private static readonly Guid Groceries = Guid.NewGuid(), Dairy = Guid.NewGuid(), Placeholder = Guid.NewGuid(),
		Event = Guid.NewGuid(), EventDairy = Guid.NewGuid(), Household = Guid.NewGuid();

	private static readonly TaxonomyNode[] Tree =
	[
		new(Groceries, null, "Groceries", null, false),
		new(Dairy, Groceries, "Dairy", null, false),
		new(Placeholder, Groceries, "N/A", null, false),
		new(Event, null, "Event", null, false),
		new(EventDairy, Event, "Dairy", null, false),
		new(Household, null, "Household", null, false),
	];

	private static readonly Guid Pantry = Guid.NewGuid(), Food = Guid.NewGuid(), Party = Guid.NewGuid(),
		PartyLinked = Guid.NewGuid(), Home = Guid.NewGuid();

	private static RestructureCategoryChange Define(Guid id, string text) => new("define", id, Definition: text);

	/// <summary>Defines every surviving type, archives the envelope root and the placeholder, and maps both.</summary>
	private static RestructurePlan Plan(IReadOnlyList<RestructureItemReassignment>? items = null,
		IReadOnlyList<RestructureEnvelopeDefault>? defaults = null, IReadOnlyList<RestructureEnvelopeMerge>? merges = null,
		IReadOnlyList<RestructureNodeMapping>? nodeMappings = null, IReadOnlyList<RestructureCategoryChange>? extra = null) => new(
		RestructurePlan.FormatName, 1, Guid.NewGuid(), "Item types, not envelopes", Base, Snapshot,
		[
			Define(Groceries, "Includes food. Not cleaning products."),
			Define(Dairy, "Includes milk and cheese. Not plant milks."),
			Define(Household, "Includes cleaning products. Not food."),
			new("archive", Event),
			new("archive", Placeholder),
			.. extra ?? []
		],
		nodeMappings ??
		[
			new(Event, Clear: true),
			new(EventDairy, Dairy, Occasion: "Event"),
			new(Placeholder, Clear: true),
		],
		items, ["Event"], defaults, merges);

	private static RestructureItemState Item(Guid category, string source = "legacy_import", DateTimeOffset? recordedAt = null,
		Guid? envelope = null) => new(Guid.NewGuid(), Guid.NewGuid(), category, recordedAt ?? Snapshot.AddDays(-1), source, null, envelope);

	private static RestructureState State(IReadOnlyList<RestructureItemState> items, IReadOnlyDictionary<Guid, Guid>? defaults = null,
		IReadOnlyList<RestructureMappingState>? mappings = null, IReadOnlyList<TaxonomyNode>? baseNodes = null) => new(Guid.NewGuid(), Tree,
		baseNodes ?? Tree, items,
		[
			new(Pantry, "Pantry", true, [new("budget", "ynab-pantry")]),
			new(Food, "Pantry (2)", false, []),
			new(Party, "Party", true, [new("budget", "ynab-party")]),
			new(PartyLinked, "Party fund", true, [new("budget", "ynab-other")]),
			new(Home, "Home", true, [new("budget", "ynab-home")]),
		],
		[], defaults ?? new Dictionary<Guid, Guid>(), mappings ?? [], []);

	[Fact]
	public void NodeMappingsMoveUnlistedPurchasesAndListedOnesOverrideThem()
	{
		RestructureItemState onEnvelopeLeaf = Item(EventDairy);
		RestructureItemState onPlaceholder = Item(Placeholder);
		RestructureItemState listed = Item(Placeholder);
		RestructureItemState kept = Item(Dairy);
		JsonElement inputs = JsonSerializer.SerializeToElement(new { description = "milk" });

		RestructureOutcome outcome = RestructurePlanner.Plan(
			Plan([new(listed.ItemId, Dairy, Rule: "d", Evidence: inputs)]), State([onEnvelopeLeaf, onPlaceholder, listed, kept]));

		outcome.Errors.Should().BeEmpty();
		outcome.ItemChanges.Should().HaveCount(3);
		outcome.ItemChanges.Single(c => c.ItemId == onEnvelopeLeaf.ItemId).Should().Match<RestructureItemChange>(c =>
			c.TargetCategoryId == Dairy && c.TargetOccasion == "Event" && c.Rule == "node" && !c.Conflict);
		outcome.ItemChanges.Single(c => c.ItemId == onPlaceholder.ItemId).TargetCategoryId.Should().BeNull();
		RestructureItemChange explicitChange = outcome.ItemChanges.Single(c => c.ItemId == listed.ItemId);
		explicitChange.Rule.Should().Be("d");
		RestructureEvidence evidence = JsonSerializer.Deserialize<RestructureEvidence>(explicitChange.Evidence)!;
		evidence.From.Should().Be(Placeholder);
		evidence.Inputs!.Value.GetProperty("description").GetString().Should().Be("milk");
		outcome.Report.Items.Should().BeEquivalentTo(new RestructureItemReport(4, 3, 1, 1, 0, 0,
			new Dictionary<string, int> { ["node"] = 2, ["d"] = 1 }));
		outcome.Report.Transitions.Should().ContainSingle(t => t.FromPath == "Event / Dairy" && t.ToPath == "Groceries / Dairy" && t.Count == 1);
		outcome.Report.Occasions.Should().ContainSingle(o => o.Name == "Event" && o.Created && o.Items == 1);
		outcome.OccasionsToCreate.Should().Equal("Event");
	}

	[Fact]
	public void PurchasesDecidedByAPersonOrJevAfterTheSnapshotAreConflictsNotWrites()
	{
		RestructureItemState manual = Item(Placeholder, "manual", Snapshot.AddMinutes(1));
		RestructureItemState jev = Item(Placeholder, "jev", Snapshot.AddMinutes(1));
		RestructureItemState mapped = Item(Placeholder, "approved_mapping", Snapshot.AddMinutes(1));
		RestructureItemState legacyLater = Item(Placeholder, "legacy_write", Snapshot.AddMinutes(1));
		RestructureItemState manualEarlier = Item(Placeholder, "manual", Snapshot.AddMinutes(-1));

		RestructureOutcome outcome = RestructurePlanner.Plan(Plan(), State([manual, jev, mapped, legacyLater, manualEarlier]));

		outcome.ItemChanges.Where(c => c.Conflict).Select(c => c.ItemId).Should().BeEquivalentTo([manual.ItemId, jev.ItemId, mapped.ItemId]);
		outcome.Report.Items.Conflicts.Should().Be(3);
		outcome.Report.Items.Reassigned.Should().Be(2);
		outcome.Warnings.Should().ContainSingle(w => w.Contains("3 purchases were classified after"));
	}

	[Fact]
	public void TheBaseTreeMustMatchByContentAndMustExist()
	{
		TaxonomyNode[] edited = [.. Tree.Select(n => n.Id == Dairy ? n with { Name = "Milk" } : n)];
		RestructurePlanner.Plan(Plan(), State([], baseNodes: edited)).Errors.Should().ContainSingle(e => e.Contains("Categories changed"));
		RestructurePlanner.Plan(Plan(), State([]) with { BaseNodes = null }).Errors.Should().ContainSingle(e => e.Contains("does not exist here"));
		// An undone restructure leaves an equal tree under a different revision: the plan applies again.
		RestructurePlanner.Plan(Plan(), State([]) with { CurrentRevisionId = Guid.NewGuid() }).Errors.Should().BeEmpty();
	}

	[Theory]
	[InlineData("unmapped-archive")]
	[InlineData("undefined")]
	[InlineData("too-deep")]
	[InlineData("archived-target")]
	[InlineData("unknown-occasion")]
	[InlineData("self-map")]
	[InlineData("target-and-clear")]
	[InlineData("bad-format")]
	public void InvalidPlansAreRejectedWithAReadableReason(string scenario)
	{
		Guid deep = Guid.NewGuid();
		RestructurePlan plan = scenario switch
		{
			"unmapped-archive" => Plan(nodeMappings: [new(Event, Clear: true), new(Placeholder, Clear: true)]),
			"undefined" => Plan(extra: [new("create", Guid.NewGuid(), Household, "Mops")]),
			"too-deep" => Plan(extra: [new("create", deep, Dairy, "Milk", "Includes milk. Not cream.")]),
			"archived-target" => Plan(nodeMappings: [new(Event, Clear: true), new(EventDairy, Placeholder), new(Placeholder, Clear: true)]),
			"unknown-occasion" => Plan(nodeMappings: [new(Event, Clear: true), new(EventDairy, Dairy, Occasion: "Easter"), new(Placeholder, Clear: true)]),
			"self-map" => Plan(nodeMappings: [new(Event, Clear: true), new(EventDairy, Dairy), new(Placeholder, Clear: true), new(Dairy, Dairy)]),
			"target-and-clear" => Plan(items: [new(Guid.NewGuid(), Dairy, Clear: true, Rule: "d")]),
			_ => Plan() with { Format = "other" },
		};

		RestructureOutcome outcome = RestructurePlanner.Plan(plan, State([]));

		outcome.IsValid.Should().BeFalse();
		string expected = scenario switch
		{
			"unmapped-archive" => "needs a node mapping",
			"undefined" => "needs a definition",
			"too-deep" => "at most 2 levels",
			"archived-target" => "must be active",
			"unknown-occasion" => "must be listed",
			"self-map" => "maps a category to itself",
			"target-and-clear" => "exactly one of a target category or clear",
			_ => "Format must be",
		};
		outcome.Errors.Should().Contain(e => e.Contains(expected));
		outcome.ItemChanges.Should().BeEmpty();
	}

	[Fact]
	public void ListedPurchasesThatNoLongerExistAreWarningsNotErrors()
	{
		RestructureOutcome outcome = RestructurePlanner.Plan(Plan([new(Guid.NewGuid(), Dairy, Rule: "d")]), State([]));

		outcome.IsValid.Should().BeTrue();
		outcome.Report.Items.MissingFromDeployment.Should().Be(1);
	}

	[Fact]
	public void DefaultsOfArchivedTypesMoveWithTheirNodeMappingAndCollisionsNeedAnExplicitChoice()
	{
		Dictionary<Guid, Guid> defaults = new() { [Household] = Pantry, [EventDairy] = Party, [Event] = Party };

		RestructureOutcome moved = RestructurePlanner.Plan(Plan(), State([], defaults));

		moved.Errors.Should().BeEmpty();
		moved.DefaultChanges.Should().BeEquivalentTo(new RestructureDefaultChange[]
		{
			new(Dairy, null, Party), new(Event, Party, null), new(EventDairy, Party, null)
		});
		moved.Report.Defaults.Should().Be(new RestructureDefaultsReport(1, 1, 2, 0));

		defaults[Dairy] = Pantry;
		RestructurePlanner.Plan(Plan(), State([], defaults)).Errors
			.Should().ContainSingle(e => e.Contains("to Groceries / Dairy that differs"));
		defaults.Remove(Dairy);
		// The default Dairy inherits from Groceries counts as well.
		RestructurePlanner.Plan(Plan(), State([], new Dictionary<Guid, Guid> { [Groceries] = Pantry, [EventDairy] = Party })).Errors
			.Should().ContainSingle(e => e.Contains("to Groceries / Dairy that differs"));
		RestructurePlanner.Plan(Plan(), State([], new Dictionary<Guid, Guid> { [Groceries] = Party, [EventDairy] = Party }))
			.DefaultChanges.Should().ContainSingle(c => c.CategoryId == EventDairy && c.EnvelopeId == null);

		defaults[Dairy] = Pantry;
		RestructureOutcome chosen = RestructurePlanner.Plan(Plan(defaults: [new(Dairy, new("Pantry"))]), State([], defaults));
		chosen.Errors.Should().BeEmpty();
		chosen.DefaultChanges.Should().NotContain(c => c.CategoryId == Dairy);
	}

	[Fact]
	public void EnvelopeMergesMovePurchasesDefaultsAndReportBudgetChanges()
	{
		RestructureItemState inFood = Item(Dairy, envelope: Food);
		RestructureItemState inParty = Item(Dairy, envelope: Party);
		Dictionary<Guid, Guid> defaults = new() { [Household] = Food };

		RestructureOutcome outcome = RestructurePlanner.Plan(
			Plan(merges: [new(new("Pantry (2)"), new("Pantry")), new(new("Party"), new("Party fund", "ynab-other"))]),
			State([inFood, inParty], defaults));

		outcome.Errors.Should().BeEmpty();
		outcome.EnvelopeMerges.Should().Equal(new RestructureResolvedMerge(Food, Pantry), new RestructureResolvedMerge(Party, PartyLinked));
		outcome.DefaultChanges.Should().ContainSingle(c => c.CategoryId == Household && c.EnvelopeId == Pantry);
		// The unlinked duplicate gains the original's link; the linked one changes YNAB category.
		outcome.Report.EnvelopeMerges.Should().BeEquivalentTo(new RestructureMergeReport[]
		{
			new("Pantry (2)", "Pantry", 1, 0, 1, 1), new("Party", "Party fund", 1, 0, 0, 1)
		});
		outcome.Report.Budget.Should().Be(new RestructureBudgetReport(2, 2));
	}

	[Theory]
	[InlineData("missing")]
	[InlineData("self")]
	[InlineData("chain")]
	[InlineData("archived-target")]
	public void InvalidEnvelopeMergesAreRejected(string scenario)
	{
		RestructureEnvelopeMerge[] merges = scenario switch
		{
			"missing" => [new(new("Nowhere"), new("Pantry"))],
			"self" => [new(new("Pantry"), new("Pantry"))],
			"chain" => [new(new("Pantry (2)"), new("Pantry")), new(new("Pantry"), new("Home"))],
			_ => [new(new("Pantry"), new("Pantry (2)"))],
		};

		RestructurePlanner.Plan(Plan(merges: merges), State([])).IsValid.Should().BeFalse();
	}

	[Fact]
	public void ApprovedMappingsAreReapprovedRepointedOrRevokedAndStaleOnesAreLeftAlone()
	{
		RestructureMappingState Mapping(Guid category, string? semantics = "valid") => new(new("Store", Guid.NewGuid().ToString(), null),
			Guid.NewGuid().ToString(), Guid.NewGuid(), category,
			semantics == "valid" ? CategoryMappingPolicy.SemanticFingerprint(Tree, category) : semantics);
		RestructureMappingState onDairy = Mapping(Dairy);
		RestructureMappingState onEnvelopeLeaf = Mapping(EventDairy);
		RestructureMappingState onPlaceholder = Mapping(Placeholder);
		RestructureMappingState stale = Mapping(Dairy, "old-definition");

		RestructureOutcome outcome = RestructurePlanner.Plan(Plan(), State([], mappings: [onDairy, onEnvelopeLeaf, onPlaceholder, stale]));

		outcome.MappingChanges.Should().BeEquivalentTo(new RestructureMappingChange[]
		{
			new(onDairy, Dairy), new(onEnvelopeLeaf, Dairy), new(onPlaceholder, null)
		});
		outcome.Report.Mappings.Should().Be(new RestructureMappingsReport(1, 1, 1, 1));
	}

	[Fact]
	public void RevertChangesRestoresTheOriginalTreeAndArchivesCreatedCategories()
	{
		Guid created = Guid.NewGuid();
		RestructureOutcome outcome = RestructurePlanner.Plan(
			Plan(extra: [new("create", created, null, "Pantry staples", "Includes flour. Not snacks."), new("move", Dairy, created),
				new("rename", Household, Name: "Home goods")]),
			State([]));
		outcome.Errors.Should().BeEmpty();

		IReadOnlyList<TaxonomyNode> reverted = TaxonomyTree.Apply(outcome.ResultNodes,
			RestructurePlanner.RevertChanges(outcome.ResultNodes, Tree));

		reverted.Where(n => n.Id != created).Should().BeEquivalentTo(Tree);
		reverted.Single(n => n.Id == created).Archived.Should().BeTrue();
	}
}
