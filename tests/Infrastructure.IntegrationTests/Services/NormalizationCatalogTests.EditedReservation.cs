using Application.Interfaces.Services;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData("description")]
	[InlineData("code")]
	[InlineData("location")]
	public async Task ChangedHistoricalObservation_IsReleasedFromOldReservationAndResolvesOnlyApprovedCurrentScope(string changedField)
	{
		await ResetAsync();
		var reviewed = await CanonicalAsync("Reviewed mapping target");
		var exact = await CanonicalAsync(Source.Description);
		var changed = await CanonicalAsync("Changed source exact name");
		var ids = await ItemsAsync(Source, 105);
		var originalFacts = await FactsAsync(ids);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(reviewed), CancellationToken.None);
		await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		var pending = (await ReadAsync(ids)).First(x => x.NormalizedDescriptionId == null);
		await using (var edit = fixture.CreateDbContext())
		{
			var item = await edit.ReceiptItems.SingleAsync(x => x.Id == pending.Id);
			if (changedField == "description")
			{
				item.Description = changed.CanonicalName;
			}
			else if (changedField == "code")
			{
				item.ReceiptItemCode = "NEW-CODE";
			}
			else
			{
				(await edit.Receipts.SingleAsync(x => x.Id == item.ReceiptId)).Location = "New store";
			}

			await edit.SaveChangesAsync();
		}
		using var provider = OfflineProvider();
		using var resolver = new NormalizedDescriptionResolutionService(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IDescriptionChangeSignal>(), NullLogger<NormalizedDescriptionResolutionService>.Instance);
		(await resolver.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(0,
			"an exact canonical name cannot authorize the changed source, while unchanged observations stay reserved");
		var awaitingApproval = (await ReadAsync([pending.Id])).Single();
		awaitingApproval.NormalizedDescriptionId.Should().BeNull();
		awaitingApproval.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		var newSource = Source with
		{
			Location = changedField == "location" ? "New store" : Source.Location,
			Description = changedField == "description" ? changed.CanonicalName : Source.Description,
			ItemCode = changedField == "code" ? "NEW-CODE" : Source.ItemCode,
		};
		Guid mappingId = await ApproveResolverSourceAsync(newSource, changedField == "description" ? changed.Id : exact.Id);
		if (changedField == "location")
		{
			// The shared parent changed the source of all 105 observations, including the 100
			// previously applied rows. The resolver must process every current source in bounded chunks.
			(await ReadAsync(ids)).Should().OnlyContain(item => item.NormalizedDescriptionId == null);
			(await resolver.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(100);
			(await resolver.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(5);
			(await resolver.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(0);
			var relocated = await ReadAsync(ids);
			relocated.Should().HaveCount(105);
			relocated.Should().OnlyContain(item => item.NormalizedDescriptionId == exact.Id
				&& item.NormalizationMappingId == mappingId && item.NormalizationReviewState == NormalizationReviewState.Verified);
			(await FactsAsync(ids)).Should().BeEquivalentTo(originalFacts,
				"moving the parent store and reclassifying its observations must preserve every purchase fact");
		}
		else
		{
			(await resolver.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(1);
		}
		var current = (await ReadAsync([pending.Id])).Single();
		current.NormalizedDescriptionId.Should().Be(changedField == "description" ? changed.Id : exact.Id);
		current.NormalizationOrigin.Should().Be("mapped");
		current.NormalizationMappingId.Should().Be(mappingId);
		current.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		current.NormalizedDescriptionMatchScore.Should().BeNull();
		if (changedField != "location")
		{
			(await ReadAsync(ids)).Count(x => x.NormalizedDescriptionId == null).Should().Be(4, "other unchanged historical observations remain reserved");
		}
	}
	private async Task<Guid> ApproveResolverSourceAsync(NormalizationSourceKey source, Guid targetId)
	{
		await using var db = fixture.CreateDbContext();
		var key = NormalizationCatalogPolicy.Normalize(source);
		Guid id = Guid.NewGuid();
		// Persist reusable approval only; leave history untouched for the real resolver to exercise.
		db.NormalizationSourceMappings.Add(new()
		{
			Id = id,
			SourceHash = NormalizationCatalogPolicy.Hash(key),
			Location = key.Location,
			Description = key.Description,
			ItemCode = key.ItemCode,
			TargetId = targetId,
			ApprovedBy = Actor,
			ApprovedAt = DateTimeOffset.UtcNow,
		});
		await db.SaveChangesAsync();
		return id;
	}

}

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task UnapprovedPreview_DoesNotReserveReceiptItemsFromOrdinaryResolution()
	{
		await ResetAsync();
		var exact = await CanonicalAsync(Source.Description);
		var proposed = await CanonicalAsync("Unapproved different identity");
		var ids = await ItemsAsync(Source, 1);
		var preview = await Service().PreviewAsync(Actor, Request(proposed), CancellationToken.None);
		using var provider = OfflineProvider();
		using var resolver = new NormalizedDescriptionResolutionService(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IDescriptionChangeSignal>(), NullLogger<NormalizedDescriptionResolutionService>.Instance);
		(await resolver.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(0);
		(await ReadAsync(ids)).Single().NormalizedDescriptionId.Should().BeNull(
			"neither a global exact name nor an unapproved preview authorizes assignment");
		(await Service().ListMappingsAsync(Actor, Source, CancellationToken.None)).Should().BeEmpty(
			"preview must not publish reusable authority");
		Guid approvedMapping = await ApproveResolverSourceAsync(Source, exact.Id);
		(await ReadAsync(ids)).Single().NormalizedDescriptionId.Should().BeNull("publishing a mapping alone does not apply its history");
		(await resolver.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(1,
			"an unapproved historical preview must not reserve observations from an independently approved mapping");
		var resolved = (await ReadAsync(ids)).Single();
		resolved.NormalizedDescriptionId.Should().Be(exact.Id);
		resolved.NormalizationMappingId.Should().Be(approvedMapping);
		resolved.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		var persistedPreview = await Service().GetOperationAsync(Actor, preview.Id, CancellationToken.None);
		persistedPreview.Should().NotBeNull();
		persistedPreview!.State.Should().Be("preview");
		persistedPreview.Applied.Should().Be(0);
		persistedPreview.MappingId.Should().BeNull();
		var mappings = await Service().ListMappingsAsync(Actor, Source, CancellationToken.None);
		mappings.Should().ContainSingle();
		mappings.Single().Id.Should().Be(approvedMapping);
		mappings.Single().TargetId.Should().Be(exact.Id);

	}
}
