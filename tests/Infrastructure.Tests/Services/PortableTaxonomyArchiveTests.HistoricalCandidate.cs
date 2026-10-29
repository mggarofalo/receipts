using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	[Fact]
	public async Task HistoricalCandidate_RoundTripsMixedWireAndPublicReaderRejectsIt()
	{
		var (basis, header, item) = AutomaticState();
		var manual = HistoricalState();
		var mixed = basis with
		{
			IncludesHistorical = true,
			HistoricalOperations = [manual.HistoricalOperations[0], header],
			HistoricalItems = [manual.HistoricalItems[0], item]
		};
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await using var command = sqlite.CreateCommand();
		command.CommandText = "CREATE TABLE historical_operation_archive(kind TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(kind,id)); CREATE TABLE taxonomy_archive(kind TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL); CREATE TABLE category_mapping_archive(kind TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL); CREATE TABLE source_revision_archive(kind TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL);";
		await command.ExecuteNonQueryAsync();
		await PortableTaxonomyArchive.WriteHistoricalCandidateAsync(sqlite, mixed, CancellationToken.None);
		var restored = await PortableTaxonomyArchive.ReadHistoricalCandidateAsync(sqlite, basis, CancellationToken.None);
		restored.HistoricalOperations.Should().BeEquivalentTo(mixed.HistoricalOperations);
		restored.HistoricalItems.Should().BeEquivalentTo(mixed.HistoricalItems);
		Func<Task> legacyRead = () => PortableTaxonomyArchive.ReadAsync(sqlite, CancellationToken.None, 13);
		await legacyRead.Should().ThrowAsync<InvalidDataException>().WithMessage("*Unknown historical archive kind*");
		Action legacyValidate = () => PortableTaxonomyArchive.Validate(mixed);
		legacyValidate.Should().Throw<InvalidDataException>().WithMessage("*cannot be restored yet*");
	}

	[Fact]
	public void HistoricalCandidate_RejectsOperationCollisionAcrossRetainedDestinationUnion()
	{
		var (basis, header, item) = AutomaticState();
		var manual = HistoricalState();
		var incoming = basis with { IncludesHistorical = true, HistoricalOperations = [header], HistoricalItems = [item] };
		var existing = basis with { IncludesHistorical = true, HistoricalOperations = manual.HistoricalOperations, HistoricalItems = manual.HistoricalItems };
		PortableTaxonomyArchive.ValidateHistoricalCandidateDestination(existing, incoming);
		item.UndoOperationId = manual.HistoricalItems[0].ApplyOperationId;
		Action validate = () => PortableTaxonomyArchive.ValidateHistoricalCandidateDestination(existing, incoming);
		validate.Should().Throw<InvalidDataException>().WithMessage("*duplicate operation ID*");
	}

	[Fact]
	public async Task HistoricalCandidate_RejectsAutomaticItemUsingManualWireKind()
	{
		var (basis, header, item) = AutomaticState();
		var state = basis with { HistoricalOperations = [header], HistoricalItems = [item] };
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await using var command = sqlite.CreateCommand();
		command.CommandText = "CREATE TABLE historical_operation_archive(kind TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(kind,id))";
		await command.ExecuteNonQueryAsync();
		await PortableTaxonomyArchive.WriteHistoricalCandidateAsync(sqlite, state, CancellationToken.None);
		command.CommandText = "UPDATE historical_operation_archive SET kind = 'item' WHERE kind = 'automatic_item'";
		await command.ExecuteNonQueryAsync();
		Func<Task> read = () => PortableTaxonomyArchive.ReadHistoricalCandidateAsync(sqlite, basis, CancellationToken.None);
		await read.Should().ThrowAsync<InvalidDataException>();
	}
	[Fact]
	public async Task HistoricalCandidate_RejectsOversizedPayloadBeforeParsing()
	{
		var (basis, _, _) = AutomaticState();
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await using var command = sqlite.CreateCommand();
		command.CommandText = "CREATE TABLE historical_operation_archive(kind TEXT,id TEXT,payload TEXT); INSERT INTO historical_operation_archive VALUES('automatic_operation','id',$payload)";
		command.Parameters.AddWithValue("$payload", new string('x', 128 * 1024 + 1));
		await command.ExecuteNonQueryAsync();
		Func<Task> read = () => PortableTaxonomyArchive.ReadHistoricalCandidateAsync(sqlite, basis, CancellationToken.None);
		await read.Should().ThrowAsync<InvalidDataException>().WithMessage("*bounded payload size*");
	}

	[Fact]
	public async Task HistoricalCandidate_RejectsOversizedAutomaticManifestWhileReading()
	{
		var (basis, header, item) = AutomaticState();
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await using var command = sqlite.CreateCommand();
		command.CommandText = "CREATE TABLE historical_operation_archive(kind TEXT,id TEXT,payload TEXT)";
		await command.ExecuteNonQueryAsync();
		command.CommandText = "INSERT INTO historical_operation_archive VALUES('automatic_item',$id,$payload)";
		var id = command.Parameters.Add("$id", SqliteType.Text);
		var payload = command.Parameters.Add("$payload", SqliteType.Text);
		for (int n = 0; n < 101; n++)
		{
			item.ItemId = Guid.NewGuid();
			id.Value = System.Text.Json.JsonSerializer.Serialize(new { item.OperationId, item.ItemId });
			payload.Value = System.Text.Json.JsonSerializer.Serialize(new PortableAutomaticHistoricalV14.ItemWire(item, item.ApprovedMappingRevisionId));
			await command.ExecuteNonQueryAsync();
		}
		Func<Task> read = () => PortableTaxonomyArchive.ReadHistoricalCandidateAsync(sqlite, basis, CancellationToken.None);
		await read.Should().ThrowAsync<InvalidDataException>().WithMessage("*manifest exceeds its bounded size*");
	}
	[Theory]
	[InlineData("kind")]
	[InlineData("id")]
	[InlineData("payload")]
	public async Task HistoricalCandidate_RejectsOversizedFieldWithEmbeddedNul(string field)
	{
		var (basis, _, _) = AutomaticState();
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await using var command = sqlite.CreateCommand();
		command.CommandText = "CREATE TABLE historical_operation_archive(kind TEXT,id TEXT,payload TEXT); INSERT INTO historical_operation_archive VALUES('operation','id','{}')";
		await command.ExecuteNonQueryAsync();
		// Build the value inside SQLite so the embedded NUL reaches the stored TEXT.
		command.CommandText = $"UPDATE historical_operation_archive SET {field} = char(0) || replace(hex(zeroblob(196609)), '0', 'x')";
		await command.ExecuteNonQueryAsync();
		Func<Task> read = () => PortableTaxonomyArchive.ReadHistoricalCandidateAsync(sqlite, basis, CancellationToken.None);
		await read.Should().ThrowAsync<InvalidDataException>().WithMessage("*bounded payload size*");
	}
}
