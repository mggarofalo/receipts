using System.Text.Json;
using Application.Normalization;
using Application.NormalizationAi;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationAiWorkflowTests
{
	[Fact]
	public async Task SourceSuggestions_SendOneRowPerScopedGroupAndCannotBypassCatalogPreviewThroughItemApply()
	{
		var ids = await SeedAsync();
		await using (var db = fixture.CreateDbContext())
		{
			var milk = await db.ReceiptItems.SingleAsync(x => x.Id == ids[0]);
			db.ReceiptItems.Add(new ReceiptItemEntity
			{
				Id = Guid.NewGuid(),
				ReceiptId = milk.ReceiptId,
				Description = "Milk",
				Quantity = 2,
				UnitPrice = 8.1234m,
				UnitPriceCurrency = Currency.USD,
				TotalAmount = 16.25m,
				TotalAmountCurrency = Currency.USD,
				Category = "Food",
			});
			await db.SaveChangesAsync();
		}
		string? sent = null;
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((ChatGptInferenceRequest request, CancellationToken _) =>
			{
				sent = request.Prompt;
				using var json = JsonDocument.Parse(request.Prompt);
				return new ChatGptInferenceResult(JsonSerializer.Serialize(new
				{
					results = json.RootElement.EnumerateArray().Select(row => new
					{
						id = row.GetProperty("id").GetGuid(),
						identityDecision = "different",
						candidateId = (Guid?)null,
						newName = "Reviewed milk",
						reason = "Synthetic explicit identity",
					}).ToArray(),
				}), new(10, 5, 0, 0));
			});
		var source = new NormalizationSourceKey("Synthetic store", "Milk", null);
		var service = Service();
		var queued = await service.StartSourcesAsync(Owner, Guid.NewGuid(), [new(source, false)], "test-model", CancellationToken.None);
		queued.Proposals.Should().ContainSingle().Which.Source.Should().Be(source);
		await service.ProcessNextAsync(CancellationToken.None);
		sent.Should().NotBeNull();
		using var payload = JsonDocument.Parse(sent!);
		payload.RootElement.GetArrayLength().Should().Be(1);
		payload.RootElement[0].GetProperty("description").GetString().Should().Be("Milk");
		var ready = (await service.GetRunAsync(Owner, queued.Id, CancellationToken.None))!;
		ready.State.Should().Be("ready");
		Func<Task> bypass = () => service.ApplyAsync(Owner, ready.Id, ready.Proposals.Select(x => x.Id).ToArray(), ready.Revision, CancellationToken.None);
		(await bypass.Should().ThrowAsync<NormalizationAiException>()).Which.Code.Should().Be("catalog_review_required");
		await using var verify = fixture.CreateDbContext();
		(await verify.ReceiptItems.ToListAsync()).Should().HaveCount(3).And.OnlyContain(x => x.NormalizedDescriptionId == null);
		(await verify.NormalizedDescriptions.CountAsync()).Should().Be(0);
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task SourceSuggestions_InsufficientEvidenceStaysUnresolvedAndNeverCreatesCanonicalRows()
	{
		await SeedAsync();
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((ChatGptInferenceRequest request, CancellationToken _) =>
			{
				using var json = JsonDocument.Parse(request.Prompt);
				return new ChatGptInferenceResult(JsonSerializer.Serialize(new
				{
					results = json.RootElement.EnumerateArray().Select(row => new
					{
						id = row.GetProperty("id").GetGuid(),
						identityDecision = "insufficient",
						candidateId = (Guid?)null,
						newName = (string?)null,
						reason = "Source identity is ambiguous",
					}).ToArray(),
				}), new(10, 5, 0, 0));
			});
		var service = new Infrastructure.NormalizationAi.NormalizationAiService(new Factory(fixture), runner.Object, matching.Object, Microsoft.Extensions.Options.Options.Create(new Infrastructure.NormalizationAi.NormalizationAiOptions { Enabled = true, OwnerUserId = Owner, PendingRunLimit = 1 }), TimeProvider.System, publisher.Object, runnerOptions: bindingOptions);
		var queued = await service.StartSourcesAsync(Owner, Guid.NewGuid(),
			[new(new("Synthetic store", "Milk", null), false)], "test-model", CancellationToken.None);
		await service.ProcessNextAsync(CancellationToken.None);
		var ready = (await service.GetRunAsync(Owner, queued.Id, CancellationToken.None))!;
		ready.State.Should().Be("ready");
		ready.Proposals.Should().ContainSingle().Which.Action.Should().Be("abstain");
		ready.Proposals.Single().TargetId.Should().BeNull();
		ready.Proposals.Single().State.Should().Be("abstained");
		var next = await service.StartSourcesAsync(Owner, Guid.NewGuid(), [new(new("Synthetic store", "Milk", null), false)], "test-model", CancellationToken.None);
		next.State.Should().Be("queued", "all-abstain results must release the source reservation and the one-run admission limit");
		await using var verify = fixture.CreateDbContext();
		(await verify.NormalizedDescriptions.CountAsync()).Should().Be(0);
		(await verify.ReceiptItems.ToListAsync()).Should().OnlyContain(x => x.NormalizedDescriptionId == null);
	}
}
