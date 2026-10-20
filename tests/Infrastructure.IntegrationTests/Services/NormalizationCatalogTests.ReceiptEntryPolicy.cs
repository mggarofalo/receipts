using Application.Interfaces.Services;
using Domain;
using Domain.Core;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task ReceiptEntry_ApprovedScopedMappingResolvesInsideLegacySaveWithoutModel()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Reviewed beef franks 8 count");
		await ItemsAsync(Source, 1);
		var catalog = Service();
		var preview = await catalog.PreviewAsync(Actor, Request(target), CancellationToken.None);
		await catalog.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		var mapping = (await catalog.ListMappingsAsync(Actor, Source, CancellationToken.None)).Single();
		var writer = LegacyReceiptWriter();
		var receipt = new Receipt(Guid.NewGuid(), Source.Location, new(2026, 10, 20), new Money(0));
		var item = new ReceiptItem(Guid.NewGuid(), Source.ItemCode, Source.Description, 2, new Money(3.125m), new Money(6.25m), "Food", null);

		var saved = await writer.CreateAsync(receipt, [], [item], [], CancellationToken.None);

		var persisted = (await ReadAsync([saved.Items.Single().Id])).Single();
		persisted.NormalizedDescriptionId.Should().Be(target.Id, "known approved scope must resolve during save, before any worker can run");
		persisted.NormalizationMappingId.Should().Be(mapping.Id);
		persisted.Description.Should().Be(Source.Description);
		persisted.Quantity.Should().Be(2);
		persisted.UnitPrice.Should().Be(3.125m);
		persisted.TotalAmount.Should().Be(6.25m);
	}

	[Fact]
	public async Task ReceiptEntry_GlobalExactCanonicalCannotAutomaticallyAssignAnUnreviewedPurchase()
	{
		await ResetAsync();
		await CanonicalAsync(Source.Description);
		var ids = await ItemsAsync(Source, 1);
		var facts = await FactsAsync(ids);
		using var provider = OfflineProvider();
		using var resolver = EntryResolver(provider);

		await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);

		(await ReadAsync(ids)).Single().NormalizedDescriptionId.Should().BeNull("equal text without an approved source decision is not product identity evidence");
		(await FactsAsync(ids)).Should().BeEquivalentTo(facts);
	}

	[Fact]
	public async Task ReceiptEntry_UnknownLegacyPurchaseNeverCreatesTrustedIdentityInBackground()
	{
		await ResetAsync();
		var ids = await ItemsAsync(Source, 1);
		var facts = await FactsAsync(ids);
		var embeddings = new Mock<IEmbeddingService>();
		embeddings.SetupGet(x => x.IsConfigured).Returns(true);
		var vector = new float[OnnxEmbeddingService.EmbeddingDimension];
		vector[0] = 1;
		embeddings.Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(vector);
		using var provider = new ServiceCollection()
			.AddSingleton(embeddings.Object)
			.AddSingleton<NormalizedDescriptionMapper>()
			.AddSingleton<NormalizedDescriptionSettingsMapper>()
			.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture))
			.AddScoped<INormalizedDescriptionService, NormalizedDescriptionService>()
			.AddSingleton<IDescriptionChangeSignal, DescriptionChangeSignal>()
			.BuildServiceProvider();
		using var resolver = EntryResolver(provider);

		await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);

		await using var db = fixture.CreateDbContext();
		(await db.NormalizedDescriptions.CountAsync(x => x.Status == NormalizedDescriptionStatus.Active)).Should().Be(0, "model similarity and unfamiliar spelling cannot approve a new catalog identity");
		(await ReadAsync(ids)).Single().NormalizedDescriptionId.Should().BeNull();
		(await FactsAsync(ids)).Should().BeEquivalentTo(facts);
	}

	private CompleteReceiptWriter LegacyReceiptWriter() => new(new Factory(fixture), new ReceiptMapper(), new TransactionMapper(), new ReceiptItemMapper(), new AdjustmentMapper());

	private static NormalizedDescriptionResolutionService EntryResolver(ServiceProvider provider) => new(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IDescriptionChangeSignal>(), NullLogger<NormalizedDescriptionResolutionService>.Instance);
}
