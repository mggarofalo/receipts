using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task GlobalLabel_RequiresExplicitApprovalAndPreservesEveryAssignmentAndPurchaseFact()
	{
		await ResetAsync();
		var target = await CanonicalAsync("BF FRANKS 8CT");
		var ids = (await ItemsAsync(Source, 2, "manual", target.Id))
			.Concat(await ItemsAsync(Source with { Location = "Other store" }, 1, "reviewed", target.Id)).ToArray();
		var facts = await FactsAsync(ids);
		var before = await ReadAsync(ids);
		int itemAudits = await AuditCountAsync(ids);
		var service = Service();
		var review = await service.PreviewLabelAsync(Actor, target.Id, "Beef franks, 8 count", CancellationToken.None);
		review.AffectedItemCount.Should().Be(3, "a display name changes the product globally, not just the selected source");
		await using (var db = fixture.CreateDbContext())
		{
			(await db.NormalizedDescriptions.SingleAsync(x => x.Id == target.Id)).DisplayLabel.Should().BeNull();
		}

		await service.ApproveLabelAsync(Actor, target.Id,
			new(review.ProposedDisplayLabel, review.TargetRevision, review.AffectedItemCount), CancellationToken.None);
		await using (var db = fixture.CreateDbContext())
		{
			var renamed = await db.NormalizedDescriptions.SingleAsync(x => x.Id == target.Id);
			renamed.DisplayLabel.Should().Be("Beef franks, 8 count");
			renamed.CanonicalName.Should().Be("BF FRANKS 8CT");
			renamed.Status.Should().Be(NormalizedDescriptionStatus.Active);
		}
		(await FactsAsync(ids)).Should().BeEquivalentTo(facts);
		(await ReadAsync(ids)).Select(x => new { x.Id, x.NormalizedDescriptionId, x.NormalizationOrigin, x.NormalizationAssignmentId })
			.Should().BeEquivalentTo(before.Select(x => new { x.Id, x.NormalizedDescriptionId, x.NormalizationOrigin, x.NormalizationAssignmentId }));
		(await AuditCountAsync(ids)).Should().Be(itemAudits, "a display label update must not rewrite purchase assignments");
	}

	[Theory]
	[InlineData("count")]
	[InlineData("rename")]
	[InlineData("reject")]
	public async Task GlobalLabel_RejectsStaleImpactOrTargetWithoutOverwritingLaterCuration(string change)
	{
		await ResetAsync();
		var target = await CanonicalAsync("BF FRANKS");
		await ItemsAsync(Source, 1, "manual", target.Id);
		var service = Service();
		var review = await service.PreviewLabelAsync(Actor, target.Id, "Beef franks", CancellationToken.None);
		if (change == "count")
		{
			await ItemsAsync(Source, 1, "manual", target.Id);
		}
		else
		{
			await using var db = fixture.CreateDbContext();
			var entity = await db.NormalizedDescriptions.SingleAsync(x => x.Id == target.Id);
			if (change == "rename")
			{
				entity.DisplayLabel = "Later human curation";
			}
			else
			{
				entity.Status = NormalizedDescriptionStatus.Rejected;
			}

			await db.SaveChangesAsync();
		}
		Func<Task> approve = () => service.ApproveLabelAsync(Actor, target.Id,
			new(review.ProposedDisplayLabel, review.TargetRevision, review.AffectedItemCount), CancellationToken.None);
		(await approve.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);
		await using var verify = fixture.CreateDbContext();
		(await verify.NormalizedDescriptions.SingleAsync(x => x.Id == target.Id)).DisplayLabel
			.Should().Be(change == "rename" ? "Later human curation" : null);
	}
}
