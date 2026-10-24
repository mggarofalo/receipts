using Application.Normalization;
using Domain;
using Domain.Core;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task ReceiptEditReplay_LostReviewResponseReplaysOriginalStaleRevisionWithoutDuplicatingDecision()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Reviewed purchase identity");
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null)], [], CancellationToken.None);
		var original = (await ReadAsync([saved.Items.Single().Id])).Single();
		var requestId = Guid.NewGuid();
		var intent = new ReceiptNormalizationIntent(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision, ExpectedItemRevision: original.NormalizationRevision);
		ReceiptItem Edit(decimal total = 6.25m) => new(original.Id, original.ReceiptItemCode, original.Description, 2, new Money(total / 2), new Money(total), "Food", null) { NormalizationIntent = intent };
		var service = new ReceiptItemService(new ReceiptItemRepository(new EntryActorFactory(fixture.CreateOptions(), Actor)), new ReceiptItemMapper());
		await service.UpdateAsync([Edit()], saved.Receipt.Id, requestId, CancellationToken.None);
		var reviewed = (await ReadAsync([original.Id])).Single();
		var auditCount = await AuditCountAsync([original.Id]);
		reviewed.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		reviewed.NormalizationRevision.Should().NotBe(original.NormalizationRevision);
		var all = await service.GetAllAsync(0, 10, Application.Models.SortParams.Default, CancellationToken.None);
		var receiptRows = await service.GetByReceiptIdAsync(saved.Receipt.Id, 0, 10, Application.Models.SortParams.Default, CancellationToken.None);
		foreach (var projection in new[] { all.Data.Single(), receiptRows.Data.Single(), (await service.GetByIdAsync(original.Id, CancellationToken.None))! })
		{
			projection.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
			projection.NormalizationRevision.Should().Be(reviewed.NormalizationRevision);
			projection.NormalizationReviewDecisionId.Should().Be(reviewed.NormalizationReviewDecisionId);
		}

		await service.UpdateAsync([Edit()], saved.Receipt.Id, requestId, CancellationToken.None);

		var replayed = (await ReadAsync([original.Id])).Single();
		replayed.NormalizationReviewDecisionId.Should().Be(reviewed.NormalizationReviewDecisionId);
		replayed.NormalizationRevision.Should().Be(reviewed.NormalizationRevision);
		(await AuditCountAsync([original.Id])).Should().Be(auditCount);
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationReviewWork.CountAsync()).Should().Be(0);
		Func<Task> changed = () => service.UpdateAsync([Edit(10)], saved.Receipt.Id, requestId, CancellationToken.None);
		(await changed.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);
		(await ReadAsync([original.Id])).Single().TotalAmount.Should().Be(6.25m);
		(await AuditCountAsync([original.Id])).Should().Be(auditCount);
	}
}
