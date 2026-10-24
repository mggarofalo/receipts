using Application.Interfaces.Services;
using Application.Models.Search;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task RenamedDisplayLabel_SpendingDrilldownAndSearchKeepIdentityAndOriginalFinancialFacts()
	{
		await ResetAsync();
		var target = await CanonicalAsync("BF FRANKS 8CT");
		var ids = await ItemsAsync(Source, 2, "manual", target.Id);
		await ReviewExistingAsync(ids, target);
		var facts = await FactsAsync(ids);
		using var provider = OfflineProvider();
		await provider.GetRequiredService<INormalizedDescriptionService>()
			.RenameAsync(target.Id, "Beef franks, 8 count", CancellationToken.None);

		var reports = new ReportService(new Factory(fixture));
		var spending = await reports.GetSpendingByNormalizedDescriptionAsync(
			null, null, "totalAmount", "desc", 1, 50, CancellationToken.None);
		var bucket = spending.Items.Should().ContainSingle().Subject;
		bucket.CanonicalName.Should().Be("Beef franks, 8 count");
		bucket.TotalAmount.Should().Be(6.80m);
		var history = await reports.GetItemCostOverTimeAsync(
			null, null, null, null, "monthly", bucket.CanonicalName, CancellationToken.None);
		history.Buckets.Should().ContainSingle().Which.Amount.Should().Be(2.8456m);
		var legacyLink = await reports.GetItemCostOverTimeAsync(
			null, null, null, null, "monthly", target.CanonicalName, CancellationToken.None);
		legacyLink.Should().BeEquivalentTo(history, "existing canonical-name report links must remain usable");
		var search = await new PurchaseSearchReader(new Factory(fixture)).SearchAsync(
			bucket.CanonicalName, 0, 25, new PurchaseSearchOptions(), CancellationToken.None);
		search.Items.Select(x => x.Id).Should().BeEquivalentTo(ids);
		(await ReadAsync(ids)).Should().OnlyContain(x => x.NormalizedDescriptionId == target.Id);
		(await FactsAsync(ids)).Should().BeEquivalentTo(facts);
	}
}
