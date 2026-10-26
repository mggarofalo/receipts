using Application.Models.Reports;
using Domain.Taxonomy;
using FluentAssertions;

namespace Application.Tests.Models.Reports;

public class CategoryReportProjectionTests
{
	private readonly Guid _old = Guid.NewGuid(), _current = Guid.NewGuid();
	private readonly Guid _root = Guid.NewGuid(), _newRoot = Guid.NewGuid(), _leaf = Guid.NewGuid();
	private CategoryReportNode[] Nodes =>
	[
		new(_old, new(_root, null, "Grocery", null, false)),
		new(_old, new(_leaf, _root, "Milk", null, false)),
		new(_current, new(_root, null, "Food", null, false)),
		new(_current, new(_newRoot, null, "Chilled", null, true)),
		new(_current, new(_leaf, _newRoot, "Dairy", null, true))
	];

	[Fact]
	public void Views_PreserveRecordedPaths_AndRegroupArchivedMovedRenamedLeaves()
	{
		CategoryReportFact fact = new(_leaf, _old, CategoryReportState.Classified, "USD", 12.345m, 1);
		CategoryReportResult historical = CategoryReportProjection.Project([fact], Nodes, CategoryReportView.AsClassified, _current);
		CategoryReportResult current = CategoryReportProjection.Project([fact], Nodes, CategoryReportView.CurrentHierarchy, _current);
		historical.Buckets.Single().Path.Select(node => node.Name).Should().Equal("Grocery", "Milk");
		historical.Buckets.Single().PathRevisionId.Should().Be(_old);
		current.Buckets.Single().Path.Select(node => node.Name).Should().Equal("Chilled", "Dairy");
		current.Buckets.Single().Path.Last().Archived.Should().BeTrue();
		current.Buckets.Single().Amount.Should().Be(12.345m);
		current.CurrentRevisionId.Should().Be(_current);
		fact.CategoryId.Should().Be(_leaf);
	}

	[Fact]
	public void RootRollup_IncludesDirectAndDescendantAmountsOnce_AndSeparatesCurrencies()
	{
		CategoryReportFact[] facts =
		[
			new(_root, _old, CategoryReportState.Classified, "USD", 0.10m, 1),
			new(_leaf, _old, CategoryReportState.Classified, "USD", 0.20m, 2),
			new(_leaf, _old, CategoryReportState.Classified, "EUR", 5m, 1)
		];
		var result = CategoryReportProjection.Project(facts, Nodes, CategoryReportView.AsClassified, null,
			CategoryReportRollup.Root, _root);
		result.Buckets.Should().HaveCount(2);
		var usd = result.Buckets.Single(bucket => bucket.Currency == "USD");
		usd.Amount.Should().Be(0.30m);
		usd.PurchaseCount.Should().Be(3);
		usd.CategoryId.Should().Be(_root);
		CategoryReportProjection.Project(facts, Nodes, CategoryReportView.CurrentHierarchy, _current,
			categoryFilter: _root).Buckets.Should().ContainSingle().Which.Amount.Should().Be(0.10m);
	}

	[Fact]
	public void SameIdentityAtDifferentRevisions_RemainsDistinctHistorically()
	{
		CategoryReportFact[] facts = [new(_root, _old, CategoryReportState.Classified, "USD", 1m, 1),
			new(_root, _current, CategoryReportState.Classified, "USD", 2m, 1)];
		CategoryReportProjection.Project(facts, Nodes, CategoryReportView.AsClassified, null).Buckets.Should().HaveCount(2);
		CategoryReportProjection.Project(facts, Nodes, CategoryReportView.CurrentHierarchy, _current)
			.Buckets.Should().ContainSingle().Which.Amount.Should().Be(3m);
	}

	[Fact]
	public void PartialData_SeparatesMissingExplicitNullLegacyAndBrokenPaths()
	{
		CategoryReportFact[] facts = [new(null, null, CategoryReportState.MissingAssignment, "USD", 1m, 1),
			new(null, _old, CategoryReportState.Unclassified, "USD", 2m, 1),
			new(null, _old, CategoryReportState.UnresolvedLegacy, "USD", 3m, 1),
			new(Guid.NewGuid(), _old, CategoryReportState.Classified, "USD", 4m, 1)];
		var result = CategoryReportProjection.Project(facts, Nodes, CategoryReportView.AsClassified, null);
		result.Buckets.Select(bucket => bucket.State).Should().BeEquivalentTo(
			new[] { CategoryReportState.MissingAssignment, CategoryReportState.Unclassified,
				CategoryReportState.UnresolvedLegacy, CategoryReportState.UnresolvedPath });
		result.Buckets.Sum(bucket => bucket.Amount).Should().Be(10m);
	}
}
