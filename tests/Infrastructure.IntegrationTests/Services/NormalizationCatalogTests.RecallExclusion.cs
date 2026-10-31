using Application.Normalization;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	// RECEIPTS-1089 leave-one-out: the recall harness hides a source's own evidence through the
	// internal probe instead of revoking rows, which would fire mapping review invalidation.
	[Fact]
	public async Task RetrievalProbe_HidesTheEvaluatedMappingAndItsDescriptionButKeepsOtherStores()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Chicken breast");
		var own = await MappingAsync(new("Store A", "KS ORG CHKN BRST", "1001"), target.Id);
		await MappingAsync(new("Store B", "KS ORG CHKN BRST", null), target.Id);
		await MappingAsync(new("Store A", "CHKN BRST BNLS", null), target.Id);
		NormalizationRetrievalProbe probe = new() { HiddenMappingId = own.Id, HiddenDescription = own.Description };

		await using var db = fixture.CreateDbContext();
		var visible = await NormalizationCandidateRetrieval.RetrieveAsync(db, new("Store A", "KS ORG CHKN BRST", "1001"), null, CancellationToken.None);
		var hidden = await NormalizationCandidateRetrieval.RetrieveAsync(db, new("Store A", "KS ORG CHKN BRST", "1001"), null, CancellationToken.None, probe: probe);

		visible.Single(candidate => candidate.Id == target.Id).ApprovedExamples.Should().Contain("KS ORG CHKN BRST");
		var candidate = hidden.Single(candidate => candidate.Id == target.Id);
		candidate.Channels.Should().Contain("approved_example", "the same description approved at another store stays visible");
		candidate.ApprovedExamples.Should().Equal(["CHKN BRST BNLS"], "the evaluated description is hidden from every candidate's examples");
		probe.Pool[target.Id].Should().Contain("approved_example");
	}

	[Fact]
	public async Task RetrievalProbe_RemovesApprovedExampleEvidenceWhenTheEvaluatedMappingIsTheOnlyOne()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Chicken breast");
		var own = await MappingAsync(new("Store A", "KS ORG CHKN BRST", null), target.Id);
		NormalizationRetrievalProbe probe = new() { HiddenMappingId = own.Id, HiddenDescription = own.Description };

		await using var db = fixture.CreateDbContext();
		var catalog = await NormalizationCandidateRetrieval.RetrieveAsync(db, new("Store A", "KS ORG CHKN BRST", null), null, CancellationToken.None, probe: probe);
		var chooser = await NormalizationCandidateRetrieval.RetrieveForReceiptEntryAsync(db, new("Store A", "KS ORG CHKN BRST", null), null, CancellationToken.None, new() { HiddenMappingId = own.Id, HiddenDescription = own.Description });

		catalog.Should().NotContain(candidate => candidate.Id == target.Id, "no channel but the hidden mapping links this abbreviation");
		chooser.Should().NotContain(candidate => candidate.Id == target.Id);
		probe.Pool.Should().NotContainKey(target.Id);
		await using var verify = fixture.CreateDbContext();
		verify.NormalizationSourceMappings.Single().State.Should().Be("active", "the probe never revokes rows");
	}

	private async Task<NormalizationSourceMappingEntity> MappingAsync(NormalizationSourceKey source, Guid target)
	{
		await using var db = fixture.CreateDbContext();
		var mapping = new NormalizationSourceMappingEntity
		{
			Id = Guid.NewGuid(),
			SourceHash = NormalizationCatalogPolicy.Hash(source),
			Location = source.Location,
			Description = source.Description,
			ItemCode = source.ItemCode,
			TargetId = target,
			ApprovedBy = Actor,
			ApprovedAt = DateTimeOffset.UtcNow,
		};
		db.NormalizationSourceMappings.Add(mapping);
		await db.SaveChangesAsync();
		return mapping;
	}
}
