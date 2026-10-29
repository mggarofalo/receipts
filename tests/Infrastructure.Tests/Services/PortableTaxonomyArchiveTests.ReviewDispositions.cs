using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	private static PortableTaxonomyArchive.State DispositionState()
	{
		var state = MappingState();
		var group = new HistoricalGroupKey("store", "item", null);
		var time = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
		var first = new HistoricalDispositionOperationEntity
		{
			OperationId = Guid.NewGuid(),
			Actor = "missing-actor",
			Store = group.Store,
			RawDescription = group.RawDescription,
			ItemCode = group.ItemCode,
			SourceHash = HistoricalReclassificationService.DispositionSourceHash(group),
			Disposition = HistoricalGroupDisposition.Later,
			ResultVersion = 1,
			ResultUpdatedAt = time
		};
		var second = new HistoricalDispositionOperationEntity
		{
			OperationId = Guid.NewGuid(),
			Actor = first.Actor,
			Store = first.Store,
			RawDescription = first.RawDescription,
			ItemCode = first.ItemCode,
			SourceHash = first.SourceHash,
			Disposition = null,
			ExpectedVersion = 1,
			ResultVersion = 2,
			ResultUpdatedAt = time.AddMinutes(1)
		};
		return state with
		{
			IncludesReviewDispositions = true,
			ReviewDispositionOperations = [first, second],
			ReviewDispositions = [new() { Id = Guid.NewGuid(), Actor = second.Actor, Store = second.Store, RawDescription = second.RawDescription,
				ItemCode = second.ItemCode, SourceHash = second.SourceHash, Disposition = second.Disposition, Version = second.ResultVersion,
				OperationId = second.OperationId, UpdatedAt = second.ResultUpdatedAt }]
		};
	}

	[Fact]
	public async Task ReviewDispositions_CandidateArchiveRetainsReopenedStateAndRetryHistory()
	{
		var state = DispositionState();
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await PortableTaxonomyArchive.WriteReviewDispositionsAsync(sqlite, state, CancellationToken.None);
		await using var command = sqlite.CreateCommand();
		command.CommandText = "SELECT count(*) FROM review_disposition_archive";
		(await command.ExecuteScalarAsync()).Should().Be(3L);
		PortableTaxonomyArchive.ValidateCandidateDestination(state, state);
		state.ReviewDispositionOperations[0].Disposition.Should().Be(HistoricalGroupDisposition.Later);
		state.ReviewDispositions[0].Disposition.Should().BeNull();
	}

	[Theory]
	[InlineData("source")]
	[InlineData("ledger")]
	[InlineData("destination")]
	public void ReviewDispositions_RejectsInvalidHistoryAndDivergentDestination(string corruption)
	{
		var state = DispositionState();
		var other = DispositionState();
		if (corruption == "source") { state.ReviewDispositions[0].SourceHash = new string('A', 64); }
		if (corruption == "ledger") { state.ReviewDispositionOperations.RemoveAt(0); }
		if (corruption == "destination")
		{
			other = state with
			{
				ReviewDispositions = [new() { Id = state.ReviewDispositions[0].Id, Actor = "another", Store = "elsewhere", RawDescription = "other",
				SourceHash = HistoricalReclassificationService.DispositionSourceHash(new HistoricalGroupKey("elsewhere", "other", null)), Version = 1,
				OperationId = Guid.NewGuid(), UpdatedAt = state.ReviewDispositions[0].UpdatedAt }],
				ReviewDispositionOperations = []
			};
			var row = other.ReviewDispositions[0];
			other.ReviewDispositionOperations.Add(new()
			{
				Actor = row.Actor,
				Store = row.Store,
				RawDescription = row.RawDescription,
				SourceHash = row.SourceHash,
				OperationId = row.OperationId,
				ResultVersion = 1,
				ResultUpdatedAt = row.UpdatedAt
			});
		}
		Action act = () => PortableTaxonomyArchive.ValidateCandidateDestination(other, state);
		act.Should().Throw<InvalidDataException>();
	}
}
