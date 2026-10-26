using System.Security.Cryptography;
using System.Text.Json;
using Application.Normalization;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	[Theory]
	[InlineData("cycle", "Cyclic mapping history.")]
	[InlineData("cross-source predecessor", "Invalid mapping predecessor.")]
	[InlineData("cross-source head", "Mapping head differs from history.")]
	[InlineData("duplicate operation", "Duplicate mapping actor operation.")]
	[InlineData("missing head", "Mapping head differs from history.")]
	[InlineData("unknown head", "Mapping head differs from history.")]
	[InlineData("branch", "Branched mapping history.")]
	public void MappingValidation_RejectsStructuralCorruptionWithValidRequestHashes(string corruption, string message)
	{
		var state = MappingState();
		var rows = state.MappingRevisions;
		switch (corruption)
		{
			case "cycle":
				rows[0].PreviousRevisionId = rows[^1].Id;
				foreach (var row in rows) { row.RecordedAt = rows[^1].RecordedAt; }
				break;
			case "cross-source predecessor":
				rows[1].Location = "Other store";
				rows[1].SourceFingerprint = NormalizationCatalogPolicy.Hash(new(rows[1].Location, rows[1].Description, rows[1].ItemCode));
				break;
			case "cross-source head":
				var source = new NormalizationSourceKey("Other store", rows[0].Description, rows[0].ItemCode);
				state.MappingHeads[0].SourceFingerprint = NormalizationCatalogPolicy.Hash(source);
				break;
			case "duplicate operation": rows[1].OperationId = rows[0].OperationId; break;
			case "missing head": state.MappingHeads.Clear(); break;
			case "unknown head": state.MappingHeads[0].RevisionId = Guid.NewGuid(); break;
			case "branch": rows[^1].PreviousRevisionId = rows[0].Id; break;
		}
		foreach (var row in rows)
		{
			var command = new CategoryMappingCommand(new(row.Location, row.Description, row.ItemCode), row.SourceFingerprint,
				row.PreviousRevisionId, row.TaxonomyRevisionId, row.CategoryId, row.OperationId, row.Reason);
			row.RequestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));
		}
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>().WithMessage(message);
	}

	[Fact]
	public void MappingDestination_RejectsChangedOriginalTimestampUnderSameIds()
	{
		var incoming = MappingState();
		var existing = incoming with
		{
			MappingRevisions = JsonSerializer.Deserialize<List<Infrastructure.Entities.Taxonomy.CategoryMappingRevisionEntity>>(
				JsonSerializer.Serialize(incoming.MappingRevisions))!
		};
		existing.MappingRevisions[0].RecordedAt = existing.MappingRevisions[0].RecordedAt.AddTicks(1);
		PortableTaxonomyArchive.Validate(existing);
		PortableTaxonomyArchive.Validate(incoming);
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateDestination(existing, incoming))
			.Should().Throw<InvalidDataException>().WithMessage("Restore conflicts with immutable destination history.");
	}
}
