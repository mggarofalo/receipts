using System.Security.Cryptography;
using System.Text.Json;
using Application.Classification;
using FluentAssertions;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	[Fact]
	public async Task ReviewPreferences_CandidateCodecPreservesDetachedStateAndRetry()
	{
		var attempt = Guid.NewGuid(); var revision = Guid.NewGuid(); var item = Guid.NewGuid();
		var preference = new CategoryAssistanceReviewPreferenceEntity
		{
			Actor = "missing-actor",
			AttemptId = attempt,
			Location = "store",
			Description = "item",
			State = "later",
			Revision = revision,
			ExcludedItemIdsJson = JsonSerializer.Serialize(new[] { item })
		};
		var operation = new CategoryAssistanceReviewOperationEntity
		{
			Actor = preference.Actor,
			AttemptId = attempt,
			OperationId = Guid.NewGuid(),
			RequestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { attemptId = attempt, ExpectedRevision = Guid.Empty, State = "later", excludedItemIds = new[] { item } }))),
			RecordedAt = DateTimeOffset.UtcNow,
			ResultJson = JsonSerializer.Serialize(new CategoryAssistanceReviewPreference(attempt, "later", revision, new[] { item }))
		};
		var state = MappingState() with { IncludesReviewPreferences = true, ReviewPreferences = [preference], ReviewOperations = [operation] };
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await PortableTaxonomyArchive.WriteReviewPreferencesAsync(sqlite, state, default);
		var read = await PortableTaxonomyArchive.ReadReviewPreferencesAsync(sqlite, state, default);
		read.ReviewPreferences.Should().BeEquivalentTo(state.ReviewPreferences);
		read.ReviewOperations.Should().BeEquivalentTo(state.ReviewOperations);
		PortableTaxonomyArchive.ValidateCandidateDestination(state, read);
		read.ReviewPreferences[0].State = "active";
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateCandidateDestination(state, read))
			.Should().Throw<InvalidDataException>().WithMessage("*differs from operation result*");
	}

	[Fact]
	public async Task ReviewPreferences_RejectsMissingRequiredFields()
	{
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await PortableTaxonomyArchive.WriteReviewPreferencesAsync(sqlite, MappingState() with { IncludesReviewPreferences = true }, default);
		await using var command = sqlite.CreateCommand();
		command.CommandText = "INSERT INTO review_preference_archive VALUES('preference','x','{}')";
		await command.ExecuteNonQueryAsync();
		Func<Task> read = () => PortableTaxonomyArchive.ReadReviewPreferencesAsync(sqlite, MappingState(), default);
		await read.Should().ThrowAsync<InvalidDataException>();
	}
}
