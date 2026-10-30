using System.Text.Json;
using Application.Classification;
using Application.Normalization;
using Common;
using FluentAssertions;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class CategoryAssistanceReadStoreTests(PostgresFixture fixture)
{
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }

	[Fact]
	public async Task ObsoleteSuggestionDoesNotExposeItsCategoryPath()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		var id = Guid.NewGuid(); var category = Guid.NewGuid();
		await using (var db = isolated.CreateDbContext())
		{
			db.CategoryAssistanceAttempts.Add(new CategoryAssistanceAttemptEntity
			{
				Id = id,
				RequestId = Guid.NewGuid(),
				RestoreGeneration = Guid.NewGuid(),
				SourceHash = new string('A', 64),
				Location = "Shop",
				Description = "Item",
				Provider = "jev",
				Model = CategorySuggestionPolicy.Model,
				QuestionPolicyVersion = "obsolete",
				State = "suggested",
				CreatedAt = DateTimeOffset.UtcNow,
				CompletedAt = DateTimeOffset.UtcNow,
				ResultJson = JsonSerializer.Serialize(new CategoryAssistanceCompletion(
					CategoryAssistanceAttemptOutcome.Suggested,
					new CategorySuggestion(category.ToString(), [category.ToString()], null, 0.9, 0.9, 0.9,
						new SuggestionProvenance("jev", CategorySuggestionPolicy.Model, "obsolete", Guid.NewGuid().ToString(), 1, 1),
						"local_choice", RootCandidates: [new(category.ToString(), 0.9)], LeafCandidates: [new(category.ToString(), 0.9)])))
			});
			await db.SaveChangesAsync();
		}
		var store = new CategoryAssistanceReadStore(new Factory(isolated), TimeProvider.System);
		var suggestion = (await store.GetSuggestionAsync(id, default))!;
		suggestion.IsCurrent.Should().BeFalse(); suggestion.ProposedPath.Should().BeEmpty();
		suggestion.RootCandidates.Should().BeEmpty(); suggestion.LeafCandidates.Should().BeEmpty();
		var activity = await store.GetActivityAsync(default);
		activity.Suggested.Should().Be(0); activity.Stale.Should().Be(1);
	}

	[Fact]
	public async Task ActivityBoundsRecentFailuresAndNeverExposesStoredProviderText()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		var newest = DateTimeOffset.UtcNow.AddMinutes(1);
		Guid newestId = Guid.NewGuid();
		await using (var db = isolated.CreateDbContext())
		{
			for (int i = 0; i < 12; i++)
			{
				db.CategoryAssistanceAttempts.Add(new CategoryAssistanceAttemptEntity
				{
					Id = i == 0 ? newestId : Guid.NewGuid(),
					RequestId = Guid.NewGuid(),
					RestoreGeneration = Guid.NewGuid(),
					SourceHash = new string('C', 64),
					Location = "Store",
					Description = $"Failure {i}",
					SourceRevision = Guid.NewGuid(),
					TaxonomyRevision = Guid.NewGuid(),
					Provider = "jev",
					Model = CategorySuggestionPolicy.Model,
					QuestionPolicyVersion = CategorySuggestionPolicy.Version,
					SettingsRevision = Guid.NewGuid(),
					ConnectionGeneration = Guid.NewGuid(),
					ReceiptId = Guid.NewGuid(),
					ReceiptRevision = Guid.NewGuid(),
					ItemId = Guid.NewGuid(),
					ItemRevision = Guid.NewGuid(),
					State = "failed",
					CreatedAt = newest.AddMinutes(-i),
					CompletedAt = newest.AddMinutes(-i),
					ResultJson = i == 0 ? "provider-secret-details" :
								JsonSerializer.Serialize(new CategoryAssistanceCompletion(
										CategoryAssistanceAttemptOutcome.Failed,
										Failure: SuggestionFailure.Timeout))
				});
			}
			await db.SaveChangesAsync();
		}

		var activity = await new CategoryAssistanceReadStore(new Factory(isolated), TimeProvider.System)
				.GetActivityAsync(default);
		activity.RecentFailures.Should().HaveCount(10);
		activity.RecentFailures[0].AttemptId.Should().Be(newestId);
		activity.RecentFailures[0].Failure.Should().Be("Unknown");
		activity.RecentFailures.Skip(1).Should().OnlyContain(x => x.Failure == "Timeout");
		activity.RecentFailures.Should().BeInDescendingOrder(x => x.CompletedAt);
	}

	[Fact]
	public async Task CompletedPageOmitsFailuresAndMarksMissingEvidenceStale()
	{
		Guid restore = Guid.NewGuid(), category = Guid.NewGuid(), otherRoot = Guid.NewGuid(), failedAttempt = Guid.NewGuid();
		var now = DateTimeOffset.UtcNow;
		await using (var db = fixture.CreateDbContext())
		{
			db.CategoryAssistanceAttempts.Add(new CategoryAssistanceAttemptEntity
			{
				Id = Guid.NewGuid(),
				RequestId = Guid.NewGuid(),
				RestoreGeneration = restore,
				SourceHash = new string('A', 64),
				Location = "Store",
				Description = "Original item",
				SourceRevision = Guid.NewGuid(),
				TaxonomyRevision = Guid.NewGuid(),
				Provider = "jev",
				Model = CategorySuggestionPolicy.Model,
				QuestionPolicyVersion = CategorySuggestionPolicy.Version,
				SettingsRevision = Guid.NewGuid(),
				ConnectionGeneration = Guid.NewGuid(),
				ReceiptId = Guid.NewGuid(),
				ReceiptRevision = Guid.NewGuid(),
				ItemId = Guid.NewGuid(),
				ItemRevision = Guid.NewGuid(),
				State = "suggested",
				CreatedAt = now,
				CompletedAt = now,
				ResultJson = JsonSerializer.Serialize(new CategoryAssistanceCompletion(
					CategoryAssistanceAttemptOutcome.Suggested,
					new CategorySuggestion(category.ToString(), [category.ToString()], null, 0.9, 0.9, 0.9,
						new SuggestionProvenance("jev", CategorySuggestionPolicy.Model,
							CategorySuggestionPolicy.Version, Guid.NewGuid().ToString(), 1, 1), "local_choice",
						RootCandidates: [new(category.ToString(), 0.9), new(otherRoot.ToString(), 0.1)],
						LeafCandidates: [new(category.ToString(), 0.9)])))
			});
			db.CategoryAssistanceAttempts.Add(new CategoryAssistanceAttemptEntity
			{
				Id = failedAttempt,
				RequestId = Guid.NewGuid(),
				RestoreGeneration = Guid.NewGuid(),
				SourceHash = new string('B', 64),
				Location = "Store",
				Description = "Failed item",
				SourceRevision = Guid.NewGuid(),
				TaxonomyRevision = Guid.NewGuid(),
				Provider = "jev",
				Model = CategorySuggestionPolicy.Model,
				QuestionPolicyVersion = CategorySuggestionPolicy.Version,
				SettingsRevision = Guid.NewGuid(),
				ConnectionGeneration = Guid.NewGuid(),
				ReceiptId = Guid.NewGuid(),
				ReceiptRevision = Guid.NewGuid(),
				ItemId = Guid.NewGuid(),
				ItemRevision = Guid.NewGuid(),
				State = "failed",
				CreatedAt = now
			});
			await db.SaveChangesAsync();
		}
		var store = new CategoryAssistanceReadStore(new Factory(fixture), TimeProvider.System);

		var page = await store.ListSuggestionsAsync(0, 1, CancellationToken.None);

		page.Total.Should().BeGreaterThanOrEqualTo(1);
		page.Items.Should().ContainSingle();
		page.Items[0].OriginalDescription.Should().BeNull();
		page.Items[0].AffectedCount.Should().Be(0);
		page.Items[0].IsCurrent.Should().BeFalse();
		page.Items[0].ProposedPath.Should().Equal(category);
		page.Items[0].ProbabilityScope.Should().Be("local_choice");
		page.Items[0].RootCandidates.Should().Equal(new CategoryAssistanceCandidateRead(category, 0.9),
			new CategoryAssistanceCandidateRead(otherRoot, 0.1));
		page.Items[0].LeafCandidates.Should().Equal(new CategoryAssistanceCandidateRead(category, 0.9));
		(await store.GetSuggestionAsync(failedAttempt, CancellationToken.None)).Should().BeNull();
	}

	[Fact]
	public async Task OriginalDescriptionUsesUnchangedRawReceiptText()
	{
		var receiptId = Guid.NewGuid();
		var itemId = Guid.NewGuid();
		var receiptRevision = Guid.NewGuid();
		var itemRevision = Guid.NewGuid();
		var source = NormalizationCatalogPolicy.Normalize(new(" Store ", "  Raw purchase  ", null));
		var attemptId = Guid.NewGuid();
		await using (var db = fixture.CreateDbContext())
		{
			db.Receipts.Add(new ReceiptEntity
			{
				Id = receiptId,
				NormalizationRevision = receiptRevision,
				Location = " Store ",
				Date = new DateOnly(2020, 1, 1),
				TaxAmountCurrency = Currency.USD
			});
			db.ReceiptItems.Add(new ReceiptItemEntity
			{
				Id = itemId,
				ReceiptId = receiptId,
				NormalizationRevision = itemRevision,
				Description = "  Raw purchase  ",
				UnitPriceCurrency = Currency.USD,
				TotalAmountCurrency = Currency.USD
			});
			await db.SaveChangesAsync();
			receiptRevision = await db.Receipts.Where(x => x.Id == receiptId)
				.Select(x => x.NormalizationRevision).SingleAsync();
			itemRevision = await db.ReceiptItems.Where(x => x.Id == itemId)
				.Select(x => x.NormalizationRevision).SingleAsync();
			db.CategoryAssistanceAttempts.Add(new CategoryAssistanceAttemptEntity
			{
				Id = attemptId,
				RequestId = Guid.NewGuid(),
				RestoreGeneration = Guid.NewGuid(),
				SourceHash = NormalizationCatalogPolicy.Hash(source),
				Location = source.Location,
				Description = source.Description,
				SourceRevision = Guid.NewGuid(),
				TaxonomyRevision = Guid.NewGuid(),
				Provider = "jev",
				Model = CategorySuggestionPolicy.Model,
				QuestionPolicyVersion = CategorySuggestionPolicy.Version,
				SettingsRevision = Guid.NewGuid(),
				ConnectionGeneration = Guid.NewGuid(),
				ReceiptId = receiptId,
				ReceiptRevision = receiptRevision,
				ItemId = itemId,
				ItemRevision = itemRevision,
				State = "abstained",
				CreatedAt = DateTimeOffset.UtcNow,
				CompletedAt = DateTimeOffset.UtcNow,
				ResultJson = JsonSerializer.Serialize(new CategoryAssistanceCompletion(
					CategoryAssistanceAttemptOutcome.Abstained, null))
			});
			await db.SaveChangesAsync();
		}
		var store = new CategoryAssistanceReadStore(new Factory(fixture), TimeProvider.System);

		var page = await store.ListSuggestionsAsync(0, 100, CancellationToken.None);

		page.Items.Single(x => x.AttemptId == attemptId).OriginalDescription.Should().Be("  Raw purchase  ");
		(await store.GetSuggestionAsync(attemptId, CancellationToken.None))!
				.OriginalDescription.Should().Be("  Raw purchase  ");
		(await store.GetSuggestionAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
	}
}
