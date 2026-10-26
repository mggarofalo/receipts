using System.Text.Json;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Configurations;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	[Fact]
	public void RequiredTables_V9RemainsCompatibleAndV10RequiresHistoryAndTrash()
	{
		PortableBackupFormat.RequiredTablesFor(9).Should().NotContain("taxonomy_archive");
		PortableBackupFormat.RequiredTablesFor(9).Should().NotContain("portable_deletion_state");
		PortableBackupFormat.RequiredTablesFor(10).Should().Contain(["taxonomy_archive", "portable_deletion_state"]);
		FluentActions.Invoking(() => PortableBackupFormat.RequiredTablesFor(13)).Should().Throw<InvalidOperationException>();
	}

	[Fact]
	public async Task Export_IncludesTrashDependencyClosureAndDeletionProvenance()
	{
		var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
		var factory = new Factory(options);
		Guid receiptId = Guid.NewGuid(), itemId = Guid.NewGuid(), categoryId = Guid.NewGuid();
		DateTimeOffset deleted = new(2026, 10, 25, 1, 2, 3, TimeSpan.Zero);
		await using (var source = new ApplicationDbContext(options))
		{
			await source.Database.EnsureCreatedAsync();
			source.Categories.Add(new CategoryEntity { Id = categoryId, Name = "Archived", DeletedAt = deleted, DeletedByUserId = "historical-user" });
			source.Receipts.Add(new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2026, 1, 1), DeletedAt = deleted });
			source.ReceiptItems.Add(new ReceiptItemEntity { Id = itemId, ReceiptId = receiptId, Description = "Original", Category = "Archived", DeletedAt = deleted, CascadeDeletedByParentId = receiptId });
			await source.SaveChangesAsync();
		}
		string path = await new BackupService(factory, NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		try
		{
			await using var sqlite = new SqliteConnection($"Data Source={path};Pooling=False");
			await sqlite.OpenAsync();
			foreach (string table in new[] { "receipts", "receipt_items", "categories" })
			{
				await using var count = sqlite.CreateCommand();
				count.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id=$id";
				count.Parameters.AddWithValue("$id", (table == "receipts" ? receiptId : table == "receipt_items" ? itemId : categoryId).ToString());
				Convert.ToInt64(await count.ExecuteScalarAsync()).Should().Be(1);
			}
			await using var evidence = sqlite.CreateCommand();
			evidence.CommandText = "SELECT payload FROM portable_deletion_state WHERE kind='receipt_items'";
			string payload = (string)(await evidence.ExecuteScalarAsync())!;
			payload.Should().Contain(receiptId.ToString()).And.Contain("2026-10-25T01:02:03");
			evidence.CommandText = "SELECT COUNT(*) FROM normalization_catalog WHERE kind='assignment'";
			Convert.ToInt64(await evidence.ExecuteScalarAsync()).Should().Be(1);
			var state = await PortableTaxonomyArchive.ReadAsync(sqlite, CancellationToken.None);
			var deletion = await PortableTaxonomyArchive.ReadDeletionStateAsync(sqlite, state, CancellationToken.None);
			deletion.Should().Contain(r => r.Id == itemId && r.CascadeDeletedByParentId == receiptId);
			state.Revisions.Should().ContainSingle(r => r.Id == TaxonomyConfiguration.GenesisId);
			evidence.CommandText = "SELECT payload FROM taxonomy_archive WHERE kind='revision'";
			((string)(await evidence.ExecuteScalarAsync())!).Should().NotContain("WriteTransaction");
			evidence.CommandText = "DELETE FROM portable_deletion_state WHERE kind='receipt_items'";
			await evidence.ExecuteNonQueryAsync();
			Func<Task> missingDeletion = () => PortableTaxonomyArchive.ReadDeletionStateAsync(sqlite, state, CancellationToken.None);
			await missingDeletion.Should().ThrowAsync<InvalidDataException>().WithMessage("*Missing*deletion*");
			evidence.CommandText = "UPDATE taxonomy_archive SET id='incorrect' WHERE kind='revision'";
			await evidence.ExecuteNonQueryAsync();
			Func<Task> wrongKey = () => PortableTaxonomyArchive.ReadAsync(sqlite, CancellationToken.None);
			await wrongKey.Should().ThrowAsync<InvalidDataException>().WithMessage("*key differs*");
			await evidence.DisposeAsync();
			await sqlite.CloseAsync();
			await using var input = File.OpenRead(path);
			var importer = new BackupImportService(new NeverFactory(), NullLogger<BackupImportService>.Instance);
			Func<Task> restore = () => importer.ImportFromSqliteAsync(input, CancellationToken.None);
			await restore.Should().ThrowAsync<InvalidOperationException>().WithMessage("*key differs*");
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public void Validate_RejectsRewindAndModifiedGenesisButAcceptsNoOpOperation()
	{
		var genesis = new TaxonomyRevisionEntity
		{
			Id = TaxonomyConfiguration.GenesisId,
			Sequence = 0,
			Actor = "system",
			OperationId = TaxonomyConfiguration.GenesisId,
			RecordedAt = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero),
			Reason = "Empty taxonomy foundation; legacy import is separate."
		};
		var state = new PortableTaxonomyArchive.State([], [new TaxonomyHeadEntity { Id = 1, RevisionId = genesis.Id }],
			[genesis], [], [new TaxonomyOperationEntity { Actor = "other", OperationId = Guid.NewGuid(), RevisionId = genesis.Id, RequestHash = new string('A', 64), Reason = "no-op", RecordedAt = genesis.RecordedAt }], [], [], [], [], [], []);
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().NotThrow();
		genesis.Reason = "modified";
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>();
		genesis.Reason = "Empty taxonomy foundation; legacy import is separate.";
		state.Revisions.Add(new TaxonomyRevisionEntity { Id = Guid.NewGuid(), Sequence = 1, PreviousRevisionId = genesis.Id, Actor = "actor", Reason = "change", OperationId = Guid.NewGuid(), RecordedAt = genesis.RecordedAt });
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>().WithMessage("*head*");
	}

	[Fact]
	public void DestinationPreflight_RejectsMissingOrChangedSealedRowsAndRewind()
	{
		var existing = EmptyState();
		var incoming = EmptyState();
		PortableTaxonomyArchive.ValidateDestination(existing, incoming);
		incoming.Revisions[0].Reason = "changed";
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateDestination(existing, incoming))
			.Should().Throw<InvalidDataException>();
		incoming = EmptyState();
		var identity = Guid.NewGuid();
		var revision = new TaxonomyRevisionEntity
		{
			Id = Guid.NewGuid(),
			Sequence = 1,
			PreviousRevisionId = existing.Revisions[0].Id,
			Actor = "admin",
			Reason = "create",
			OperationId = Guid.NewGuid(),
			RecordedAt = existing.Revisions[0].RecordedAt.AddSeconds(1),
			AffectedCount = 1
		};
		existing.Identities.Add(new() { Id = identity });
		existing.Revisions.Add(revision);
		existing.Heads[0].RevisionId = revision.Id;
		existing.Nodes.Add(new() { RevisionId = revision.Id, CategoryId = identity, Name = "Food" });
		existing.Operations.Add(new()
		{
			Actor = revision.Actor,
			OperationId = revision.OperationId,
			RequestHash = new string('A', 64),
			RevisionId = revision.Id,
			Reason = revision.Reason,
			RecordedAt = revision.RecordedAt,
			AffectedCount = 1
		});
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateDestination(existing, incoming))
			.Should().Throw<InvalidDataException>().WithMessage("*rewind*");
		incoming = EmptyState();
		incoming.Identities.AddRange(existing.Identities);
		incoming.Revisions.Add(revision);
		incoming.Heads[0].RevisionId = revision.Id;
		incoming.Operations.AddRange(existing.Operations);
		incoming.Nodes.Add(new() { RevisionId = revision.Id, CategoryId = identity, Name = "Changed" });
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateDestination(existing, incoming))
			.Should().Throw<InvalidDataException>().WithMessage("*immutable*");
		incoming.Nodes[0].Name = "Food";
		PortableTaxonomyArchive.ValidateDestination(existing, incoming);
	}

	[Fact]
	public void Validate_RejectsDuplicateCompoundSnapshotKey()
	{
		var state = EmptyState();
		var node = new TaxonomyNodeEntity { RevisionId = state.Revisions[0].Id, CategoryId = Guid.NewGuid(), Name = "x" };
		state.Nodes.Add(node); state.Nodes.Add(node);
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>()
			.WithMessage("*Duplicate*");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(9)]
	public void Validate_RecomputesMigrationPlanFromFrozenEvidence(int submicrosecondTicks)
	{
		var state = EmptyState();
		DateTimeOffset stored = state.Revisions[0].RecordedAt.AddSeconds(1);
		var plan = LegacyTaxonomyMigrationPlanner.Plan(new("dataset", Guid.NewGuid(),
			LegacyTaxonomyMigrationService.InitialOperationKind, "actor", stored.AddTicks(submicrosecondTicks), [], [], []));
		state.Revisions.Add(new()
		{
			Id = plan.RevisionId,
			Sequence = 1,
			PreviousRevisionId = state.Revisions[0].Id,
			Actor = plan.Actor,
			OperationId = plan.OperationId,
			RecordedAt = stored,
			Reason = "observed"
		});
		state.Heads[0].RevisionId = plan.RevisionId;
		var migration = new LegacyMigrationEntity
		{
			Actor = plan.Actor,
			OperationId = plan.OperationId,
			OperationKind = plan.OperationKind,
			SourceDataset = plan.SourceDataset,
			RequestHash = plan.RequestHash,
			RevisionId = plan.RevisionId,
			ObservedAt = stored,
			PlanJson = JsonSerializer.Serialize(plan)
		};
		state.Migrations.Add(migration);
		PortableTaxonomyArchive.Validate(state);
		migration.PlanJson.Should().Be(JsonSerializer.Serialize(plan));
		migration.ObservedAt = stored.AddTicks(1);
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>()
			.WithMessage("*Migration plan differs from metadata*");
		migration.ObservedAt = stored;
		migration.PlanJson = JsonSerializer.Serialize(plan with { ObservedAt = stored.AddTicks(submicrosecondTicks == 1 ? 2 : 1) });
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>()
			.WithMessage("*frozen source*");
		migration.PlanJson = JsonSerializer.Serialize(plan with { Counts = plan.Counts with { Items = 1 } });
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>()
			.WithMessage("*frozen source*");
	}

	[Theory]
	[InlineData("CategoryId")]
	[InlineData("PreviousAssignmentId")]
	public void StrictJson_RequiresNullableAssignmentFieldsExactlyOnce(string field)
	{
		string payload = JsonSerializer.Serialize(new PurchaseClassificationEntity());
		var json = System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject();
		json.Remove(field);
		FluentActions.Invoking(() => PortableTaxonomyArchive.DeserializeStrict<PurchaseClassificationEntity>(json.ToJsonString()))
			.Should().Throw<InvalidDataException>().WithMessage("*Missing JSON field*" + field);
		string duplicate = payload[..^1] + ",\"" + field + "\":null}";
		FluentActions.Invoking(() => PortableTaxonomyArchive.DeserializeStrict<PurchaseClassificationEntity>(duplicate))
			.Should().Throw<InvalidDataException>().WithMessage("*Duplicate JSON field*" + field);
	}

	[Theory]
	[InlineData("ParentId")]
	[InlineData("Archived")]
	public void StrictJson_RequiresNullableAndDefaultSnapshotFields(string field)
	{
		var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new TaxonomyNodeEntity()))!.AsObject();
		json.Remove(field);
		FluentActions.Invoking(() => PortableTaxonomyArchive.DeserializeStrict<TaxonomyNodeEntity>(json.ToJsonString()))
			.Should().Throw<InvalidDataException>().WithMessage("*Missing JSON field*" + field);
	}

	[Theory]
	[InlineData("DeletedAt")]
	[InlineData("DeletedByUserId")]
	[InlineData("DeletedByApiKeyId")]
	[InlineData("CascadeDeletedByParentId")]
	public void StrictJson_RequiresEveryDeletionField(string field)
	{
		var row = new PortableTaxonomyArchive.DeletionState("categories", Guid.NewGuid(), null, null, null, null);
		string payload = JsonSerializer.Serialize(row);
		var json = System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject();
		json.Remove(field);
		FluentActions.Invoking(() => PortableTaxonomyArchive.DeserializeStrict<PortableTaxonomyArchive.DeletionState>(json.ToJsonString()))
			.Should().Throw<InvalidDataException>().WithMessage("*Missing JSON field*" + field);
		FluentActions.Invoking(() => PortableTaxonomyArchive.DeserializeStrict<PortableTaxonomyArchive.DeletionState>(payload[..^1] + ",\"" + field + "\":null}"))
			.Should().Throw<InvalidDataException>().WithMessage("*Duplicate JSON field*" + field);
	}

	[Fact]
	public void StrictJson_RequiresNullableFieldsInNestedFrozenEvidence()
	{
		var evidence = new LegacyCategoryEvidence(Guid.NewGuid(), "Food", null, true, new());
		var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(evidence))!.AsObject();
		json["Deletion"]!.AsObject().Remove("DeletedAt");
		FluentActions.Invoking(() => PortableTaxonomyArchive.DeserializeStrict<LegacyCategoryEvidence>(json.ToJsonString()))
			.Should().Throw<InvalidDataException>().WithMessage("*Missing JSON field*DeletedAt");
	}

	private static PortableTaxonomyArchive.State EmptyState()
	{
		var genesis = new TaxonomyRevisionEntity
		{
			Id = TaxonomyConfiguration.GenesisId,
			Actor = "system",
			OperationId = TaxonomyConfiguration.GenesisId,
			RecordedAt = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero),
			Reason = "Empty taxonomy foundation; legacy import is separate."
		};
		return new([], [new() { Id = 1, RevisionId = genesis.Id }], [genesis], [], [], [], [], [], [], [], []);
	}

	private sealed class NeverFactory : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => throw new InvalidOperationException("Destination context must not be opened.");
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Destination context must not be opened.");
	}

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
	}
}
