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
	public void Totals_IncludeEnvelopeTables()
	{
		BackupImportResult result = new(5, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
		{
			EnvelopesCreated = 1,
			EnvelopesUpdated = 2,
			OccasionsCreated = 3,
			OccasionsUpdated = 4,
			ItemTypeEnvelopeDefaultsCreated = 5,
			ItemTypeEnvelopeDefaultsUpdated = 6,
			YnabEnvelopeMappingsCreated = 7,
			YnabEnvelopeMappingsUpdated = 8,
		};

		result.TotalCreated.Should().Be(5 + 1 + 3 + 5 + 7);
		result.TotalUpdated.Should().Be(7 + 2 + 4 + 6 + 8);
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
