using FluentAssertions;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task AutomaticActivityIsOwnedAndSeparateFromManualOperations()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).AutomaticActivityCore();
	}

	private async Task AutomaticActivityCore()
	{
		var captured = await CaptureExecutableAsync();
		var manual = await HistoricalRequest(captured.Item, captured.Request.TaxonomyRevisionId, captured.Category);
		await Historical().SavePreviewAsync(manual);
		var page = await Historical().ListAutomaticAsync(Actor, 0, 1);
		page.Items.Should().ContainSingle().Which.OperationId.Should().Be(captured.Request.OperationId);
		page.Items[0].Groups.Should().ContainSingle().Which.Group.Should().Be(captured.Request.Group);
		page.NextOffset.Should().BeNull();
		(await Historical().ListAutomaticAsync("other-actor", 0, 25)).Items.Should().BeEmpty();
		(await Historical().ListAutomaticAsync(Actor, 1, 1)).Items.Should().BeEmpty();
		(await Historical().ListAsync(Actor, 0, 25)).Items.Should().ContainSingle().Which.OperationId.Should().Be(manual.OperationId);
		await Assert.ThrowsAsync<ArgumentException>(() => Historical().ListAutomaticAsync(Actor, -1, 25));
	}
}
