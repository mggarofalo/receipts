using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	[Fact]
	public void LegacyDestination_OmissionRetainsMappingEvidenceForAutomaticHistory()
	{
		var (basis, header, item) = AutomaticState();
		CompleteWithoutEffect(header, item);
		var existing = basis with { IncludesHistorical = true, HistoricalOperations = [header], HistoricalItems = [item] };
		// v10 has neither mapping nor historical tables; restoration retains both.
		var legacy = basis with { IncludesMappings = false, MappingRevisions = [], MappingHeads = [], IncludesHistorical = false };
		PortableTaxonomyArchive.ValidateCandidateDestination(existing, legacy);
		PortableTaxonomyArchive.ValidateDestination(existing, legacy);

		// v13 carries mappings and manual history; omitted automatic history is retained.
		var v13 = basis with { IncludesHistorical = true };
		PortableTaxonomyArchive.ValidateDestination(existing, v13);

		// A format that does contain mappings must still preserve all existing evidence.
		var missingMappings = legacy with { IncludesMappings = true };
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateCandidateDestination(existing, missingMappings))
				.Should().Throw<InvalidDataException>();
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateDestination(existing, missingMappings))
						.Should().Throw<InvalidDataException>();

		// Retaining automatic evidence still requires its complete mapping chain.
		var inconsistentV13 = v13 with { MappingRevisions = [], MappingHeads = [] };
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateDestination(existing, inconsistentV13))
						.Should().Throw<InvalidDataException>();
	}

	[Fact]
	public async Task CandidateReader_ValidatesCompleteSqliteArchive()
	{
		var (basis, header, item) = AutomaticState();
		var protection = DispositionState();
		// Source fields have no service-side size limit; candidate backup must preserve them.
		var longDescription = new string('x', 128 * 1024 + 1);
		var disposition = protection.ReviewDispositions.Single();
		disposition.RawDescription = longDescription;
		disposition.SourceHash = HistoricalReclassificationService.DispositionSourceHash(new(disposition.Store, longDescription, disposition.ItemCode));
		foreach (var operation in protection.ReviewDispositionOperations)
		{ operation.RawDescription = longDescription; operation.SourceHash = disposition.SourceHash; }
		var state = basis with { IncludesHistorical = true, HistoricalOperations = [header], HistoricalItems = [item], IncludesReviewDispositions = true, ReviewDispositions = protection.ReviewDispositions, ReviewDispositionOperations = protection.ReviewDispositionOperations };
		await using var sqlite = new SqliteConnection("Data Source=:memory:");
		await sqlite.OpenAsync();
		await using var command = sqlite.CreateCommand();
		command.CommandText = """
                        CREATE TABLE taxonomy_archive(kind TEXT,id TEXT,payload TEXT);
                        CREATE TABLE category_mapping_archive(kind TEXT,id TEXT,payload TEXT);
                        CREATE TABLE historical_operation_archive(kind TEXT,id TEXT,payload TEXT);
                        CREATE TABLE source_revision_archive(kind TEXT,id TEXT,revision TEXT);
                        CREATE TABLE receipts(id TEXT, location TEXT); CREATE TABLE receipt_items(id TEXT, receipt_id TEXT, description TEXT, receipt_item_code TEXT);
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
		await PortableTaxonomyArchive.WriteReviewDispositionsAsync(sqlite, state, CancellationToken.None);
		await PortableTaxonomyArchive.WriteReviewPreferencesAsync(sqlite, state, CancellationToken.None);
		command.Parameters.Clear();
		command.CommandText = """
			INSERT INTO receipts VALUES($receipt,$store);
			INSERT INTO receipt_items VALUES($item,$receipt,$description,$code);
			INSERT INTO source_revision_archive VALUES('Receipts',$receipt,$receiptRevision);
			INSERT INTO source_revision_archive VALUES('ReceiptItems',$item,$itemRevision);
			""";
		command.Parameters.AddWithValue("$receipt", item.ReceiptId.ToString());
		command.Parameters.AddWithValue("$item", item.ItemId.ToString());
		command.Parameters.AddWithValue("$store", item.Store);
		command.Parameters.AddWithValue("$description", item.RawDescription);
		command.Parameters.AddWithValue("$code", (object?)item.ItemCode ?? DBNull.Value);
		command.Parameters.AddWithValue("$receiptRevision", item.ReceiptRevision.ToString());
		command.Parameters.AddWithValue("$itemRevision", item.ItemRevision.ToString());
		await command.ExecuteNonQueryAsync();

		var read = await PortableTaxonomyArchive.ReadCandidateAsync(sqlite, CancellationToken.None);
		read.HistoricalItems.Should().BeEquivalentTo(state.HistoricalItems);
		read.ReviewDispositions.Should().BeEquivalentTo(state.ReviewDispositions);
		read.ReviewDispositionOperations.Should().BeEquivalentTo(state.ReviewDispositionOperations);
		command.CommandText = "UPDATE review_disposition_archive SET payload=json_remove(payload, '$.SourceHash') WHERE kind='disposition'";
		await command.ExecuteNonQueryAsync();
		Func<Task> malformedDisposition = () => PortableTaxonomyArchive.ReadCandidateAsync(sqlite, CancellationToken.None);
		await malformedDisposition.Should().ThrowAsync<InvalidDataException>();
		command.CommandText = "UPDATE review_disposition_archive SET payload=$payload WHERE kind='disposition'";
		command.Parameters.AddWithValue("$payload", System.Text.Json.JsonSerializer.Serialize(state.ReviewDispositions.Single()));
		await command.ExecuteNonQueryAsync();
		read.MappingRevisions.Should().BeEquivalentTo(state.MappingRevisions);
		command.Parameters.Clear();
		command.CommandText = "UPDATE receipt_items SET description='different'";
		await command.ExecuteNonQueryAsync();
		Func<Task> badSource = () => PortableTaxonomyArchive.ReadCandidateAsync(sqlite, CancellationToken.None);
		await badSource.Should().ThrowAsync<InvalidDataException>().WithMessage("*source differs*");
		// A later source edit with a changed revision is valid historical evidence.
		command.CommandText = "UPDATE source_revision_archive SET revision=$revision WHERE kind='ReceiptItems'";
		command.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString());
		await command.ExecuteNonQueryAsync();
		await PortableTaxonomyArchive.ReadCandidateAsync(sqlite, CancellationToken.None);
		// Each source component is independently bound to its own revision.
		command.Parameters.Clear();
		command.CommandText = "UPDATE receipts SET location='different'";
		await command.ExecuteNonQueryAsync();
		await badSource.Should().ThrowAsync<InvalidDataException>().WithMessage("*source differs*");
		command.CommandText = "UPDATE receipts SET location=$store; UPDATE source_revision_archive SET revision=$originalItem WHERE kind='ReceiptItems'; UPDATE source_revision_archive SET revision=$newReceipt WHERE kind='Receipts'";
		command.Parameters.AddWithValue("$store", item.Store);
		command.Parameters.AddWithValue("$originalItem", item.ItemRevision.ToString());
		command.Parameters.AddWithValue("$newReceipt", Guid.NewGuid().ToString());
		await command.ExecuteNonQueryAsync();
		await badSource.Should().ThrowAsync<InvalidDataException>().WithMessage("*source differs*");
		command.Parameters.Clear();
		command.CommandText = "UPDATE source_revision_archive SET revision=$newItem WHERE kind='ReceiptItems'; UPDATE source_revision_archive SET revision=$originalReceipt WHERE kind='Receipts'";
		command.Parameters.AddWithValue("$newItem", Guid.NewGuid().ToString());
		command.Parameters.AddWithValue("$originalReceipt", item.ReceiptRevision.ToString());
		await command.ExecuteNonQueryAsync();
		command.Parameters.Clear();
		command.CommandText = "UPDATE receipt_items SET receipt_id=$other";
		command.Parameters.AddWithValue("$other", Guid.NewGuid().ToString());
		await command.ExecuteNonQueryAsync();
		Func<Task> badReceipt = () => PortableTaxonomyArchive.ReadCandidateAsync(sqlite, CancellationToken.None);
		await badReceipt.Should().ThrowAsync<InvalidDataException>().WithMessage("*missing receipt*");
		command.CommandText = "INSERT INTO receipts VALUES($other,'other'); INSERT INTO source_revision_archive VALUES('Receipts',$other,$otherRevision)";
		command.Parameters.AddWithValue("$otherRevision", Guid.NewGuid().ToString());
		await command.ExecuteNonQueryAsync();
		// A move after capture is legitimate once the item revision changes.
		await PortableTaxonomyArchive.ReadCandidateAsync(sqlite, CancellationToken.None);
		command.CommandText = "UPDATE source_revision_archive SET revision=$original WHERE kind='ReceiptItems'";
		command.Parameters.AddWithValue("$original", item.ItemRevision.ToString());
		await command.ExecuteNonQueryAsync();
		await badReceipt.Should().ThrowAsync<InvalidDataException>().WithMessage("*different business receipt*unchanged item revision*");
		command.Parameters.Clear();
		command.CommandText = "UPDATE receipt_items SET receipt_id=$receipt, description=$description";
		command.Parameters.AddWithValue("$description", item.RawDescription);
		command.Parameters.AddWithValue("$receipt", item.ReceiptId.ToString());
		await command.ExecuteNonQueryAsync();
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
		CompleteWithoutEffect(header, item);
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
		CompleteWithoutEffect(header, item);
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
			Reason = "Different intent",
			State = HistoricalOperationState.Completed
		};
		var existing = basis with { IncludesHistorical = true, HistoricalOperations = [different], HistoricalItems = [item] };
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateCandidateDestination(existing, incoming))
				.Should().Throw<InvalidDataException>().WithMessage("*destination progress or intent*");
	}

	[Fact]
	public void CandidateDestination_AbandonsPendingAutomaticWork()
	{
		var (basis, header, item) = AutomaticState();
		var pending = basis with { IncludesHistorical = true, HistoricalOperations = [header], HistoricalItems = [item] };
		var empty = basis with { IncludesHistorical = true, HistoricalOperations = [], HistoricalItems = [] };
		PortableTaxonomyArchive.ValidateCandidateDestination(empty, pending);
		header.State.Should().Be(HistoricalOperationState.Cancelled);
		item.State.Should().Be(HistoricalItemState.Pending);
		PortableTaxonomyArchive.ValidateCandidateDestination(pending, empty);
	}

	private static void CompleteWithoutEffect(Infrastructure.Entities.Taxonomy.HistoricalOperationEntity header,
			Infrastructure.Entities.Taxonomy.HistoricalOperationItemEntity item)
	{
		header.State = HistoricalOperationState.Completed;
		item.State = HistoricalItemState.Skipped;
		item.Reason = "No change";
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
