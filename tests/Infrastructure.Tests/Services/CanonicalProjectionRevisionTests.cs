using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Infrastructure.Tests.Services;

public class CanonicalProjectionRevisionTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task NonrelationalRevision_OnlyProjectionChangesPreserveBusinessRevision(bool businessEdit)
	{
		var (factory, _) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		await using var db = factory.CreateDbContext();
		var target = new NormalizedDescriptionEntity { CanonicalName = "Original", Status = NormalizedDescriptionStatus.Active };
		db.NormalizedDescriptions.Add(target);
		await db.SaveChangesAsync();
		var revision = target.NormalizationRevision;
		revision.Should().NotBeEmpty();
		target.Embedding = new Vector(new float[1024]);
		target.EmbeddingModelVersion = "rebuilt";
		if (businessEdit) { target.DisplayLabel = "Reviewed label"; }
		await db.SaveChangesAsync();
		await db.Entry(target).ReloadAsync();
		if (businessEdit)
		{
			target.NormalizationRevision.Should().NotBe(revision);
			var changed = target.NormalizationRevision;
			target.DisplayLabel = null;
			await db.SaveChangesAsync();
			target.NormalizationRevision.Should().NotBe(revision).And.NotBe(changed);
		}
		else { target.NormalizationRevision.Should().Be(revision); }
	}
}
