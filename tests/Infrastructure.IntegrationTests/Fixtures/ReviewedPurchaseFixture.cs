using Application.Interfaces.Services;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Infrastructure.IntegrationTests.Fixtures;

internal static class ReviewedPurchaseFixture
{
	// Confirm the selected live purchases through the same guarded explicit choice as receipt entry.
	// Pending identities and unlinked purchases deliberately remain unresolved.
	public static async Task ConfirmActiveAsync(DbContextOptions<ApplicationDbContext> options,
		params Guid[] itemIds)
	{
		const string actor = "report-fixture-reviewer";
		var accessor = new Mock<ICurrentUserAccessor>();
		accessor.SetupGet(x => x.UserId).Returns(actor);
		await using ApplicationDbContext db = new(options, accessor.Object);
		if (!await db.Users.AnyAsync(x => x.Id == actor))
		{
			db.Users.Add(new ApplicationUser
			{
				Id = actor,
				UserName = actor,
				NormalizedUserName = actor.ToUpperInvariant(),
				SecurityStamp = Guid.NewGuid().ToString(),
				CreatedAt = DateTimeOffset.UtcNow
			});
			await db.SaveChangesAsync();
		}
		var items = await db.ReceiptItems.IgnoreAutoIncludes()
			.Where(x => itemIds.Contains(x.Id) && x.NormalizedDescriptionId != null).ToListAsync();
		foreach (var item in items)
		{
			var target = await db.NormalizedDescriptions.SingleAsync(x => x.Id == item.NormalizedDescriptionId);
			if (target.Status != NormalizedDescriptionStatus.Active) { continue; }
			var parent = await db.Receipts.SingleAsync(x => x.Id == item.ReceiptId);
			item.NormalizationIntent = new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(),
				NormalizationCatalogPolicy.Hash(new(parent.Location, item.Description, item.ReceiptItemCode)),
				target.Id, target.NormalizationRevision, ExpectedItemRevision: item.NormalizationRevision);
		}
		await db.SaveChangesAsync();
	}
}
