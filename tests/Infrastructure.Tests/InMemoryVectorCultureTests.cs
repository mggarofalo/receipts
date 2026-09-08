using System.Globalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Infrastructure.Tests;

public class InMemoryVectorCultureTests
{
	[Theory]
	[InlineData(false, "de-DE", "en-US")]
	[InlineData(false, "en-US", "de-DE")]
	[InlineData(true, "de-DE", "en-US")]
	[InlineData(true, "en-US", "de-DE")]
	[InlineData(false, "de-DE", "de-DE")]
	[InlineData(false, "en-US", "en-US")]
	[InlineData(true, "de-DE", "de-DE")]
	[InlineData(true, "en-US", "en-US")]
	public async Task Embedding_SaveAndFreshRead_PreserveValuesAcrossCultures(
		bool canonical, string writeCulture, string readCulture)
	{
		(IDbContextFactory<ApplicationDbContext> factory, _) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		Guid id = Guid.NewGuid();
		float[] expected = [0.33621293f, -0.125f, 0f, 1f, 1.2345678e-10f, 9876.125f];
		CultureInfo originalCulture = CultureInfo.CurrentCulture;
		try
		{
			// Only this async flow changes culture. Never change the process-wide default:
			// unrelated parallel tests must not inherit either side of this boundary.
			CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(writeCulture);
			using (ApplicationDbContext seed = factory.CreateDbContext())
			{
				if (canonical)
				{
					seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
					{
						Id = id,
						CanonicalName = "Milk",
						Status = NormalizedDescriptionStatus.Active,
						Embedding = new Vector(expected),
						EmbeddingModelVersion = "culture-fixture",
						CreatedAt = DateTimeOffset.UtcNow,
					});
				}
				else
				{
					seed.ItemEmbeddings.Add(new ItemEmbeddingEntity
					{
						Id = id,
						EntityId = Guid.NewGuid(),
						EntityType = "ReceiptItem",
						EntityText = "Milk",
						Embedding = new Vector(expected),
						ModelVersion = "culture-fixture",
						CreatedAt = DateTimeOffset.UtcNow,
					});
				}
				await seed.SaveChangesAsync();
			}

			CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(readCulture);
			using ApplicationDbContext read = factory.CreateDbContext();
			Vector actual = canonical
				? (await read.NormalizedDescriptions.AsNoTracking().SingleAsync(row => row.Id == id)).Embedding!
				: (await read.ItemEmbeddings.AsNoTracking().SingleAsync(row => row.Id == id)).Embedding;

			// Compare ordered, exact values, not an approximate similarity or a converter
			// called directly. A new context must materialize the actual provider value.
			actual.ToArray().Should().Equal(expected);
		}
		finally
		{
			CultureInfo.CurrentCulture = originalCulture;
		}
	}

	[Fact]
	public async Task CanonicalEmbedding_NullRemainsNullAcrossCultureBoundary()
	{
		(IDbContextFactory<ApplicationDbContext> factory, _) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		Guid id = Guid.NewGuid();
		CultureInfo originalCulture = CultureInfo.CurrentCulture;
		try
		{
			CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
			using (ApplicationDbContext seed = factory.CreateDbContext())
			{
				seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
				{
					Id = id,
					CanonicalName = "Unresolved milk",
					Status = NormalizedDescriptionStatus.PendingReview,
					Embedding = null,
					CreatedAt = DateTimeOffset.UtcNow,
				});
				await seed.SaveChangesAsync();
			}

			CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
			using ApplicationDbContext read = factory.CreateDbContext();
			NormalizedDescriptionEntity row = await read.NormalizedDescriptions.AsNoTracking().SingleAsync(row => row.Id == id);
			row.Embedding.Should().BeNull();
			row.CanonicalName.Should().Be("Unresolved milk");
		}
		finally
		{
			CultureInfo.CurrentCulture = originalCulture;
		}
	}
}
