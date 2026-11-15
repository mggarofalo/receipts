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
	public async Task JevWriteFillsAnUnresolvedEnvelopeFromTheItemTypesDefaultLikeAnyOtherClassificationWrite(bool leafEnabled)
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

	[Theory]
	[InlineData(EnvelopeSource.Default)]
	[InlineData(EnvelopeSource.Historical)]
	public async Task JevWriteKeepsARecordedEnvelope(EnvelopeSource source)
	{
		// Arrange — the item already records an envelope that differs from its new type's default.
		var seed = await SeedAsync(root: 0.95, leaf: 0.95);
		Guid recorded = Guid.NewGuid(), typeDefault = Guid.NewGuid();
		await using (var db = fixture.CreateDbContext())
		{
			db.Envelopes.AddRange(
				new EnvelopeEntity { Id = recorded, Name = "Party " + recorded, IsActive = true },
				new EnvelopeEntity { Id = typeDefault, Name = "Groceries " + typeDefault, IsActive = true });
			db.ItemTypeEnvelopeDefaults.Add(new ItemTypeEnvelopeDefaultEntity
			{
				Id = Guid.NewGuid(),
				CategoryId = seed.Root,
				EnvelopeId = typeDefault,
				CreatedAt = DateTimeOffset.UtcNow,
				UpdatedAt = DateTimeOffset.UtcNow,
			});
			var item = await db.ReceiptItems.IgnoreAutoIncludes().SingleAsync(row => row.Id == seed.Items[0]);
			item.EnvelopeId = recorded;
			item.EnvelopeSource = source;
			await db.SaveChangesAsync();
		}

		// Act
		(await Writer(new CategoryAutoWriteOptions()).ApplyNextAsync()).Should().BeTrue();

		// Assert
		(await CurrentAsync(seed.Items[0])).Source.Should().Be("jev");
		(await ReadEnvelopeAsync(seed.Items[0])).Should().Be(new EnvelopeAssignment(recorded, source));
	}

	private async Task<EnvelopeAssignment> ReadEnvelopeAsync(Guid itemId)
	{
		await using var db = fixture.CreateDbContext();
		return await db.ReceiptItems.IgnoreAutoIncludes().AsNoTracking().Where(row => row.Id == itemId)
			.Select(row => new EnvelopeAssignment(row.EnvelopeId, row.EnvelopeSource)).SingleAsync();
	}
}
