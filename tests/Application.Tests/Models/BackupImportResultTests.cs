using Application.Models;
using FluentAssertions;

namespace Application.Tests.Models;

public class BackupImportResultTests
{
	[Theory]
	[InlineData(0, 0)]
	[InlineData(2, 3)]
	public void Totals_IncludeAcceptedDuplicateDecisions(int created, int updated)
	{
		BackupImportResult result = new(5, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			AcceptedDuplicatePairsCreated: created, AcceptedDuplicatePairsUpdated: updated);

		result.TotalCreated.Should().Be(5 + created);
		result.TotalUpdated.Should().Be(7 + updated);
	}

	[Fact]
	public void LegacyCallers_OmitNewOptionalCounts_WithoutChangingTotals()
	{
		BackupImportResult result = new(5, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

		result.AcceptedDuplicatePairsCreated.Should().Be(0);
		result.AcceptedDuplicatePairsUpdated.Should().Be(0);
		result.TotalCreated.Should().Be(5);
		result.TotalUpdated.Should().Be(7);
	}
}
