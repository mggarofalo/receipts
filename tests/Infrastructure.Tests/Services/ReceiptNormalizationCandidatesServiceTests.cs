using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Microsoft.Extensions.Time.Testing;

namespace Infrastructure.Tests.Services;

public sealed class ReceiptNormalizationCandidatesServiceTests
{
	[Theory]
	[InlineData(false, true, 1, false)]
	[InlineData(true, true, 1, false)]
	[InlineData(true, true, -1, true)]
	[InlineData(true, false, 1, true)]
	public async Task RechecksPersistedActorBeforeRetrieval(bool exists, bool lockoutEnabled, int endHours, bool allowed)
	{
		var (factory, _) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
		await using (var seed = factory.CreateDbContext())
		{
			if (exists)
			{
				seed.Users.Add(new ApplicationUser { Id = "writer", UserName = "writer", LockoutEnabled = lockoutEnabled, LockoutEnd = clock.GetUtcNow().AddHours(endHours) });
			}

			await seed.SaveChangesAsync();
		}
		var service = new ReceiptNormalizationCandidatesService(factory, clock);
		var act = () => service.GetCandidatesAsync("writer", new("Store", "franks", null), CancellationToken.None);
		if (allowed)
		{
			(await act()).Should().BeEmpty();
		}
		else
		{
			var rejection = (await act.Should().ThrowAsync<NormalizationCatalogException>()).Which;
			rejection.StatusCode.Should().Be(403);
			rejection.Code.Should().Be("receipt_entry_actor_required");
		}
	}

	[Fact]
	public async Task ReturnsBoundedActiveIdentitiesWithoutPersistingAnIdentityOrEvidence()
	{
		var (factory, _) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		Guid exactId = Guid.NewGuid(), revision = Guid.NewGuid();
		await using (var seed = factory.CreateDbContext())
		{
			seed.Users.Add(new ApplicationUser { Id = "writer", UserName = "writer" });
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity { Id = exactId, NormalizationRevision = revision, CanonicalName = "Franks", Status = NormalizedDescriptionStatus.Active });
			foreach (int i in Enumerable.Range(0, 30))
			{
				seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity { Id = Guid.NewGuid(), CanonicalName = $"Franks {i:D2}", Status = NormalizedDescriptionStatus.Active });
			}

			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity { Id = Guid.NewGuid(), CanonicalName = "Franks rejected", Status = NormalizedDescriptionStatus.Rejected });
			await seed.SaveChangesAsync();
		}
		await using (var persisted = factory.CreateDbContext())
		{
			revision = persisted.NormalizedDescriptions.Single(item => item.Id == exactId).NormalizationRevision;
		}
		var service = new ReceiptNormalizationCandidatesService(factory, TimeProvider.System);
		var result = await service.GetCandidatesAsync("writer", new("Store", "Franks", null), CancellationToken.None);
		result.Should().HaveCount(20);
		result[0].Id.Should().Be(exactId);
		result[0].Revision.Should().Be(revision);
		result[0].Channels.Should().Contain("exact");
		result.Should().NotContain(item => item.CanonicalName == "Franks rejected");
		await using var verify = factory.CreateDbContext();
		verify.NormalizedDescriptions.Count().Should().Be(32);
		verify.NormalizationSourceMappings.Should().BeEmpty();
		verify.ReceiptItems.Should().BeEmpty();
	}
}
