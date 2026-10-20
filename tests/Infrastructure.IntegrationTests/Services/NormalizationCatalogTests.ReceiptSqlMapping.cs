using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData("delete")]
	[InlineData("location")]
	[InlineData("description")]
	[InlineData("code")]
	[InlineData("exclude")]
	public async Task ReceiptSqlMappingMutationWithoutRevision_InvalidatesOnlyDependentEvidence(string change)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Approved scoped identity");
		var independentReceipt = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(
			ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source),
			target.Id, target.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		var mappedReceipt = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null)], [], CancellationToken.None);
		Guid independentId = independentReceipt.Items.Single().Id;
		Guid dependentId = mappedReceipt.Items.Single().Id;
		Guid[] ids = [independentId, dependentId];
		var before = await ReadAsync(ids);
		before.Should().OnlyContain(item => item.NormalizationReviewState == NormalizationReviewState.Verified);
		var independentBefore = before.Single(item => item.Id == independentId);
		var facts = await FactsAsync(ids);

		await using (var writer = fixture.CreateDbContext())
		{
			var mapping = await writer.NormalizationSourceMappings.AsNoTracking().SingleAsync();
			before.Single(item => item.Id == dependentId).ReviewMappingRevision.Should().Be(mapping.Revision);
			independentBefore.NormalizationMappingId.Should().BeNull();
			// No statement updates Revision. The database must compare the actual authority facts.
			switch (change)
			{
				case "delete":
					await writer.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM matching."NormalizationSourceMappings" WHERE "Id"={mapping.Id}"""); break;
				case "location":
					await writer.Database.ExecuteSqlInterpolatedAsync($"""UPDATE matching."NormalizationSourceMappings" SET "Location"='Other store' WHERE "Id"={mapping.Id}"""); break;
				case "description":
					await writer.Database.ExecuteSqlInterpolatedAsync($"""UPDATE matching."NormalizationSourceMappings" SET "Description"='Other observation' WHERE "Id"={mapping.Id}"""); break;
				case "code":
					await writer.Database.ExecuteSqlInterpolatedAsync($"""UPDATE matching."NormalizationSourceMappings" SET "ItemCode"='Other code' WHERE "Id"={mapping.Id}"""); break;
				case "exclude":
					await writer.Database.ExecuteSqlInterpolatedAsync($"""UPDATE matching."NormalizationSourceMappings" SET "Exclude"=TRUE WHERE "Id"={mapping.Id}"""); break;
			}
			if (change == "delete") { (await writer.NormalizationSourceMappings.AnyAsync()).Should().BeFalse(); }
			else { (await writer.NormalizationSourceMappings.AsNoTracking().SingleAsync()).Revision.Should().Be(mapping.Revision); }
		}

		var after = await ReadAsync(ids);
		var independent = after.Single(item => item.Id == independentId);
		independent.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		independent.NormalizationReviewDecisionId.Should().Be(independentBefore.NormalizationReviewDecisionId);
		independent.ReviewedSourceHash.Should().Be(independentBefore.ReviewedSourceHash);
		independent.ReviewedTargetId.Should().Be(target.Id);
		var dependent = after.Single(item => item.Id == dependentId);
		dependent.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		dependent.NormalizationReviewDecisionId.Should().BeNull();
		dependent.ReviewedSourceHash.Should().BeNull();
		dependent.ReviewedTargetId.Should().BeNull();
		after.Should().OnlyContain(item => item.NormalizedDescriptionId == target.Id,
			"revoking reusable authority must preserve the historical identity association");
		(await FactsAsync(ids)).Should().BeEquivalentTo(facts);
		await using var verify = fixture.CreateDbContext();
		var work = await verify.NormalizationReviewWork.SingleAsync();
		work.ItemId.Should().Be(dependentId);
		work.SourceRevision.Should().Be(dependent.NormalizationRevision);
		work.SourceHash.Should().BeNull("SQL invalidation must not manufacture an application source hash");
	}

	[Fact]
	public async Task ReceiptSqlAfterReviewedSaveInSameTransaction_CannotReuseTheSaveAuthorization()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Purchase identity");
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null)], [], CancellationToken.None);
		Guid id = saved.Items.Single().Id;
		var original = (await ReadAsync([id])).Single();
		original.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		await using (var writer = new EntryActorFactory(fixture.CreateOptions(), Actor).CreateDbContext())
		{
			await using var transaction = await writer.Database.BeginTransactionAsync();
			var item = await writer.ReceiptItems.SingleAsync(row => row.Id == id);
			item.NormalizationIntent = new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(),
				NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision,
				ExpectedItemRevision: item.NormalizationRevision);
			await writer.SaveChangesAsync();
			var reviewed = await writer.ReceiptItems.AsNoTracking().SingleAsync(row => row.Id == id);
			reviewed.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
			reviewed.NormalizationReviewDecisionId.Should().NotBeNull();
			(await writer.NormalizationReviewWork.AnyAsync()).Should().BeFalse();
			// The same open connection and outer transaction must not retain the EF save bypass.
			await writer.Database.ExecuteSqlInterpolatedAsync($"""UPDATE receipts."ReceiptItems" SET "Description"='Changed after review' WHERE "Id"={id}""");
			await transaction.CommitAsync();
		}

		var changed = (await ReadAsync([id])).Single();
		changed.Description.Should().Be("Changed after review");
		changed.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		changed.NormalizationReviewDecisionId.Should().BeNull();
		changed.ReviewedSourceHash.Should().BeNull();
		changed.ReviewedTargetId.Should().BeNull();
		changed.NormalizedDescriptionId.Should().Be(target.Id);
		changed.Quantity.Should().Be(original.Quantity);
		changed.UnitPrice.Should().Be(original.UnitPrice);
		changed.TotalAmount.Should().Be(original.TotalAmount);
		await using var verify = fixture.CreateDbContext();
		var work = await verify.NormalizationReviewWork.SingleAsync();
		work.ItemId.Should().Be(id);
		work.SourceRevision.Should().Be(changed.NormalizationRevision);
		work.SourceHash.Should().BeNull();
	}
}
