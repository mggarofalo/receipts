using Application.NormalizationAi;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationAiWorkflowTests
{
	[Theory]
	[InlineData(NormalizedDescriptionStatus.PendingReview, "pending_identity_requires_review", "pendingReview")]
	[InlineData(NormalizedDescriptionStatus.Rejected, "label_target_inactive", "rejected")]
	public async Task LabelOnly_InactiveIdentityReturnsExactRecoveryTargetWithoutRunOrStatusMutation(
		NormalizedDescriptionStatus status, string code, string wireStatus)
	{
		var ids = await SeedAsync();
		var target = await SeedAssignedLabelTargetAsync(ids[0]);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.NormalizedDescriptions.SingleAsync(x => x.Id == target.Id)).Status = status;
			await db.SaveChangesAsync();
		}
		int audits = await AuditCountAsync(ids);
		Func<Task> start = () => Service().StartSourcesAsync(Owner, Guid.NewGuid(),
			[new(new("Synthetic store", "Milk", null), false, true)], "test-model", CancellationToken.None);
		var error = (await start.Should().ThrowAsync<NormalizationAiException>()).Which;
		error.StatusCode.Should().Be(409);
		error.Code.Should().Be(code);
		error.Extensions.Should().Contain("normalizedDescriptionId", target.Id)
			.And.Contain("normalizedDescriptionStatus", wireStatus);
		await using var check = fixture.CreateDbContext();
		(await check.NormalizationAiRuns.CountAsync()).Should().Be(0);
		(await check.NormalizationAiProposals.CountAsync()).Should().Be(0);
		(await check.NormalizedDescriptions.SingleAsync(x => x.Id == target.Id)).Status.Should().Be(status);
		var item = await check.ReceiptItems.SingleAsync(x => x.Id == ids[0]);
		item.NormalizedDescriptionId.Should().Be(target.Id);
		item.NormalizationOrigin.Should().Be("manual");
		item.Quantity.Should().Be(1.2345m);
		item.UnitPrice.Should().Be(2.3456m);
		item.TotalAmount.Should().Be(2.90m);
		(await AuditCountAsync(ids)).Should().Be(audits);
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}
