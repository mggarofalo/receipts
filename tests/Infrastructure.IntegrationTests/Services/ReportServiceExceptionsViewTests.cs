using System.Text.Json;
using Application.Classification;
using Application.Models.Reports;
using FluentAssertions;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

// RECEIPTS-1085: the exceptions view filters uncategorized items by whether Jev answered, through a
// Contains over a Concat of two attempt subqueries. InMemory evaluates that in-process; only a real
// provider proves Npgsql translates it.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class ReportServiceExceptionsViewTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	[Fact]
	public async Task SuggestionFilter_TranslatesToSql_AndAttachesLatestAnswer()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid(), leaf = Guid.NewGuid();
		Guid direct = Guid.NewGuid(), observed = Guid.NewGuid(), never = Guid.NewGuid();
		Guid directAttempt = Guid.NewGuid(), observedAttempt = Guid.NewGuid();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Receipts.Add(new ReceiptEntity { Id = receiptId, Location = "Exceptions Store", Date = new DateOnly(2026, 9, 1), TaxAmount = 0m });
			foreach ((Guid id, string description) in new[] { (direct, "A direct"), (observed, "B observed"), (never, "C never") })
			{
				seed.ReceiptItems.Add(new ReceiptItemEntity
				{
					Id = id,
					ReceiptId = receiptId,
					Description = description,
					Quantity = 1,
					UnitPrice = 1m,
					TotalAmount = 1m,
					Category = "Uncategorized"
				});
			}
			seed.CategoryAssistanceAttempts.Add(Attempt(directAttempt, direct, "stale", now, leaf));
			seed.CategoryAssistanceAttempts.Add(Attempt(observedAttempt, Guid.NewGuid(), "suggested", now, leaf));
			seed.CategoryAssistanceObservations.Add(new CategoryAssistanceObservationEntity
			{
				AttemptId = observedAttempt,
				SaveId = Guid.NewGuid(),
				ReceiptItemId = observed,
				ReceiptId = receiptId,
				Actor = "report-test",
				SourceHash = new string('B', 64),
				RecordedAt = now
			});
			await seed.SaveChangesAsync();
		}
		ReportService report = new(new FixtureDbContextFactory(fixture));

		// Act
		UncategorizedItemsResult with = await report.GetUncategorizedItemsAsync("description", "asc", 1, 50, "with", true, CancellationToken.None);
		UncategorizedItemsResult without = await report.GetUncategorizedItemsAsync("description", "asc", 1, 50, "without", true, CancellationToken.None);
		UncategorizedItemsResult any = await report.GetUncategorizedItemsAsync("description", "asc", 1, 50, "any", true, CancellationToken.None);

		// Assert
		with.TotalCount.Should().Be(2);
		with.Items.Select(item => item.Id).Should().Equal(direct, observed);
		with.Items.Select(item => item.Suggestion!.AttemptId).Should().Equal(directAttempt, observedAttempt);
		with.Items.Should().OnlyContain(item => item.Suggestion!.CategoryId == leaf && item.Location == "Exceptions Store");
		without.TotalCount.Should().Be(1);
		without.Items.Should().ContainSingle().Which.Suggestion.Should().BeNull();
		without.Items[0].Id.Should().Be(never);
		any.TotalCount.Should().Be(3);
	}

	private static CategoryAssistanceAttemptEntity Attempt(Guid id, Guid itemId, string state, DateTimeOffset completedAt, Guid leaf) => new()
	{
		Id = id,
		RequestId = Guid.NewGuid(),
		RequestScope = Guid.NewGuid(),
		RestoreGeneration = Guid.NewGuid(),
		SourceHash = new string('A', 64),
		Location = "Exceptions Store",
		Description = "Item",
		Provider = "jev",
		Model = CategorySuggestionPolicy.Model,
		QuestionPolicyVersion = CategorySuggestionPolicy.Version,
		ItemId = itemId,
		State = state,
		CreatedAt = completedAt,
		CompletedAt = completedAt,
		ResultJson = JsonSerializer.Serialize(new CategoryAssistanceCompletion(
			CategoryAssistanceAttemptOutcome.Suggested,
			new CategorySuggestion(leaf.ToString(), [leaf.ToString()], null, 0.9, 0.9, 0.9,
				new SuggestionProvenance("jev", CategorySuggestionPolicy.Model, CategorySuggestionPolicy.Version, Guid.NewGuid().ToString(), 1, 1),
				"local_choice", LeafCandidates: [new(leaf.ToString(), 0.9)])))
	};

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
