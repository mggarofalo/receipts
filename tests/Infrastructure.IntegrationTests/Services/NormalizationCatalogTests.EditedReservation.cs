using Application.Interfaces.Services;
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
	public async Task ChangedHistoricalObservation_IsReleasedFromOldReservationAndMayResolveCurrentExactIdentity(string changedField)
	{
		await ResetAsync();
		var reviewed = await CanonicalAsync("Reviewed mapping target");
		var exact = await CanonicalAsync(Source.Description);
		var changed = await CanonicalAsync("Changed source exact name");
		var ids = await ItemsAsync(Source, 105);
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
		await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);
		await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);
		var current = (await ReadAsync([pending.Id])).Single();
		current.NormalizedDescriptionId.Should().Be(changedField == "description" ? changed.Id : exact.Id);
		current.NormalizationOrigin.Should().Be("automatic");
		current.NormalizationMappingId.Should().BeNull();
		if (changedField != "location")
		{
			(await ReadAsync(ids)).Count(x => x.NormalizedDescriptionId == null).Should().Be(4, "other unchanged historical observations remain reserved");
		}
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
		await Service().PreviewAsync(Actor, Request(proposed), CancellationToken.None);
		using var provider = OfflineProvider();
		using var resolver = new NormalizedDescriptionResolutionService(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IDescriptionChangeSignal>(), NullLogger<NormalizedDescriptionResolutionService>.Instance);
		await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);
		(await ReadAsync(ids)).Single().NormalizedDescriptionId.Should().Be(exact.Id);
		(await Service().ListMappingsAsync(Actor, Source, CancellationToken.None)).Should().BeEmpty();
	}
}
