using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData("unassigned", false)]
	[InlineData("automatic", false)]
	[InlineData("legacy_unknown", false)]
	[InlineData("legacy_unknown", true)]
	[InlineData("manual", true)]
	[InlineData("raw-rejected", true)]
	[InlineData("template-linked", true)]
	[InlineData("revoked-owned", true)]
	public async Task ReadProjectionAndPreview_AgreeWithAuthoritativeEligibilityOnBoundaryFacts(string scenario, bool includeLegacy)
	{
		await ResetAsync();
		var existing = await CanonicalAsync("Old catalog name");
		var target = await CanonicalAsync("Reviewed replacement");
		string origin = scenario is "raw-rejected" ? "unassigned" : scenario is "template-linked" ? "automatic" : scenario is "revoked-owned" ? "unassigned" : scenario;
		await ItemsAsync(Source, 1, origin, origin == "unassigned" ? null : existing.Id);
		var service = Service();
		if (scenario == "revoked-owned")
		{
			var p = await service.PreviewAsync(Actor, Request(existing), CancellationToken.None);
			await service.ApproveAsync(Actor, p.Id, p.Revision, CancellationToken.None);
			var mapping = (await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Single();
			await service.RevokeAsync(Actor, mapping.Id, mapping.Revision, CancellationToken.None);
			origin = "mapped";
		}
		await using (var db = fixture.CreateDbContext())
		{
			if (scenario == "raw-rejected")
			{
				db.NormalizedDescriptions.Add(new() { Id = Guid.NewGuid(), CanonicalName = Source.Description, Status = NormalizedDescriptionStatus.Rejected, CreatedAt = DateTimeOffset.UtcNow });
			}

			if (scenario == "template-linked")
			{
				db.ItemTemplates.Add(new ItemTemplateEntity { Id = Guid.NewGuid(), Name = "Protected template", NormalizedDescriptionId = existing.Id });
			}

			await db.SaveChangesAsync();
		}
		var decision = NormalizationCatalogPolicy.Classify(new(origin, true, true, false, scenario == "raw-rejected", scenario == "template-linked", scenario == "revoked-owned"), includeLegacy);
		var all = await service.ListSourcesAsync(Actor, null, "all", 0, 25, CancellationToken.None);
		var row = all.Items.Should().ContainSingle().Subject;
		row.ProtectedCount.Should().Be(decision.Protected ? 1 : 0);
		row.RevisableCount.Should().Be(scenario == "revoked-owned" ? 1 : 0);
		var preview = await service.PreviewAsync(Actor, Request(target) with { IncludeLegacyUnknown = includeLegacy }, CancellationToken.None);
		preview.Eligible.Should().Be(decision.Eligible ? 1 : 0);
		preview.Protected.Should().Be(decision.Eligible ? 0 : 1);
		var coverage = await service.GetCoverageAsync(Actor, CancellationToken.None);
		coverage.TotalItems.Should().Be(1);
		coverage.Protected.Should().Be(decision.Protected ? 1 : 0);
		coverage.Revisable.Should().Be(scenario == "revoked-owned" ? 1 : 0);
	}
}
