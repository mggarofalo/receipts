using System.Text.Json;
using Application.Normalization;
using Application.NormalizationAi;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.NormalizationAi;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationAiWorkflowTests
{
	[Fact]
	public async Task SavedOldCatalogProtocol_FailsBeforeDispatchInsteadOfReinterpretingPendingEvidence()
	{
		var ids = await SeedAsync();
		var ai = Service();
		var queued = await ai.StartSourcesAsync(Owner, Guid.NewGuid(),
			[new(new("Synthetic store", "Milk", null), false)], "test-model", CancellationToken.None);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.NormalizationAiRuns.SingleAsync(x => x.Id == queued.Id)).PromptVersion = "receipts-catalog-v2-retrieval-v1";
			await db.SaveChangesAsync();
		}
		(await ai.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();
		var failed = (await ai.GetRunAsync(Owner, queued.Id, CancellationToken.None))!;
		failed.State.Should().Be("failed");
		failed.ErrorCode.Should().Be("unsupported_protocol");
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
		await AssertUnchangedAsync(ids);
	}

	[Fact]
	public async Task LabelOnly_RejectsSourcesAssignedToMultipleProductsBeforeDispatch()
	{
		var ids = await SeedAsync();
		await SeedAssignedLabelTargetAsync(ids[0]);
		await using (var db = fixture.CreateDbContext())
		{
			var bread = await db.ReceiptItems.SingleAsync(x => x.Id == ids[1]);
			bread.Description = "Milk";
			await db.SaveChangesAsync();
		}
		await SeedAssignedLabelTargetAsync(ids[1], "Different milk identity");
		Func<Task> start = () => Service().StartSourcesAsync(Owner, Guid.NewGuid(),
			[new(new("Synthetic store", "Milk", null), false, true)], "test-model", CancellationToken.None);
		(await start.Should().ThrowAsync<NormalizationAiException>()).Which.Code.Should().Be("label_source_ambiguous");
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task LabelOnly_ConnectionChangeRejectsOldApprovalWithoutRenamingProduct()
	{
		var ids = await SeedAsync();
		var target = await SeedAssignedLabelTargetAsync(ids[0]);
		SetupLabelResponse(target.Id, "Whole milk");
		var ai = Service();
		var queued = await ai.StartSourcesAsync(Owner, Guid.NewGuid(),
			[new(new("Synthetic store", "Milk", null), false, true)], "test-model", CancellationToken.None);
		await ai.ProcessNextAsync(CancellationToken.None);
		var ready = (await ai.GetRunAsync(Owner, queued.Id, CancellationToken.None))!;
		var catalog = new NormalizationCatalogService(new Factory(fixture), TimeProvider.System, publisher.Object);
		var review = await catalog.PreviewLabelAsync(Owner, target.Id, "Whole milk", CancellationToken.None);
		var epoch = await ai.BeginConnectionChangeAsync(CancellationToken.None);
		await ai.CompleteConnectionChangeAsync(epoch, CancellationToken.None);
		Func<Task> approve = () => catalog.ApproveLabelAsync(Owner, target.Id,
			new(review.ProposedDisplayLabel, review.TargetRevision, review.AffectedItemCount,
				ready.Id, ready.Proposals.Single().Id, ready.Revision), CancellationToken.None);
		(await approve.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);
		await using var db = fixture.CreateDbContext();
		(await db.NormalizedDescriptions.SingleAsync(x => x.Id == target.Id)).DisplayLabel.Should().BeNull();
		(await db.ReceiptItems.SingleAsync(x => x.Id == ids[0])).NormalizedDescriptionId.Should().Be(target.Id);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LabelOnly_AlreadyAssignedSourceRequiresSeparateApprovalAndConsumesOnlyItsSuggestion(bool humanEdit)
	{
		var ids = await SeedAsync();
		var target = await SeedAssignedLabelTargetAsync(ids[0]);
		SetupLabelResponse(target.Id, "Whole milk");
		var ai = Service();
		var queued = await ai.StartSourcesAsync(Owner, Guid.NewGuid(),
			[new(new("Synthetic store", "Milk", null), false, true)], "test-model", CancellationToken.None);
		await ai.ProcessNextAsync(CancellationToken.None);
		var ready = (await ai.GetRunAsync(Owner, queued.Id, CancellationToken.None))!;
		var proposal = ready.Proposals.Should().ContainSingle().Subject;
		proposal.LabelOnly.Should().BeTrue();
		proposal.SuggestedDisplayLabel.Should().Be("Whole milk");
		proposal.TargetId.Should().Be(target.Id);
		int audits = await AuditCountAsync(ids);
		var catalog = new NormalizationCatalogService(new Factory(fixture), TimeProvider.System, publisher.Object);
		Func<Task> mappingBypass = () => catalog.PreviewAsync(Owner,
			new(Guid.NewGuid(), proposal.Source!, target.Id, proposal.TargetRevision, null, true,
				false, ready.Id, proposal.Id, ready.Revision), CancellationToken.None);
		(await mappingBypass.Should().ThrowAsync<NormalizationCatalogException>()).Which.Code.Should().Be("label_review_required");
		var review = await catalog.PreviewLabelAsync(Owner, target.Id,
			humanEdit ? "Whole dairy milk" : proposal.SuggestedDisplayLabel, CancellationToken.None);
		await catalog.ApproveLabelAsync(Owner, target.Id,
			new(review.ProposedDisplayLabel, review.TargetRevision, review.AffectedItemCount, ready.Id, proposal.Id, ready.Revision), CancellationToken.None);
		var finished = (await ai.GetRunAsync(Owner, queued.Id, CancellationToken.None))!;
		finished.State.Should().Be("applied");
		finished.Proposals.Single().State.Should().Be(humanEdit ? "overridden" : "applied");
		finished.Proposals.Single().SuggestedDisplayLabel.Should().Be("Whole milk", "human editing must retain the original model evidence");
		await using var db = fixture.CreateDbContext();
		var item = await db.ReceiptItems.SingleAsync(x => x.Id == ids[0]);
		item.NormalizedDescriptionId.Should().Be(target.Id);
		item.NormalizationOrigin.Should().Be("manual");
		item.Description.Should().Be("Milk");
		item.Quantity.Should().Be(1.2345m);
		item.UnitPrice.Should().Be(2.3456m);
		item.TotalAmount.Should().Be(2.90m);
		(await AuditCountAsync(ids)).Should().Be(audits);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("MILK")]
	public async Task LabelOnly_NoSuggestedChangeDoesNotLeakPendingCapacity(string? label)
	{
		var ids = await SeedAsync();
		var target = await SeedAssignedLabelTargetAsync(ids[0]);
		SetupLabelResponse(target.Id, label);
		var ai = new NormalizationAiService(new Factory(fixture), runner.Object,
			Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = Owner, PendingRunLimit = 1 }),
			TimeProvider.System, publisher.Object, runnerOptions: bindingOptions);
		var selection = new NormalizationAiSourceSelection(new("Synthetic store", "Milk", null), false, true);
		var run = await ai.StartSourcesAsync(Owner, Guid.NewGuid(), [selection], "test-model", CancellationToken.None);
		await ai.ProcessNextAsync(CancellationToken.None);
		var ready = (await ai.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		ready.Proposals.Single().State.Should().Be("no_change");
		(await ai.StartSourcesAsync(Owner, Guid.NewGuid(), [selection], "test-model", CancellationToken.None))
			.State.Should().Be("queued");
		await using var db = fixture.CreateDbContext();
		(await db.NormalizedDescriptions.SingleAsync(x => x.Id == target.Id)).DisplayLabel.Should().BeNull();
	}

	private async Task<NormalizedDescriptionEntity> SeedAssignedLabelTargetAsync(Guid itemId, string name = "MILK")
	{
		await using var db = fixture.CreateDbContext();
		var target = new NormalizedDescriptionEntity
		{
			Id = Guid.NewGuid(),
			CanonicalName = name,
			Status = NormalizedDescriptionStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow
		};
		db.NormalizedDescriptions.Add(target);
		var item = await db.ReceiptItems.SingleAsync(x => x.Id == itemId);
		item.NormalizedDescriptionId = target.Id;
		item.NormalizationOrigin = "manual";
		item.NormalizationAssignmentId = Guid.NewGuid();
		await db.SaveChangesAsync();
		return target;
	}

	private void SetupLabelResponse(Guid targetId, string? label)
	{
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((ChatGptInferenceRequest request, CancellationToken _) =>
			{
				using var input = JsonDocument.Parse(request.Prompt);
				var row = input.RootElement.EnumerateArray().Single();
				row.GetProperty("labelOnly").GetBoolean().Should().BeTrue();
				row.GetProperty("candidates").EnumerateArray().Should().ContainSingle()
					.Which.GetProperty("id").GetGuid().Should().Be(targetId);
				return new ChatGptInferenceResult(JsonSerializer.Serialize(new
				{
					results = new[] {
					new { id = row.GetProperty("id").GetGuid(), identityDecision = "same", candidateId = targetId,
						newName = (string?)null, suggestedDisplayLabel = label, reason = "Synthetic naming suggestion" } }
				}));
			});
	}
}
