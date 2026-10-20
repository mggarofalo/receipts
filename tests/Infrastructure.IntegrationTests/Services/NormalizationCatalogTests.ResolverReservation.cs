using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task HistoryPendingMembers_AreReservedFromResolverWhileNewPurchasesUseApprovedMapping(bool fuzzyConfigured)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks");
		var original = await ItemsAsync(Source, 105);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		var first = await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		first.Applied.Should().Be(100);
		var future = await ItemsAsync(Source, 1);
		var fuzzy = new Moq.Mock<INormalizedDescriptionService>();
		fuzzy.Setup(x => x.GetEmbeddingCoverageAsync(Moq.It.IsAny<CancellationToken>())).ReturnsAsync(new Application.Models.NormalizedDescriptions.EmbeddingCoverage(OnnxEmbeddingService.EmbeddingSpaceFingerprint, 0, 0, 0, 0));
		var embeddings = new Moq.Mock<IEmbeddingService>();
		embeddings.SetupGet(x => x.IsConfigured).Returns(true);
		using (var provider = fuzzyConfigured ? new ServiceCollection().AddSingleton(fuzzy.Object).AddSingleton(embeddings.Object).AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<Infrastructure.ApplicationDbContext>>(new Factory(fixture)).AddSingleton<IDescriptionChangeSignal, DescriptionChangeSignal>().BuildServiceProvider() : OfflineProvider())
		using (var resolver = new NormalizedDescriptionResolutionService(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IDescriptionChangeSignal>(), NullLogger<NormalizedDescriptionResolutionService>.Instance))
		{ await resolver.ProcessPendingResolutionsAsync(CancellationToken.None); await resolver.ProcessPendingResolutionsAsync(CancellationToken.None); }
		fuzzy.VerifyNoOtherCalls();
		embeddings.VerifyNoOtherCalls();
		(await ReadAsync(original)).Count(x => x.NormalizedDescriptionId == null).Should().Be(5, "pending historical members belong to the durable operation, not the general resolver");
		(await ReadAsync(future)).Single().NormalizedDescriptionId.Should().Be(target.Id, "an approved rule still handles future purchases outside the frozen historical snapshot");
		var completed = await service.ResumeAsync(Actor, first.Id, CancellationToken.None);
		completed.Applied.Should().Be(105);
		completed.Conflicted.Should().Be(0);
		var undo = await service.UndoAsync(Actor, completed.Id, completed.Revision, CancellationToken.None);
		if (undo.State == "undoing")
		{
			undo = await service.ResumeAsync(Actor, undo.Id, CancellationToken.None);
		}

		undo.Undone.Should().Be(105);
		(await ReadAsync(original)).Should().OnlyContain(x => x.NormalizedDescriptionId == null);
		(await ReadAsync(future)).Single().NormalizedDescriptionId.Should().Be(target.Id, "undo restores only its frozen historical members, preserving later future assignments");
	}
}
