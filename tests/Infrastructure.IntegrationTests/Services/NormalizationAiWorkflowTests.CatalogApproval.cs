using System.Text.Json;
using Application.Normalization;
using Application.NormalizationAi;
using FluentAssertions;
using Infrastructure.Services;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationAiWorkflowTests
{
	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public async Task SourceSuggestionApproval_KeepsSiblingPreviewValidAndFinishesRunOnlyAfterLastExplicitApproval(bool overrideIdentity, bool includeLegacyAtReview)
	{
		await SeedAsync();
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((ChatGptInferenceRequest request, CancellationToken _) =>
		{
			using var input = JsonDocument.Parse(request.Prompt);
			return new ChatGptInferenceResult(JsonSerializer.Serialize(new { results = input.RootElement.EnumerateArray().Select(row => new { id = row.GetProperty("id").GetGuid(), identityDecision = "different", candidateId = (Guid?)null, newName = "Reviewed " + row.GetProperty("description").GetString(), reason = "Explicit identity evidence" }).ToArray() }));
		});
		var ai = Service();
		var queued = await ai.StartSourcesAsync(Owner, Guid.NewGuid(), [new(new("Synthetic store", "Milk", null), false), new(new("Synthetic store", "Bread", null), false)], "test-model", CancellationToken.None);
		await ai.ProcessNextAsync(CancellationToken.None);
		var run = (await ai.GetRunAsync(Owner, queued.Id, CancellationToken.None))!;
		var catalog = new NormalizationCatalogService(new Factory(fixture), TimeProvider.System, publisher.Object);
		List<NormalizationCatalogOperation> previews = [];
		foreach (var proposal in run.Proposals)
		{
			previews.Add(await catalog.PreviewAsync(Owner, new(Guid.NewGuid(), proposal.Source!, null, null, overrideIdentity ? "Human corrected " + proposal.Description : proposal.CanonicalName, includeLegacyAtReview, false, run.Id, proposal.Id, run.Revision), CancellationToken.None));
		}
		var first = await catalog.ApproveAsync(Owner, previews[0].Id, previews[0].Revision, CancellationToken.None);
		first.Applied.Should().Be(1);
		var partly = (await ai.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		partly.State.Should().Be("ready");
		partly.Revision.Should().Be(run.Revision, "an already reviewed sibling preview remains bound to the unchanged proposal evidence");
		partly.Proposals.Count(x => x.State == (overrideIdentity ? "overridden" : "applied")).Should().Be(1);
		var second = await catalog.ApproveAsync(Owner, previews[1].Id, previews[1].Revision, CancellationToken.None);
		second.Applied.Should().Be(1);
		var finished = (await ai.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		finished.State.Should().Be("applied");
		finished.Revision.Should().NotBe(run.Revision);
		finished.Proposals.Should().OnlyContain(x => x.State == (overrideIdentity ? "overridden" : "applied"));
		Func<Task> replay = () => catalog.PreviewAsync(Owner, new(Guid.NewGuid(), run.Proposals[0].Source!, null, null, "Another name", false, false, run.Id, run.Proposals[0].Id, run.Revision), CancellationToken.None);
		(await replay.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);
	}

	[Fact]
	public async Task CatalogPreview_RejectsSuggestionSourceSubstitutionBeforeCreatingHistoryOperation()
	{
		await SeedAsync();
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((ChatGptInferenceRequest request, CancellationToken _) =>
		{
			using var input = JsonDocument.Parse(request.Prompt);
			return new ChatGptInferenceResult(JsonSerializer.Serialize(new { results = input.RootElement.EnumerateArray().Select(row => new { id = row.GetProperty("id").GetGuid(), identityDecision = "different", candidateId = (Guid?)null, newName = "Reviewed milk", reason = "Synthetic" }).ToArray() }));
		});
		var ai = Service();
		var queued = await ai.StartSourcesAsync(Owner, Guid.NewGuid(), [new(new("Synthetic store", "Milk", null), false)], "test-model", CancellationToken.None);
		await ai.ProcessNextAsync(CancellationToken.None);
		var run = (await ai.GetRunAsync(Owner, queued.Id, CancellationToken.None))!;
		var catalog = new NormalizationCatalogService(new Factory(fixture), TimeProvider.System, publisher.Object);
		Func<Task> substitute = () => catalog.PreviewAsync(Owner, new(Guid.NewGuid(), new("Synthetic store", "Bread", null), null, null, "Reviewed milk", false, false, run.Id, run.Proposals.Single().Id, run.Revision), CancellationToken.None);
		(await substitute.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);
		(await catalog.ListOperationsAsync(Owner, CancellationToken.None)).Should().BeEmpty();
	}
}
