using System.Security.Cryptography;
using System.Text.Json;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	private static PortableTaxonomyArchive.State HistoricalState()
	{
		var state = EmptyState();
		var groups = new[] { new HistoricalGroupSelection(new(" Store ", "raw", null), null) };
		var header = new HistoricalOperationEntity
		{
			Id = Guid.NewGuid(),
			Actor = "actor",
			Reason = "category review",
			TaxonomyRevisionId = state.Heads[0].RevisionId,
			CapturedAt = state.Revisions[0].RecordedAt,
			SelectionJson = JsonSerializer.Serialize(groups)
		};
		header.RequestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
		{ header.Actor, header.TaxonomyRevisionId, header.Reason, Groups = groups })));
		return state with
		{
			IncludesHistorical = true,
			HistoricalOperations = [header],
			HistoricalItems = [new()
		{
			OperationId = header.Id, ItemId = Guid.NewGuid(), ReceiptId = Guid.NewGuid(), Store = " Store ", RawDescription = "raw",
			ItemRevision = Guid.NewGuid(), ReceiptRevision = Guid.NewGuid(), ApplyOperationId = Guid.NewGuid(), UndoOperationId = Guid.NewGuid()
		}]
		};
	}

	[Fact]
	public void Historical_ValidatesFrozenSelectionReferencesAndOutcomeEvidence()
	{
		var state = HistoricalState();
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().NotThrow();
		var row = state.HistoricalItems[0];
		row.ExpectedAssignmentId = Guid.NewGuid();
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>().WithMessage("*previous assignment*");
		row.ExpectedAssignmentId = null;
		state.HistoricalOperations[0].State = HistoricalOperationState.Applying;
		row.State = HistoricalItemState.Applied;
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>().WithMessage("*lacks assignment*");
		row.State = HistoricalItemState.Pending;
		row.UndoOperationId = row.ApplyOperationId;
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>().WithMessage("*manifest evidence*");
	}

	[Fact]
	public void Historical_RejectsAutomaticProvenanceOnManualHeader()
	{
		var state = HistoricalState();
		state.HistoricalOperations[0].ApprovedMappingRevisionId = Guid.NewGuid();
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state))
			.Should().Throw<InvalidDataException>().WithMessage("*headers cannot carry automatic provenance*");
	}

	[Fact]
	public void Historical_DestinationRejectsChangedProgressAndPreservesOlderFormatOmission()
	{
		var existing = HistoricalState();
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateDestination(existing, EmptyState())).Should().NotThrow();
		var header = JsonSerializer.Deserialize<HistoricalOperationEntity>(JsonSerializer.Serialize(existing.HistoricalOperations[0]))!;
		header.State = HistoricalOperationState.Cancelled;
		var incoming = existing with { HistoricalOperations = [header] };
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateDestination(existing, incoming)).Should().Throw<InvalidDataException>().WithMessage("*historical destination*");
	}

	[Fact]
	public async Task Historical_ReadRequiresVersion12TableAndRejectsMissingNullableEvidence()
	{
		var state = HistoricalState();
		string json = JsonSerializer.Serialize(state.HistoricalItems[0]);
		var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
		node.Remove("ExpectedAssignmentId");
		FluentActions.Invoking(() => PortableTaxonomyArchive.DeserializeStrict<HistoricalOperationItemEntity>(node.ToJsonString()))
			.Should().Throw<InvalidDataException>().WithMessage("*Missing JSON field*ExpectedAssignmentId");
		PortableBackupFormat.RequiredTablesFor(12).Should().Contain("historical_operation_archive");
		PortableBackupFormat.RequiredTablesFor(11).Should().NotContain("historical_operation_archive");
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await using var command = sqlite.CreateCommand();
		command.CommandText = "CREATE TABLE taxonomy_archive(kind TEXT,id TEXT,payload TEXT); CREATE TABLE category_mapping_archive(kind TEXT,id TEXT,payload TEXT);";
		await command.ExecuteNonQueryAsync();
		Func<Task> read = () => PortableTaxonomyArchive.ReadAsync(sqlite, CancellationToken.None, 12);
		await read.Should().ThrowAsync<SqliteException>().WithMessage("*historical_operation_archive*");
	}
}
