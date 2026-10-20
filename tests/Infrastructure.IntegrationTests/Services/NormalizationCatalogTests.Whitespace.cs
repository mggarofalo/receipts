using Application.Interfaces.Services;
using Application.Normalization;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData("location", "\t")]
	[InlineData("description", "\t")]
	[InlineData("code", "\t")]
	[InlineData("location", "\u00a0")]
	[InlineData("description", "\u00a0")]
	[InlineData("code", "\u00a0")]
	public async Task SignificantWhitespaceScope_ListPreviewAndFutureReuseAgreeWithoutCrossScopeAttachment(string field, string whitespace)
	{
		await ResetAsync();
		var key = field switch
		{
			"location" => Source with { Location = whitespace + Source.Location + whitespace },
			"description" => Source with { Description = whitespace + Source.Description + whitespace },
			_ => Source with { ItemCode = whitespace + Source.ItemCode + whitespace },
		};
		var target = await CanonicalAsync("Reviewed franks");
		var original = await ItemsAsync(key, 1);
		var decoy = await ItemsAsync(Source, 1);
		var service = Service();
		var list = await service.ListSourcesAsync(Actor, null, "all", 0, 25, CancellationToken.None);
		list.Items.Select(x => x.Source).Should().Contain(key).And.Contain(Source);
		var preview = await service.PreviewAsync(Actor, Request(target) with { Source = key }, CancellationToken.None);
		preview.Eligible.Should().Be(1);
		await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		(await ReadAsync(original)).Single().NormalizedDescriptionId.Should().Be(target.Id);
		var future = await ItemsAsync(key, 1);
		using var provider = OfflineProvider();
		using var resolver = new NormalizedDescriptionResolutionService(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IDescriptionChangeSignal>(), NullLogger<NormalizedDescriptionResolutionService>.Instance);
		await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);
		(await ReadAsync(future)).Single().NormalizedDescriptionId.Should().Be(target.Id);
		(await ReadAsync(decoy)).Single().NormalizedDescriptionId.Should().BeNull();
	}
}
