using Domain.Envelopes;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Envelopes;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task Assign_DefaultSourcedEnvelopeFollowsItemTypeButOverrideIsKept()
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

		// Act — the child has no default of its own, so the nearest ancestor's applies.
		var first = await Service().AssignAsync(Actor, seed.Item, new(seed.Child, seed.Revision, seed.Assignment, Guid.NewGuid(), "Typed"));
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
			ReceiptItemEntity item = await db.ReceiptItems.IgnoreAutoIncludes().SingleAsync(row => row.Id == seed.Item);
			item.EnvelopeId = overrideEnvelope;
			item.EnvelopeSource = EnvelopeSource.Item;
			await db.SaveChangesAsync();
		}
		await Service().AssignAsync(Actor, seed.Item, new(seed.Root, seed.Revision, first.Id, Guid.NewGuid(), "Retyped"));
		EnvelopeAssignment afterOverride = await ReadEnvelopeAsync(seed.Item);

		// Assert
		afterFirst.Should().Be(new EnvelopeAssignment(rootEnvelope, EnvelopeSource.Default));
		afterOverride.Should().Be(new EnvelopeAssignment(overrideEnvelope, EnvelopeSource.Item));
	}

	private async Task<EnvelopeAssignment> ReadEnvelopeAsync(Guid itemId)
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		return await db.ReceiptItems.IgnoreAutoIncludes().AsNoTracking().Where(row => row.Id == itemId)
			.Select(row => new EnvelopeAssignment(row.EnvelopeId, row.EnvelopeSource)).SingleAsync();
	}
}
