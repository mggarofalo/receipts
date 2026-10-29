using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	[Fact]
	public void CandidateDestination_LegacyOmissionRetainsMappingEvidenceForAutomaticHistory()
	{
		var (basis, header, item) = AutomaticState();
		var existing = basis with { IncludesHistorical = true, HistoricalOperations = [header], HistoricalItems = [item] };
		// v10 has neither mapping nor historical tables; restoration retains both.
		var legacy = basis with { IncludesMappings = false, MappingRevisions = [], MappingHeads = [], IncludesHistorical = false };
		PortableTaxonomyArchive.ValidateCandidateDestination(existing, legacy);
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateDestination(existing, legacy))
				.Should().Throw<InvalidDataException>().WithMessage("*cannot be restored yet*");

		// A format that does contain mappings must still preserve all existing evidence.
		var missingMappings = legacy with { IncludesMappings = true };
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateCandidateDestination(existing, missingMappings))
				.Should().Throw<InvalidDataException>();
	}

	[Fact]
	public async Task CandidateReader_ValidatesCompleteSqliteArchive()
	{
		var (basis, header, item) = AutomaticState();
		var state = basis with { IncludesHistorical = true, HistoricalOperations = [header], HistoricalItems = [item] };
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await using var command = sqlite.CreateCommand();
		command.CommandText = """
                        CREATE TABLE taxonomy_archive(kind TEXT,id TEXT,payload TEXT);
                        CREATE TABLE category_mapping_archive(kind TEXT,id TEXT,payload TEXT);
                        CREATE TABLE historical_operation_archive(kind TEXT,id TEXT,payload TEXT);
                        CREATE TABLE source_revision_archive(kind TEXT,id TEXT,revision TEXT);
                        CREATE TABLE receipts(id TEXT); CREATE TABLE receipt_items(id TEXT);
                        """;
		await command.ExecuteNonQueryAsync();
		async Task Write<T>(string table, string kind, T row, object key)
		{
			command.CommandText = $"INSERT INTO {table} VALUES($kind,$id,$payload)";
			command.Parameters.Clear();
			command.Parameters.AddWithValue("$kind", kind);
			command.Parameters.AddWithValue("$id", System.Text.Json.JsonSerializer.Serialize(key));
			command.Parameters.AddWithValue("$payload", System.Text.Json.JsonSerializer.Serialize(row));
			await command.ExecuteNonQueryAsync();
		}
		foreach (var row in state.Identities) { await Write("taxonomy_archive", "identity", row, row.Id); }
		foreach (var row in state.Heads) { await Write("taxonomy_archive", "head", row, row.Id); }
		foreach (var row in state.Revisions) { await Write("taxonomy_archive", "revision", row, row.Id); }
		foreach (var row in state.Nodes) { await Write("taxonomy_archive", "node", row, new { row.RevisionId, row.CategoryId }); }
		foreach (var row in state.Operations) { await Write("taxonomy_archive", "operation", row, new { row.Actor, row.OperationId }); }
		foreach (var row in state.MappingRevisions) { await Write("category_mapping_archive", "revision", row, row.Id); }
		foreach (var row in state.MappingHeads) { await Write("category_mapping_archive", "head", row, row.SourceFingerprint); }
		await PortableTaxonomyArchive.WriteHistoricalCandidateAsync(sqlite, state, CancellationToken.None);

		var read = await PortableTaxonomyArchive.ReadCandidateAsync(sqlite, CancellationToken.None);
		read.HistoricalItems.Should().BeEquivalentTo(state.HistoricalItems);
		read.MappingRevisions.Should().BeEquivalentTo(state.MappingRevisions);
		Func<Task> legacy = () => PortableTaxonomyArchive.ReadAsync(sqlite, CancellationToken.None, 13);
		await legacy.Should().ThrowAsync<InvalidDataException>();
		command.Parameters.Clear();
		command.CommandText = "UPDATE taxonomy_archive SET payload=json_set(payload,'$.RevisionId',$revision) WHERE kind='head'";
		command.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString());
		await command.ExecuteNonQueryAsync();
		Func<Task> invalid = () => PortableTaxonomyArchive.ReadCandidateAsync(sqlite, CancellationToken.None);
		await invalid.Should().ThrowAsync<InvalidDataException>().WithMessage("*head*");
	}

	[Fact]
	public void CandidateDestination_ValidatesWholeArchiveAndRejectsDivergence()
	{
		var (basis, header, item) = AutomaticState();
		var incoming = basis with { IncludesHistorical = true, HistoricalOperations = [header], HistoricalItems = [item] };
		var existing = basis with { IncludesHistorical = true, HistoricalOperations = [], HistoricalItems = [] };
		PortableTaxonomyArchive.ValidateCandidate(incoming);
		PortableTaxonomyArchive.ValidateCandidateDestination(existing, incoming);
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateDestination(existing, incoming))
				.Should().Throw<InvalidDataException>();

		// A valid automatic record must not bypass unrelated taxonomy checks.
		incoming.Heads[0].RevisionId = Guid.NewGuid();
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateCandidate(incoming))
				.Should().Throw<InvalidDataException>().WithMessage("*head*");
	}

	[Fact]
	public void CandidateDestination_RejectsSharedHistoricalProgressChanges()
	{
		var (basis, header, item) = AutomaticState();
		var incoming = basis with { IncludesHistorical = true, HistoricalOperations = [header], HistoricalItems = [item] };
		var different = new Infrastructure.Entities.Taxonomy.HistoricalOperationEntity
		{
			Id = header.Id,
			Kind = header.Kind,
			ApprovedMappingRevisionId = header.ApprovedMappingRevisionId,
			Actor = header.Actor,
			RequestHash = header.RequestHash,
			SelectionJson = header.SelectionJson,
			TaxonomyRevisionId = header.TaxonomyRevisionId,
			CapturedAt = header.CapturedAt,
			Reason = header.Reason,
			State = Application.Taxonomy.HistoricalOperationState.Cancelled
		};
		var existing = basis with { IncludesHistorical = true, HistoricalOperations = [different], HistoricalItems = [item] };
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateCandidateDestination(existing, incoming))
				.Should().Throw<InvalidDataException>().WithMessage("*destination progress or intent*");
	}
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
