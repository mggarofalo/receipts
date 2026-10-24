using Application.Normalization;
using Domain;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task ReceiptReplay_LostResponseWithFreshServerIdsReturnsOriginalWithoutDuplicatingAnySideEffect()
	{
		await ResetAsync();
		var requestId = Guid.NewGuid();
		var intent = new ReceiptNormalizationIntent(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source),
			NewName: "Reviewed beef franks", RememberSource: true);
		var first = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(intent)], [], requestId, CancellationToken.None);
		await using var before = fixture.CreateDbContext();
		int audits = await before.AuditLogs.CountAsync();
		var decision = (await before.ReceiptItems.SingleAsync()).NormalizationReviewDecisionId;

		// Re-enter the same API-shaped facts: server-generated domain IDs are intentionally different.
		var retry = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(intent)], [], requestId, CancellationToken.None);

		retry.Receipt.Id.Should().Be(first.Receipt.Id);
		retry.Items.Single().Id.Should().Be(first.Items.Single().Id);
		retry.Items.Single().NormalizedDescriptionId.Should().Be(first.Items.Single().NormalizedDescriptionId);
		await using var db = fixture.CreateDbContext();
		(await db.Receipts.CountAsync()).Should().Be(1);
		(await db.ReceiptItems.CountAsync()).Should().Be(1);
		(await db.NormalizedDescriptions.CountAsync()).Should().Be(1);
		(await db.NormalizationSourceMappings.CountAsync()).Should().Be(1);
		(await db.NormalizationReviewWork.CountAsync()).Should().Be(0);
		(await db.AuditLogs.CountAsync()).Should().Be(audits);
		(await db.ReceiptItems.SingleAsync()).NormalizationReviewDecisionId.Should().Be(decision);
	}

	[Fact]
	public async Task ReceiptReplay_ChangedFinancialPayloadCannotReuseAnAcceptedRequestKey()
	{
		await ResetAsync();
		var requestId = Guid.NewGuid();
		var first = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null)], [], requestId, CancellationToken.None);
		var changed = EntryItem(null);
		changed.UnitPrice = new Money(4);
		changed.TotalAmount = new Money(8);

		Func<Task> act = () => EntryWriter().CreateAsync(EntryReceipt(), [], [changed], [], requestId, CancellationToken.None);
		(await act.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);

		await using var db = fixture.CreateDbContext();
		(await db.Receipts.SingleAsync()).Id.Should().Be(first.Receipt.Id);
		(await db.ReceiptItems.SingleAsync()).TotalAmount.Should().Be(6.25m);
		(await db.NormalizationReviewWork.CountAsync()).Should().Be(1);
	}

	[Fact]
	public async Task ReceiptReplay_UnknownSaveAndRetryRegistersExactlyOneOutstandingObservation()
	{
		await ResetAsync();
		var requestId = Guid.NewGuid();
		var first = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null)], [], requestId, CancellationToken.None);
		await using var before = fixture.CreateDbContext();
		var initial = await before.NormalizationReviewWork.AsNoTracking().SingleAsync();
		int audits = await before.AuditLogs.CountAsync();

		var retry = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null)], [], requestId, CancellationToken.None);

		retry.Receipt.Id.Should().Be(first.Receipt.Id);
		await using var db = fixture.CreateDbContext();
		var work = await db.NormalizationReviewWork.SingleAsync();
		work.ItemId.Should().Be(initial.ItemId);
		work.SourceRevision.Should().Be(initial.SourceRevision);
		work.CreatedAt.Should().Be(initial.CreatedAt);
		(await db.NormalizedDescriptions.CountAsync()).Should().Be(0);
		(await db.AuditLogs.CountAsync()).Should().Be(audits);
	}
}
