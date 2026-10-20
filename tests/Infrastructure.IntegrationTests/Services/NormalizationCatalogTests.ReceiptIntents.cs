using Application.Interfaces.Services;
using Application.Normalization;
using Domain;
using Domain.Core;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task ReceiptIntent_CreateAndRememberCommitsIdentityPurchaseAndEvidenceTogether()
	{
		await ResetAsync();
		var row = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source),
			NewName: "Beef franks 8 count", RememberSource: true));
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [row], [], CancellationToken.None);

		await using var db = fixture.CreateDbContext();
		var identity = await db.NormalizedDescriptions.SingleAsync();
		identity.CanonicalName.Should().Be("Beef franks 8 count");
		identity.Status.Should().Be(NormalizedDescriptionStatus.Active);
		var item = await db.ReceiptItems.SingleAsync();
		item.NormalizedDescriptionId.Should().Be(identity.Id);
		item.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		item.NormalizationReviewDecisionId.Should().NotBeNull();
		item.ReviewedBy.Should().Be(Actor);
		item.ReviewedSourceHash.Should().Be(NormalizationCatalogPolicy.Hash(Source));
		item.Description.Should().Be(Source.Description);
		item.UnitPrice.Should().Be(3.125m);
		item.Quantity.Should().Be(2);
		item.TotalAmount.Should().Be(6.25m);
		item.ReceiptId.Should().Be(saved.Receipt.Id);
		(await db.NormalizationSourceMappings.SingleAsync()).TargetId.Should().Be(identity.Id);
		(await db.NormalizationReviewWork.AnyAsync()).Should().BeFalse();
		(await db.AuditLogs.CountAsync(x => x.EntityType == "ReceiptItem" && x.EntityId == item.Id.ToString())).Should().Be(1);
	}

	[Fact]
	public async Task ReceiptIntent_CommitFailureLeavesNoIdentityReceiptMappingWorkOrAuditOrphans()
	{
		await ResetAsync();
		await using var before = fixture.CreateDbContext();
		int audits = await before.AuditLogs.CountAsync();
		var options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(new FailCommit()).Options;
		var writer = EntryWriter(new EntryActorFactory(options, Actor));
		var row = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source),
			NewName: "Never committed product", RememberSource: true));

		Func<Task> act = () => writer.CreateAsync(EntryReceipt(), [], [row], [], CancellationToken.None);
		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected history commit failure");

		await using var db = fixture.CreateDbContext();
		(await db.Receipts.CountAsync()).Should().Be(0);
		(await db.ReceiptItems.CountAsync()).Should().Be(0);
		(await db.NormalizedDescriptions.CountAsync()).Should().Be(0);
		(await db.NormalizationSourceMappings.CountAsync()).Should().Be(0);
		(await db.NormalizationReviewWork.CountAsync()).Should().Be(0);
		(await db.AuditLogs.CountAsync()).Should().Be(audits);
	}

	[Fact]
	public async Task ReceiptIntent_StaleChoiceRollsBackOtherValidRowsInsteadOfPartiallySaving()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Existing product");
		var create = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), NewName: "Would be orphaned"));
		var stale = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, Guid.NewGuid()));

		Func<Task> act = () => EntryWriter().CreateAsync(EntryReceipt(), [], [create, stale], [], CancellationToken.None);
		(await act.Should().ThrowAsync<NormalizationCatalogException>()).Which.Code.Should().Be("normalization_target_changed");

		await using var db = fixture.CreateDbContext();
		(await db.Receipts.CountAsync()).Should().Be(0);
		(await db.ReceiptItems.CountAsync()).Should().Be(0);
		(await db.NormalizedDescriptions.Select(x => x.Id).ToListAsync()).Should().Equal(target.Id);
		(await db.NormalizationReviewWork.CountAsync()).Should().Be(0);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReceiptIntent_ExplicitReviewRequiresEnabledActorButOrdinaryUnresolvedCaptureDoesNot(bool disabledActor)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Existing product");
		const string disabledActorId = "disabled-receipt-reviewer";
		if (disabledActor)
		{
			await using var disable = fixture.CreateDbContext();
			var actor = await disable.Users.SingleOrDefaultAsync(x => x.Id == disabledActorId);
			if (actor is null)
			{
				actor = new Infrastructure.Entities.ApplicationUser { Id = disabledActorId, UserName = disabledActorId, NormalizedUserName = disabledActorId.ToUpperInvariant(), CreatedAt = DateTimeOffset.UtcNow };
				disable.Users.Add(actor);
			}
			actor.LockoutEnabled = true;
			actor.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
			await disable.SaveChangesAsync();
		}
		var row = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision));
		Func<Task> unauthorized = () => (disabledActor ? EntryWriter(new EntryActorFactory(fixture.CreateOptions(), disabledActorId)) : LegacyReceiptWriter()).CreateAsync(EntryReceipt(), [], [row], [], CancellationToken.None);
		(await unauthorized.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(403);

		var saved = await LegacyReceiptWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null)], [], CancellationToken.None);
		await using var db = fixture.CreateDbContext();
		var item = await db.ReceiptItems.SingleAsync();
		item.Id.Should().Be(saved.Items.Single().Id);
		item.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		item.NormalizedDescriptionId.Should().BeNull();
		(await db.NormalizationReviewWork.SingleAsync()).ItemId.Should().Be(item.Id);
	}

	[Fact]
	public async Task ReceiptIntent_FinancialEditsPreserveIndependentReviewAndSourceEditInvalidatesIt()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Existing product");
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting,
			Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision))], [], CancellationToken.None);
		var original = (await ReadAsync([saved.Items.Single().Id])).Single();
		var service = new ReceiptItemService(new ReceiptItemRepository(new EntryActorFactory(fixture.CreateOptions(), Actor)), new ReceiptItemMapper());
		var financialEdit = new ReceiptItem(original.Id, original.ReceiptItemCode, original.Description, 3, new Money(4), new Money(12), "Food", null);
		await service.UpdateAsync([financialEdit], saved.Receipt.Id, CancellationToken.None);
		var edited = (await ReadAsync([original.Id])).Single();
		edited.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		edited.NormalizationReviewDecisionId.Should().Be(original.NormalizationReviewDecisionId);
		edited.ReviewedAt.Should().Be(original.ReviewedAt);
		edited.TotalAmount.Should().Be(12);

		financialEdit.ReceiptItemCode = "different-code";
		await service.UpdateAsync([financialEdit], saved.Receipt.Id, CancellationToken.None);
		var changed = (await ReadAsync([original.Id])).Single();
		changed.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		changed.NormalizationReviewDecisionId.Should().BeNull();
		changed.TotalAmount.Should().Be(12);
		await using var db = fixture.CreateDbContext();
		var work = await db.NormalizationReviewWork.SingleAsync();
		work.ItemId.Should().Be(original.Id);
		work.SourceHash.Should().Be(NormalizationCatalogPolicy.Hash(Source with { ItemCode = "different-code" }));
	}

	private CompleteReceiptWriter EntryWriter(IDbContextFactory<ApplicationDbContext>? factory = null) => new(
		factory ?? new EntryActorFactory(fixture.CreateOptions(), Actor), new ReceiptMapper(), new TransactionMapper(), new ReceiptItemMapper(), new AdjustmentMapper());
	private static Receipt EntryReceipt() => new(Guid.NewGuid(), Source.Location, new(2026, 10, 20), new Money(0));
	private static ReceiptItem EntryItem(ReceiptNormalizationIntent? intent) => new(Guid.NewGuid(), Source.ItemCode, Source.Description,
		2, new Money(3.125m), new Money(6.25m), "Food", null)
	{ NormalizationIntent = intent };
	private sealed class EntryActorFactory(DbContextOptions<ApplicationDbContext> options, string? actor) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext()
		{
			var accessor = new Mock<ICurrentUserAccessor>();
			accessor.SetupGet(x => x.UserId).Returns(actor);
			return new(options, accessor.Object);
		}
	}
}
