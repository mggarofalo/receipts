using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReceiptRepeatedNew_IdenticalExplicitNamesCreateOneIdentityForDistinctRows(bool remember)
	{
		await ResetAsync();
		var first = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source),
			NewName: "Beef franks 8 count", RememberSource: remember));
		var second = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source),
			NewName: " BEEF FRANKS 8 COUNT ", RememberSource: remember));

		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [first, second], [], CancellationToken.None);

		await using var db = fixture.CreateDbContext();
		var identity = await db.NormalizedDescriptions.SingleAsync();
		var rows = await db.ReceiptItems.OrderBy(row => row.Id).ToListAsync();
		rows.Should().HaveCount(2);
		rows.Select(row => row.Id).Distinct().Should().HaveCount(2);
		rows.Should().OnlyContain(row => row.ReceiptId == saved.Receipt.Id && row.NormalizedDescriptionId == identity.Id
			&& row.NormalizationReviewState == NormalizationReviewState.Verified);
		rows.Should().OnlyContain(row => row.Description == Source.Description && row.Quantity == 2 && row.UnitPrice == 3.125m && row.TotalAmount == 6.25m);
		(await db.NormalizationSourceMappings.CountAsync()).Should().Be(remember ? 1 : 0);
		(await db.NormalizationReviewWork.AnyAsync()).Should().BeFalse();
		(await AuditCountAsync(rows.Select(row => row.Id).ToArray())).Should().Be(2);
	}

	[Fact]
	public async Task ReceiptRepeatedNew_DoesNotConvertPreexistingNameCollisionIntoImplicitApproval()
	{
		await ResetAsync();
		var existing = await CanonicalAsync("Beef franks 8 count");
		var first = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), NewName: "Beef franks 8 count"));
		var second = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), NewName: "Beef franks 8 count"));

		Func<Task> act = () => EntryWriter().CreateAsync(EntryReceipt(), [], [first, second], [], CancellationToken.None);
		(await act.Should().ThrowAsync<NormalizationCatalogException>()).Which.Code.Should().Be("normalization_name_exists");

		await using var db = fixture.CreateDbContext();
		(await db.Receipts.AnyAsync()).Should().BeFalse();
		(await db.ReceiptItems.AnyAsync()).Should().BeFalse();
		(await db.NormalizedDescriptions.Select(row => row.Id).ToListAsync()).Should().Equal(existing.Id);
		(await db.NormalizationSourceMappings.AnyAsync()).Should().BeFalse();
	}

	[Fact]
	public async Task ReceiptRepeatedNew_ConflictingRememberedProductsForSameSourceRejectWholeReceipt()
	{
		await ResetAsync();
		var first = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), NewName: "Beef franks", RememberSource: true));
		var second = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), NewName: "Turkey franks", RememberSource: true));

		Func<Task> act = () => EntryWriter().CreateAsync(EntryReceipt(), [], [first, second], [], CancellationToken.None);
		(await act.Should().ThrowAsync<NormalizationCatalogException>()).Which.Code.Should().Be("normalization_source_ambiguous");

		await using var db = fixture.CreateDbContext();
		(await db.Receipts.AnyAsync()).Should().BeFalse();
		(await db.NormalizedDescriptions.AnyAsync()).Should().BeFalse();
		(await db.NormalizationSourceMappings.AnyAsync()).Should().BeFalse();
		(await db.NormalizationReviewWork.AnyAsync()).Should().BeFalse();
	}
}
