using Application.Interfaces.Services;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task CanonicalMergeBetweenChunks_CancelsStaleHistoryAndAllowsFreshPreviewAgainstRedirectedMapping()
	{
		await ResetAsync();
		var original = await CanonicalAsync("Original beef franks");
		var survivor = await CanonicalAsync("Surviving beef franks");
		var ids = await ItemsAsync(Source, 105);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(original), CancellationToken.None);
		var first = await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		first.Applied.Should().Be(100);
		using (var provider = OfflineProvider())
		{
			await provider.GetRequiredService<INormalizedDescriptionService>().MergeAsync(survivor.Id, original.Id, CancellationToken.None);
		}
		var cancelled = (await service.GetOperationAsync(Actor, first.Id, CancellationToken.None))!;
		cancelled.State.Should().Be("cancelled");
		cancelled.ErrorCode.Should().Be("target_changed");
		using (var provider = OfflineProvider())
		using (var resolver = new Infrastructure.Services.NormalizedDescriptionResolutionService(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IDescriptionChangeSignal>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.NormalizedDescriptionResolutionService>.Instance))
		{ await resolver.ProcessPendingResolutionsAsync(CancellationToken.None); }
		var afterMerge = await ReadAsync(ids);
		afterMerge.Count(x => x.NormalizedDescriptionId == survivor.Id).Should().Be(100);
		afterMerge.Count(x => x.NormalizedDescriptionId == null).Should().Be(5);
		afterMerge.Should().NotContain(x => x.NormalizedDescriptionId == original.Id);
		Func<Task> staleResume = () => service.ResumeAsync(Actor, first.Id, CancellationToken.None);
		(await staleResume.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);
		var mapping = (await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Single();
		mapping.State.Should().Be("active");
		mapping.TargetId.Should().Be(survivor.Id);
		await using var read = fixture.CreateDbContext();
		NormalizedDescriptionEntity current = await read.NormalizedDescriptions.SingleAsync(x => x.Id == survivor.Id);
		var fresh = await service.PreviewAsync(Actor, Request(current), CancellationToken.None);
		fresh.Eligible.Should().Be(5);
		Func<Task> implicitReuse = () => service.ApproveAsync(Actor, fresh.Id, fresh.Revision, CancellationToken.None);
		(await implicitReuse.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);
		await service.RevokeAsync(Actor, mapping.Id, mapping.Revision, CancellationToken.None);
		var revokedRows = await ReadAsync(ids);
		revokedRows.Should().OnlyContain(x => x.NormalizationReviewState == NormalizationReviewState.NeedsReview);
		revokedRows.Count(x => x.NormalizedDescriptionId == survivor.Id).Should().Be(100,
			"revoking mapping-dependent proof preserves historical identity links");
		revokedRows.Count(x => x.NormalizedDescriptionId == null).Should().Be(5);
		var replacement = await service.PreviewAsync(Actor, Request(current), CancellationToken.None);
		replacement.Eligible.Should().Be(105, "revocation requires fresh approval of every dependent observation");
		replacement.Protected.Should().Be(0);
		var resumed = await service.ApproveAsync(Actor, replacement.Id, replacement.Revision, CancellationToken.None);
		resumed.Applied.Should().Be(100);
		resumed = await service.ResumeAsync(Actor, replacement.Id, CancellationToken.None);
		resumed.Applied.Should().Be(105);
		resumed.State.Should().Be("completed");
		(await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Count(x => x.State == "active").Should().Be(1);
		(await ReadAsync(ids)).Should().OnlyContain(x => x.NormalizedDescriptionId == survivor.Id);
	}
}
