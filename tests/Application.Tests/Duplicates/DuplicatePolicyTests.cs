using Application.Duplicates;
using Application.Models.Reports;
using FluentAssertions;

namespace Application.Tests.Duplicates;

public class DuplicatePolicyTests
{
	[Theory]
	[InlineData("dateAndTotal")]
	[InlineData("dateAndLocationAndTotal")]
	public void Clustering_IsStableInclusiveAndDoesNotChain(string mode)
	{
		Guid low = Guid.Parse("00000000-0000-0000-0000-000000000001");
		Guid middle = Guid.Parse("00000000-0000-0000-0000-000000000002");
		Guid high = Guid.Parse("00000000-0000-0000-0000-000000000003");
		DuplicateReceiptSummary[] rows = [new(low, "Store", new(2025, 1, 1), 10m), new(middle, "Store", new(2025, 1, 1), 11m), new(high, "Store", new(2025, 1, 1), 12m)];
		foreach (int[] permutation in new[] { new[] { 0, 1, 2 }, [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0] })
		{
			List<DuplicateGroup> groups = DuplicateDetectionPolicy.Group(permutation.Select(i => rows[i]).ToArray(), mode, "exact", 1m);
			groups.Should().ContainSingle().Which.Receipts.Select(r => r.ReceiptId).Should().Equal(low, middle);
		}
	}

	[Fact]
	public void LocationNormalization_IsAnExplicitMode()
	{
		DuplicateReceiptSummary[] rows = [new(Guid.NewGuid(), " Store  A ", new(2025, 1, 1), 10m), new(Guid.NewGuid(), "store a", new(2025, 1, 1), 10m)];
		DuplicateDetectionPolicy.Group(rows, "dateAndLocation", "exact", 0m).Should().BeEmpty();
		DuplicateDetectionPolicy.Group(rows, "dateAndLocation", "normalized", 0m).Should().ContainSingle().Which.Receipts.Should().HaveCount(2);
	}

	[Fact]
	public void Components_IncludeAllMembersWithoutImplyingCompleteAcceptance()
	{
		Guid a = Guid.NewGuid(); Guid b = Guid.NewGuid(); Guid c = Guid.NewGuid();
		DateTimeOffset first = DateTimeOffset.UnixEpoch; DateTimeOffset latest = first.AddDays(1);
		List<DuplicateAcceptanceComponent> components = DuplicateAcceptancePolicy.Components([new(a, b, first), new(b, c, latest)]);
		components.Should().ContainSingle().Which.ReceiptIds.Should().BeEquivalentTo([a, b, c]);
		components[0].AcceptedAt.Should().Be(latest);
		HashSet<(Guid A, Guid B)> pairs = [.. DuplicateAcceptancePolicy.CanonicalPairs([a, b]), .. DuplicateAcceptancePolicy.CanonicalPairs([b, c])];
		DuplicateAcceptancePolicy.IsFullyAccepted([a, b, c], pairs).Should().BeFalse();
		pairs.UnionWith(DuplicateAcceptancePolicy.CanonicalPairs([a, c]));
		DuplicateAcceptancePolicy.IsFullyAccepted([a, b, c], pairs).Should().BeTrue();
	}

	[Fact]
	public void Components_LongChain_DoesNotRequireRecursionOrQuadraticMembershipChecks()
	{
		Guid[] ids = Enumerable.Range(0, 20000).Select(_ => Guid.NewGuid()).ToArray();
		DuplicateAcceptedPair[] pairs = Enumerable.Range(1, ids.Length - 1).Select(i => new DuplicateAcceptedPair(ids[i - 1], ids[i], DateTimeOffset.UnixEpoch)).ToArray();
		DuplicateAcceptancePolicy.Components(pairs).Should().ContainSingle().Which.ReceiptIds.Should().Equal(ids.Order());
	}
}
