using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData("description")]
	[InlineData("code")]
	[InlineData("target")]
	[InlineData("parent-aba")]
	[InlineData("target-rejected")]
	public async Task ReceiptSqlChanges_InvalidateProofAndDurablyObserveWork(string change)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Reviewed item");
		var other = await CanonicalAsync("Other item");
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting,
			Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision))], [], CancellationToken.None);
		var id = saved.Items.Single().Id;
		var original = (await ReadAsync([id])).Single();
		original.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		await using (var writer = fixture.CreateDbContext())
		{
			switch (change)
			{
				case "description": await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"Description\"='Changed observation' WHERE \"Id\"={id}"); break;
				case "code": await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"ReceiptItemCode\"='Other code' WHERE \"Id\"={id}"); break;
				case "target": await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"NormalizedDescriptionId\"={other.Id} WHERE \"Id\"={id}"); break;
				case "target-rejected": await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE matching.\"NormalizedDescriptions\" SET \"Status\"='Rejected' WHERE \"Id\"={target.Id}"); break;
				case "parent-aba":
					await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"Receipts\" SET \"Location\"='Different store' WHERE \"Id\"={saved.Receipt.Id}");
					await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"Receipts\" SET \"Location\"={Source.Location} WHERE \"Id\"={saved.Receipt.Id}");
					break;
			}
		}
		var changed = (await ReadAsync([id])).Single();
		changed.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		changed.NormalizationReviewDecisionId.Should().BeNull();
		changed.ReviewedTargetId.Should().BeNull();
		changed.ReviewedSourceHash.Should().BeNull();
		changed.NormalizedDescriptionId.Should().Be(change == "target" ? other.Id : target.Id, "invalidating trust must not silently rewrite historical identity");
		changed.Quantity.Should().Be(original.Quantity);
		changed.UnitPrice.Should().Be(original.UnitPrice);
		changed.TotalAmount.Should().Be(original.TotalAmount);
		await using var verify = fixture.CreateDbContext();
		var work = await verify.NormalizationReviewWork.SingleAsync();
		work.ItemId.Should().Be(id);
		work.SourceRevision.Should().Be(changed.NormalizationRevision);
		work.SourceHash.Should().BeNull("SQL invalidation records an observation, not a fabricated application hash");
	}

	[Fact]
	public async Task ReceiptSqlMappingRevocation_DowngradesDependentProofButPreservesIndependentReview()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Scoped identity");
		var explicitReceipt = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting,
			Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		var mappedReceipt = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null)], [], CancellationToken.None);
		var independent = explicitReceipt.Items.Single().Id;
		var dependent = mappedReceipt.Items.Single().Id;
		await using (var writer = fixture.CreateDbContext())
		{
			await writer.Database.ExecuteSqlRawAsync("UPDATE matching.\"NormalizationSourceMappings\" SET \"State\"='revoked', \"Revision\"=gen_random_uuid()");
		}
		var rows = await ReadAsync([independent, dependent]);
		rows.Single(x => x.Id == independent).NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		rows.Single(x => x.Id == dependent).NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		rows.Should().OnlyContain(x => x.NormalizedDescriptionId == target.Id);
		await using var verify = fixture.CreateDbContext();
		(await verify.NormalizationReviewWork.SingleAsync()).ItemId.Should().Be(dependent);
	}

	[Fact]
	public async Task ReceiptSqlFinancialDateAndDisplayLabelChanges_PreserveIndependentIdentityEvidence()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Identity");
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting,
			Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision))], [], CancellationToken.None);
		var id = saved.Items.Single().Id;
		var original = (await ReadAsync([id])).Single();
		await using (var writer = fixture.CreateDbContext())
		{
			await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"Quantity\"=3, \"UnitPrice\"=4, \"TotalAmount\"=12 WHERE \"Id\"={id}");
			await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"Receipts\" SET \"Date\"=DATE '2026-01-01' WHERE \"Id\"={saved.Receipt.Id}");
			await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE matching.\"NormalizedDescriptions\" SET \"DisplayLabel\"='Readable identity' WHERE \"Id\"={target.Id}");
		}
		var current = (await ReadAsync([id])).Single();
		current.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		current.NormalizationReviewDecisionId.Should().Be(original.NormalizationReviewDecisionId);
		current.ReviewedSourceHash.Should().Be(original.ReviewedSourceHash);
		current.ReviewedTargetId.Should().Be(target.Id);
		current.TotalAmount.Should().Be(12);
		await using var verify = fixture.CreateDbContext();
		(await verify.NormalizationReviewWork.AnyAsync()).Should().BeFalse();
	}
}
