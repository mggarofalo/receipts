using Application.Normalization;
using FluentAssertions;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData("manual")]
	[InlineData("template")]
	[InlineData("reviewed")]
	public async Task ReceiptClassification_UnverifiedLegacyLinksRequireConsentButAreNotPermanentlyProtected(string origin)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Historical identity");
		await ItemsAsync(Source, 2, origin, target.Id);
		var service = Service();

		var page = await service.ListSourcesAsync(Actor, null, "needs-review", 0, 10, CancellationToken.None);
		var coverage = await service.GetCoverageAsync(Actor, CancellationToken.None);
		var withoutConsent = await service.PreviewAsync(Actor, new(Guid.NewGuid(), Source, target.Id, target.NormalizationRevision, null, false), CancellationToken.None);
		var withConsent = await service.PreviewAsync(Actor, new(Guid.NewGuid(), Source, target.Id, target.NormalizationRevision, null, true), CancellationToken.None);

		var source = page.Items.Should().ContainSingle().Subject;
		source.LegacyUnknownCount.Should().Be(2);
		source.ProtectedCount.Should().Be(0);
		coverage.LegacyUnknown.Should().Be(2);
		coverage.Protected.Should().Be(0);
		withoutConsent.Eligible.Should().Be(0);
		withConsent.Eligible.Should().Be(2);
		withConsent.Protected.Should().Be(0);
	}
}
