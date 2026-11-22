using Domain.Envelopes;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Envelopes;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task Assign_FillsUnresolvedEnvelopeOnceThenKeepsRecordedOnes()
	{
		try
		{
			// Arrange
			var seed = await SeedAsync();
			Guid rootEnvelope = Guid.NewGuid(), childEnvelope = Guid.NewGuid(), overrideEnvelope = Guid.NewGuid();
			await using (ApplicationDbContext db = fixture.CreateDbContext())
			{
				db.Envelopes.AddRange(
					new EnvelopeEntity { Id = rootEnvelope, Name = "Root " + rootEnvelope, IsActive = true },
					new EnvelopeEntity { Id = childEnvelope, Name = "Child " + childEnvelope, IsActive = true },
					new EnvelopeEntity { Id = overrideEnvelope, Name = "Override " + overrideEnvelope, IsActive = true });
				db.ItemTypeEnvelopeDefaults.Add(new ItemTypeEnvelopeDefaultEntity
				{
					Id = Guid.NewGuid(),
					CategoryId = seed.Root,
					EnvelopeId = rootEnvelope,
					CreatedAt = DateTimeOffset.UtcNow,
					UpdatedAt = DateTimeOffset.UtcNow,
				});
				await db.SaveChangesAsync();
			}

			// Act — the unresolved item is filled from its type's default, then keeps it when retyped to a type
			// whose default differs, and an override is kept likewise.
			var first = await Service().AssignAsync(Actor, seed.Item, new(seed.Root, seed.Revision, seed.Assignment, Guid.NewGuid(), "Typed"));
			EnvelopeAssignment afterFirst = await ReadEnvelopeAsync(seed.Item);
			await using (ApplicationDbContext db = fixture.CreateDbContext())
			{
				db.ItemTypeEnvelopeDefaults.Add(new ItemTypeEnvelopeDefaultEntity
				{
					Id = Guid.NewGuid(),
					CategoryId = seed.Child,
					EnvelopeId = childEnvelope,
					CreatedAt = DateTimeOffset.UtcNow,
					UpdatedAt = DateTimeOffset.UtcNow,
				});
				await db.SaveChangesAsync();
			}
			var second = await Service().AssignAsync(Actor, seed.Item, new(seed.Child, seed.Revision, first.Id, Guid.NewGuid(), "Retyped"));
			EnvelopeAssignment afterRetype = await ReadEnvelopeAsync(seed.Item);
			await using (ApplicationDbContext db = fixture.CreateDbContext())
			{
				ReceiptItemEntity item = await db.ReceiptItems.IgnoreAutoIncludes().SingleAsync(row => row.Id == seed.Item);
				item.EnvelopeId = overrideEnvelope;
				item.EnvelopeSource = EnvelopeSource.Item;
				await db.SaveChangesAsync();
			}
			var third = await Service().AssignAsync(Actor, seed.Item, new(seed.Root, seed.Revision, second.Id, Guid.NewGuid(), "Retyped again"));
			EnvelopeAssignment afterOverride = await ReadEnvelopeAsync(seed.Item);
			await Service().AssignAsync(Actor, seed.Item, new(null, seed.Revision, third.Id, Guid.NewGuid(), "Cleared"));
			EnvelopeAssignment afterClear = await ReadEnvelopeAsync(seed.Item);

			// Assert
			afterFirst.Should().Be(new EnvelopeAssignment(rootEnvelope, EnvelopeSource.Default));
			afterRetype.Should().Be(new EnvelopeAssignment(rootEnvelope, EnvelopeSource.Default));
			afterOverride.Should().Be(new EnvelopeAssignment(overrideEnvelope, EnvelopeSource.Item));
			afterClear.Should().Be(new EnvelopeAssignment(overrideEnvelope, EnvelopeSource.Item));
		}
		finally
		{
			await EnvelopeRowsCleanup.RemoveAllAsync(fixture);
		}
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task HistoricalApplyAndUndo_OnlyFillAnUnresolvedEnvelopeAndNeverMoveARecordedOne(bool recorded)
	{
		try
		{
			// Arrange — the cleanup retypes the item to Root, whose default differs from what it records.
			var seed = await HistoricalSeedAsync();
			Guid recordedEnvelope = Guid.NewGuid(), rootEnvelope = Guid.NewGuid();
			await using (ApplicationDbContext db = fixture.CreateDbContext())
			{
				db.Envelopes.AddRange(
					new EnvelopeEntity { Id = recordedEnvelope, Name = "Recorded " + recordedEnvelope, IsActive = true },
					new EnvelopeEntity { Id = rootEnvelope, Name = "Root " + rootEnvelope, IsActive = true });
				db.ItemTypeEnvelopeDefaults.Add(new ItemTypeEnvelopeDefaultEntity
				{
					Id = Guid.NewGuid(),
					CategoryId = seed.Root,
					EnvelopeId = rootEnvelope,
					CreatedAt = DateTimeOffset.UtcNow,
					UpdatedAt = DateTimeOffset.UtcNow,
				});
				ReceiptItemEntity item = await db.ReceiptItems.IgnoreAutoIncludes().SingleAsync(row => row.Id == seed.Item);
				item.EnvelopeId = recorded ? recordedEnvelope : null;
				item.EnvelopeSource = EnvelopeSource.Default;
				await db.SaveChangesAsync();
			}
			var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
			await Historical().SavePreviewAsync(request);
			EnvelopeAssignment expected = new(recorded ? recordedEnvelope : rootEnvelope, EnvelopeSource.Default);

			// Act
			(await Historical().ApplyChunkAsync(Actor, request.OperationId, 100)).Applied.Should().Be(1);
			EnvelopeAssignment afterApply = await ReadEnvelopeAsync(seed.Item);
			(await Historical().UndoChunkAsync(Actor, request.OperationId, new(seed.Revision, "Restore previous choice"), 100))
				.Undone.Should().Be(1);
			EnvelopeAssignment afterUndo = await ReadEnvelopeAsync(seed.Item);

			// Assert — an undo does not clear an envelope its apply filled.
			afterApply.Should().Be(expected);
			afterUndo.Should().Be(expected);
		}
		finally
		{
			await EnvelopeRowsCleanup.RemoveAllAsync(fixture);
		}
	}

	private async Task<EnvelopeAssignment> ReadEnvelopeAsync(Guid itemId)
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		return await db.ReceiptItems.IgnoreAutoIncludes().AsNoTracking().Where(row => row.Id == itemId)
			.Select(row => new EnvelopeAssignment(row.EnvelopeId, row.EnvelopeSource)).SingleAsync();
	}
}
