using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task Sources_LinkedTargetsPreserveStatusAndExactScopeWhileBoundingDisplayedIdentities()
	{
		await ResetAsync();
		List<NormalizedDescriptionEntity> targets = [];
		for (int i = 0; i < 7; i++)
		{
			var target = await CanonicalAsync($"Identity {i}");
			targets.Add(target);
			await ItemsAsync(Source, 1, "legacy_unknown", target.Id);
		}
		var excluded = await CanonicalAsync("A different scope");
		await ItemsAsync(Source with { Location = "Store B" }, 1, "legacy_unknown", excluded.Id);
		await ItemsAsync(Source with { ItemCode = "456" }, 1, "legacy_unknown", excluded.Id);
		await ItemsAsync(Source with { Description = Source.Description.ToLowerInvariant() }, 1, "legacy_unknown", excluded.Id);
		await ItemsAsync(Source with { ItemCode = null }, 1, "legacy_unknown", excluded.Id);
		await ItemsAsync(Source with { ItemCode = Source.ItemCode + "\t" }, 1, "legacy_unknown", excluded.Id);
		await ItemsAsync(Source with { Location = " Store A ", Description = $" {Source.Description} ", ItemCode = " 123 " },
			2, "legacy_unknown", targets[0].Id);
		var deletedItem = await ItemsAsync(Source, 1, "legacy_unknown", excluded.Id);
		var deletedParent = await ItemsAsync(Source, 1, "legacy_unknown", excluded.Id);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.NormalizedDescriptions.SingleAsync(x => x.Id == targets[0].Id)).Status = NormalizedDescriptionStatus.PendingReview;
			(await db.NormalizedDescriptions.SingleAsync(x => x.Id == targets[1].Id)).Status = NormalizedDescriptionStatus.Rejected;
			(await db.NormalizedDescriptions.SingleAsync(x => x.Id == targets[2].Id)).DisplayLabel = "Readable identity";
			(await db.ReceiptItems.SingleAsync(x => x.Id == deletedItem[0])).DeletedAt = DateTimeOffset.UtcNow;
			var parentId = (await db.ReceiptItems.SingleAsync(x => x.Id == deletedParent[0])).ReceiptId;
			(await db.Receipts.SingleAsync(x => x.Id == parentId)).DeletedAt = DateTimeOffset.UtcNow;
			await db.SaveChangesAsync();
		}

		var page = await Service().ListSourcesAsync(Actor, null, "all", 0, 50, CancellationToken.None);
		var group = page.Items.Should().ContainSingle(x => x.Source == Source).Subject;
		group.ItemCount.Should().Be(9);
		group.LinkedTargetCount.Should().Be(7, "duplicate observations must not count as distinct identities");
		group.LinkedTargets.Should().HaveCount(5);
		group.LinkedTargets!.Select(x => x.Id).Should().BeEquivalentTo(targets.Where((_, i) => i is 0 or 1 or 3 or 4 or 5).Select(x => x.Id));
		group.LinkedTargets.Should().ContainSingle(x => x.Id == targets[0].Id && x.Status == NormalizedDescriptionStatus.PendingReview);
		group.LinkedTargets.Should().ContainSingle(x => x.Id == targets[1].Id && x.Status == NormalizedDescriptionStatus.Rejected);
		group.LinkedTargets.Should().ContainSingle(x => x.Id == targets[3].Id && x.Status == NormalizedDescriptionStatus.Active && x.Name == "Identity 3");
		group.LinkedTargets.Should().OnlyContain(x => x.Id != excluded.Id);
		var fresh = await Service().ListSourcesAsync(Actor, null, "all", 0, 50, CancellationToken.None);
		fresh.Items.Single(x => x.Source == Source).LinkedTargets.Should().Equal(group.LinkedTargets);
	}

	[Theory]
	[InlineData(NormalizedDescriptionStatus.PendingReview, "pending_identity_requires_review", "pendingReview")]
	[InlineData(NormalizedDescriptionStatus.Rejected, "name_exists", "rejected")]
	[InlineData(NormalizedDescriptionStatus.Active, "name_exists", "active")]
	public async Task Preview_NameCollisionReturnsExactIdentityWithoutActivatingOrAssigningIt(
		NormalizedDescriptionStatus status, string code, string wireStatus)
	{
		await ResetAsync();
		var collision = await CanonicalAsync("Unrelated canonical key");
		var ids = await ItemsAsync(Source, 1);
		await using (var db = fixture.CreateDbContext())
		{
			var saved = await db.NormalizedDescriptions.SingleAsync(x => x.Id == collision.Id);
			saved.DisplayLabel = "Proposed readable name";
			saved.Status = status;
			await db.SaveChangesAsync();
		}
		var facts = await FactsAsync(ids);
		int audits = await AuditCountAsync(ids);
		Func<Task> preview = () => Service().PreviewAsync(Actor,
			new(Guid.NewGuid(), Source, null, null, "proposed READABLE name", false), CancellationToken.None);
		var error = (await preview.Should().ThrowAsync<NormalizationCatalogException>()).Which;
		error.StatusCode.Should().Be(409);
		error.Code.Should().Be(code);
		error.Extensions.Should().Contain("normalizedDescriptionId", collision.Id)
			.And.Contain("normalizedDescriptionStatus", wireStatus);
		await using var check = fixture.CreateDbContext();
		(await check.NormalizedDescriptions.SingleAsync(x => x.Id == collision.Id)).Status.Should().Be(status);
		(await check.NormalizationCatalogOperations.CountAsync()).Should().Be(0);
		(await check.NormalizationSourceMappings.CountAsync()).Should().Be(0);
		(await ReadAsync(ids)).Single().NormalizedDescriptionId.Should().BeNull();
		(await FactsAsync(ids)).Should().BeEquivalentTo(facts);
		(await AuditCountAsync(ids)).Should().Be(audits);
	}
}
