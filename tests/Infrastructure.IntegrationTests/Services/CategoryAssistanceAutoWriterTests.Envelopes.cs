using Application.Classification;
using Domain.Envelopes;
using FluentAssertions;
using Infrastructure.Entities.Envelopes;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class CategoryAssistanceAutoWriterTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task JevWriteDerivesTheItemTypesDefaultEnvelopeLikeAnyOtherClassificationWrite(bool leafEnabled)
	{
		// Arrange — only the root has a default, so a leaf write inherits it from its nearest ancestor.
		var seed = await SeedAsync(root: 0.95, leaf: 0.95);
		Guid envelope = Guid.NewGuid();
		await using (var db = fixture.CreateDbContext())
		{
			db.Envelopes.Add(new EnvelopeEntity { Id = envelope, Name = "Groceries " + envelope, IsActive = true });
			db.ItemTypeEnvelopeDefaults.Add(new ItemTypeEnvelopeDefaultEntity
			{
				Id = Guid.NewGuid(),
				CategoryId = seed.Root,
				EnvelopeId = envelope,
				CreatedAt = DateTimeOffset.UtcNow,
				UpdatedAt = DateTimeOffset.UtcNow,
			});
			await db.SaveChangesAsync();
		}
		Guid manual = await AddItemAsync(seed, attach: false);
		var legacy = await CurrentAsync(manual);
		await Classification().AssignAsync(seed.Actor, manual, new(leafEnabled ? seed.Leaf : seed.Root, seed.Taxonomy, legacy.Id,
			Guid.NewGuid(), "Owner's choice"));

		// Act
		(await Writer(new CategoryAutoWriteOptions { LeafAutoWriteEnabled = leafEnabled }).ApplyNextAsync()).Should().BeTrue();

		// Assert
		var written = await CurrentAsync(seed.Items[0]);
		written.Source.Should().Be("jev");
		written.CategoryId.Should().Be(leafEnabled ? seed.Leaf : seed.Root);
		EnvelopeAssignment expected = await ReadEnvelopeAsync(manual);
		expected.Should().Be(new EnvelopeAssignment(envelope, EnvelopeSource.Default));
		(await ReadEnvelopeAsync(seed.Items[0])).Should().Be(expected);
	}

	private async Task<EnvelopeAssignment> ReadEnvelopeAsync(Guid itemId)
	{
		await using var db = fixture.CreateDbContext();
		return await db.ReceiptItems.IgnoreAutoIncludes().AsNoTracking().Where(row => row.Id == itemId)
			.Select(row => new EnvelopeAssignment(row.EnvelopeId, row.EnvelopeSource)).SingleAsync();
	}
}
