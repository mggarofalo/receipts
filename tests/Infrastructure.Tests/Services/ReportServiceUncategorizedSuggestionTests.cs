using System.Text.Json;
using Application.Classification;
using Application.Models.Reports;
using FluentAssertions;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Tests.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

// The exceptions view (RECEIPTS-1085): uncategorized items carry Jev's latest stored answer and can be
// filtered by whether Jev has answered. InMemory proves the logic; ReportServiceExceptionsViewTests in the
// integration suite proves the SQL translation.
public class ReportServiceUncategorizedSuggestionTests
{
	private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task GetUncategorizedItemsAsync_StaleAttemptStillListsItemWithItsSuggestion()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> factory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid itemId = Guid.NewGuid(), root = Guid.NewGuid(), leaf = Guid.NewGuid(), otherLeaf = Guid.NewGuid();
		Guid attemptId = Guid.NewGuid();
		await using (ApplicationDbContext context = factory.CreateDbContext())
		{
			Guid receiptId = AddReceipt(context, "Corner Market");
			AddItem(context, receiptId, itemId, "Apples");
			context.CategoryAssistanceAttempts.Add(Attempt(attemptId, itemId, "stale", Now,
				Suggested([root, leaf], 0.82, roots: [new(root.ToString(), 0.9)],
					leaves: [new(leaf.ToString(), 0.8), new(otherLeaf.ToString(), 0.15)])));
			await context.SaveChangesAsync();
		}

		// Act
		UncategorizedItemsResult result = await new ReportService(factory)
			.GetUncategorizedItemsAsync("description", "asc", 1, 50, "any", CancellationToken.None);

		// Assert
		UncategorizedItemRecord item = result.Items.Should().ContainSingle().Subject;
		item.Id.Should().Be(itemId);
		item.Location.Should().Be("Corner Market");
		UncategorizedItemSuggestion suggestion = item.Suggestion!;
		suggestion.AttemptId.Should().Be(attemptId);
		suggestion.State.Should().Be("stale");
		suggestion.CategoryId.Should().Be(leaf);
		suggestion.Confidence.Should().Be(0.82);
		suggestion.AbstentionReason.Should().BeNull();
		suggestion.RootCandidates.Should().Equal(new CategoryAssistanceCandidateRead(root, 0.9));
		suggestion.LeafCandidates.Should().Equal(
			new CategoryAssistanceCandidateRead(leaf, 0.8), new CategoryAssistanceCandidateRead(otherLeaf, 0.15));
	}

	[Fact]
	public async Task GetUncategorizedItemsAsync_LatestCompletedAttemptWins_AcrossObservationAndDirectLinks()
	{
		// Arrange — an older direct attempt, a newer one linked only through a save observation,
		// and a queued attempt with no result that must be ignored.
		IDbContextFactory<ApplicationDbContext> factory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid itemId = Guid.NewGuid(), olderLeaf = Guid.NewGuid(), newerLeaf = Guid.NewGuid();
		Guid olderId = Guid.NewGuid(), newerId = Guid.NewGuid();
		await using (ApplicationDbContext context = factory.CreateDbContext())
		{
			Guid receiptId = AddReceipt(context, "Store");
			AddItem(context, receiptId, itemId, "Bread");
			context.CategoryAssistanceAttempts.Add(Attempt(olderId, itemId, "suggested", Now.AddDays(-2),
				Suggested([olderLeaf], 0.6)));
			context.CategoryAssistanceAttempts.Add(Attempt(newerId, Guid.NewGuid(), "stale", Now,
				Suggested([newerLeaf], 0.7)));
			context.CategoryAssistanceAttempts.Add(Attempt(Guid.NewGuid(), itemId, "queued", null, null));
			context.CategoryAssistanceObservations.Add(Observation(newerId, itemId, receiptId));
			await context.SaveChangesAsync();
		}

		// Act
		UncategorizedItemsResult result = await new ReportService(factory)
			.GetUncategorizedItemsAsync("description", "asc", 1, 50, "any", CancellationToken.None);

		// Assert
		UncategorizedItemSuggestion suggestion = result.Items.Should().ContainSingle().Subject.Suggestion!;
		suggestion.AttemptId.Should().Be(newerId);
		suggestion.CategoryId.Should().Be(newerLeaf);
		suggestion.Confidence.Should().Be(0.7);
	}

	[Fact]
	public async Task GetUncategorizedItemsAsync_AbstentionCarriesReasonAndNoCategory()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> factory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid itemId = Guid.NewGuid();
		await using (ApplicationDbContext context = factory.CreateDbContext())
		{
			Guid receiptId = AddReceipt(context, "Store");
			AddItem(context, receiptId, itemId, "Mystery");
			context.CategoryAssistanceAttempts.Add(Attempt(Guid.NewGuid(), itemId, "abstained", Now,
				JsonSerializer.Serialize(new CategoryAssistanceCompletion(CategoryAssistanceAttemptOutcome.Abstained,
					new CategorySuggestion(null, [], "no_fit", 0.2, 0, 0.2, Provenance())))));
			await context.SaveChangesAsync();
		}

		// Act
		UncategorizedItemsResult result = await new ReportService(factory)
			.GetUncategorizedItemsAsync("description", "asc", 1, 50, "any", CancellationToken.None);

		// Assert
		UncategorizedItemSuggestion suggestion = result.Items.Should().ContainSingle().Subject.Suggestion!;
		suggestion.State.Should().Be("abstained");
		suggestion.CategoryId.Should().BeNull();
		suggestion.AbstentionReason.Should().Be("no_fit");
		suggestion.Confidence.Should().Be(0.2);
	}

	[Fact]
	public async Task GetUncategorizedItemsAsync_EarlierQuestionPolicyHidesCategoryAndCandidates()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> factory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid itemId = Guid.NewGuid(), leaf = Guid.NewGuid();
		await using (ApplicationDbContext context = factory.CreateDbContext())
		{
			Guid receiptId = AddReceipt(context, "Store");
			AddItem(context, receiptId, itemId, "Old answer");
			CategoryAssistanceAttemptEntity attempt = Attempt(Guid.NewGuid(), itemId, "suggested", Now,
				Suggested([leaf], 0.9, roots: [new(leaf.ToString(), 0.9)], leaves: [new(leaf.ToString(), 0.9)]));
			attempt.QuestionPolicyVersion = "classification-questions-v1";
			context.CategoryAssistanceAttempts.Add(attempt);
			await context.SaveChangesAsync();
		}

		// Act
		UncategorizedItemsResult result = await new ReportService(factory)
			.GetUncategorizedItemsAsync("description", "asc", 1, 50, "any", CancellationToken.None);

		// Assert
		UncategorizedItemSuggestion suggestion = result.Items.Should().ContainSingle().Subject.Suggestion!;
		suggestion.CategoryId.Should().BeNull();
		suggestion.RootCandidates.Should().BeEmpty();
		suggestion.LeafCandidates.Should().BeEmpty();
	}

	[Theory]
	[InlineData("any", new[] { "Answered directly", "Answered via save", "Never asked", "Still queued" })]
	[InlineData("with", new[] { "Answered directly", "Answered via save" })]
	[InlineData("WITH", new[] { "Answered directly", "Answered via save" })]
	[InlineData("without", new[] { "Never asked", "Still queued" })]
	public async Task GetUncategorizedItemsAsync_SuggestionFilterSelectsItemsAndCount(string filter, string[] expected)
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> factory = DbContextHelpers.CreateInMemoryContextFactory();
		await using (ApplicationDbContext context = factory.CreateDbContext())
		{
			Guid receiptId = AddReceipt(context, "Store");
			Guid direct = AddItem(context, receiptId, Guid.NewGuid(), "Answered directly");
			Guid observed = AddItem(context, receiptId, Guid.NewGuid(), "Answered via save");
			AddItem(context, receiptId, Guid.NewGuid(), "Never asked");
			Guid queued = AddItem(context, receiptId, Guid.NewGuid(), "Still queued");
			context.CategoryAssistanceAttempts.Add(Attempt(Guid.NewGuid(), direct, "suggested", Now,
				Suggested([Guid.NewGuid()], 0.9)));
			Guid observedAttempt = Guid.NewGuid();
			context.CategoryAssistanceAttempts.Add(Attempt(observedAttempt, Guid.NewGuid(), "stale", Now,
				Suggested([Guid.NewGuid()], 0.9)));
			context.CategoryAssistanceObservations.Add(Observation(observedAttempt, observed, receiptId));
			Guid queuedAttempt = Guid.NewGuid();
			context.CategoryAssistanceAttempts.Add(Attempt(queuedAttempt, queued, "queued", null, null));
			context.CategoryAssistanceObservations.Add(Observation(queuedAttempt, queued, receiptId));
			await context.SaveChangesAsync();
		}
		ReportService service = new(factory);

		// Act
		UncategorizedItemsResult page = await service
			.GetUncategorizedItemsAsync("description", "asc", 1, 1, filter, CancellationToken.None);
		UncategorizedItemsResult all = await service
			.GetUncategorizedItemsAsync("description", "asc", 1, 50, filter, CancellationToken.None);

		// Assert
		page.TotalCount.Should().Be(expected.Length);
		page.Items.Should().ContainSingle();
		all.Items.Select(i => i.Description).Should().Equal(expected);
		all.Items.Where(i => i.Description.StartsWith("Answered")).Should().OnlyContain(i => i.Suggestion != null);
		all.Items.Where(i => !i.Description.StartsWith("Answered")).Should().OnlyContain(i => i.Suggestion == null);
	}

	private static Guid AddReceipt(ApplicationDbContext context, string location)
	{
		Guid id = Guid.NewGuid();
		context.Receipts.Add(new ReceiptEntity { Id = id, Location = location, Date = new DateOnly(2026, 9, 1), TaxAmount = 0m });
		return id;
	}

	private static Guid AddItem(ApplicationDbContext context, Guid receiptId, Guid id, string description)
	{
		context.ReceiptItems.Add(new ReceiptItemEntity
		{
			Id = id,
			ReceiptId = receiptId,
			Description = description,
			Quantity = 1,
			UnitPrice = 1m,
			TotalAmount = 1m,
			Category = "Uncategorized"
		});
		return id;
	}

	private static CategoryAssistanceAttemptEntity Attempt(Guid id, Guid itemId, string state,
		DateTimeOffset? completedAt, string? resultJson) => new()
		{
			Id = id,
			RequestId = Guid.NewGuid(),
			ItemId = itemId,
			Provider = "jev",
			Model = CategorySuggestionPolicy.Model,
			QuestionPolicyVersion = CategorySuggestionPolicy.Version,
			State = state,
			CreatedAt = completedAt ?? Now,
			CompletedAt = completedAt,
			ResultJson = resultJson
		};

	private static CategoryAssistanceObservationEntity Observation(Guid attemptId, Guid itemId, Guid receiptId) => new()
	{
		AttemptId = attemptId,
		SaveId = Guid.NewGuid(),
		ReceiptItemId = itemId,
		ReceiptId = receiptId,
		RecordedAt = Now
	};

	private static string Suggested(Guid[] path, double confidence,
		SuggestionCandidate[]? roots = null, SuggestionCandidate[]? leaves = null) =>
		JsonSerializer.Serialize(new CategoryAssistanceCompletion(CategoryAssistanceAttemptOutcome.Suggested,
			new CategorySuggestion(path[^1].ToString(), [.. path.Select(x => x.ToString())], null,
				0.9, confidence, confidence, Provenance(), "local_choice",
				RootCandidates: roots, LeafCandidates: leaves)));

	private static SuggestionProvenance Provenance() =>
		new("jev", CategorySuggestionPolicy.Model, CategorySuggestionPolicy.Version, Guid.NewGuid().ToString(), 1, 1);
}
