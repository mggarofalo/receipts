using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ScopeEdit_InvalidatesMappedAssignmentInTrackedStateAndAuditWithoutChangingMoney(bool parentLocation)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks");
		var ids = await ItemsAsync(Source, 1);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		int before = await AuditCountAsync(ids);
		var initial = (await ReadAsync(ids)).Single();
		await using (var edit = fixture.CreateDbContext())
		{
			if (parentLocation)
			{
				// Loading only the parent reflects receipt editing; child invalidation must
				// still participate in the same EF audit unit rather than only a DB trigger.
				var receipt = await edit.Receipts.SingleAsync(x => x.Id == initial.ReceiptId);
				receipt.Location = "Store B";
				await edit.SaveChangesAsync();
				var trackedChild = edit.ChangeTracker.Entries<Infrastructure.Entities.Core.ReceiptItemEntity>().Single().Entity;
				trackedChild.NormalizedDescriptionId.Should().BeNull();
				trackedChild.NormalizationMappingId.Should().BeNull();
			}
			else
			{
				var item = await edit.ReceiptItems.SingleAsync(x => x.Id == ids[0]);
				item.ReceiptItemCode = "DIFFERENT";
				await edit.SaveChangesAsync();
				item.NormalizedDescriptionId.Should().BeNull();
				item.NormalizationMappingId.Should().BeNull();
			}
		}
		var stored = (await ReadAsync(ids)).Single();
		stored.NormalizationOrigin.Should().Be("unassigned");
		stored.NormalizedDescriptionId.Should().BeNull();
		stored.NormalizationMappingId.Should().BeNull();
		stored.Quantity.Should().Be(initial.Quantity);
		stored.UnitPrice.Should().Be(initial.UnitPrice);
		stored.TotalAmount.Should().Be(initial.TotalAmount);
		(await AuditCountAsync(ids)).Should().Be(before + 1, "assignment invalidation is a committed business change with one item audit");
	}
}
