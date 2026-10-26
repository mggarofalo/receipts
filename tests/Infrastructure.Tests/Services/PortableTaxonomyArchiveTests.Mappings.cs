using System.Security.Cryptography;
using System.Text.Json;
using Application.Normalization;
using Application.Taxonomy;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	internal static PortableTaxonomyArchive.State MappingState()
	{
		var state = EmptyState() with { IncludesMappings = true };
		Guid category = Guid.NewGuid();
		var taxonomy = new TaxonomyRevisionEntity
		{
			Id = Guid.NewGuid(),
			Sequence = 1,
			PreviousRevisionId = state.Revisions[0].Id,
			Actor = "admin",
			OperationId = Guid.NewGuid(),
			Reason = "create",
			RecordedAt = state.Revisions[0].RecordedAt.AddSeconds(1)
		};
		state.Identities.Add(new() { Id = category }); state.Revisions.Add(taxonomy);
		state.Heads[0].RevisionId = taxonomy.Id;
		state.Operations.Add(new()
		{
			Actor = taxonomy.Actor,
			OperationId = taxonomy.OperationId,
			RevisionId = taxonomy.Id,
			Reason = taxonomy.Reason,
			RecordedAt = taxonomy.RecordedAt,
			RequestHash = new string('A', 64)
		});
		state.Nodes.Add(new() { RevisionId = taxonomy.Id, CategoryId = category, Name = "Food" });
		NormalizationSourceKey source = new("Store", "Milk", null);
		Guid? prior = null;
		for (int i = 0; i < 3; i++)
		{
			var command = new CategoryMappingCommand(source, NormalizationCatalogPolicy.Hash(source), prior,
				taxonomy.Id, i == 1 ? null : category, Guid.NewGuid(), "reviewed");
			var row = new CategoryMappingRevisionEntity
			{
				Id = Guid.NewGuid(),
				PreviousRevisionId = prior,
				SourceFingerprint = command.SourceFingerprint,
				Location = source.Location,
				Description = source.Description,
				CategoryId = command.CategoryId,
				TaxonomyRevisionId = taxonomy.Id,
				SemanticFingerprint = command.CategoryId == null ? null : CategoryMappingPolicy.SemanticFingerprint(
					new[] { new TaxonomyNode(category, null, "Food", null, false) }, category),
				Actor = "reviewer",
				OperationId = command.OperationId,
				Reason = command.Reason,
				RequestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command))),
				RecordedAt = taxonomy.RecordedAt.AddSeconds(i + 1)
			};
			state.MappingRevisions.Add(row); prior = row.Id;
		}
		state.MappingHeads.Add(new() { SourceFingerprint = NormalizationCatalogPolicy.Hash(source), RevisionId = prior!.Value });
		return state;
	}

	[Fact]
	public async Task MappingArchive_RoundTripsApprovalRevokeRenewAndRestoresReplayWithoutHistoryMutation()
	{
		var state = MappingState();
		var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
		await using var db = new ApplicationDbContext(options);
		await db.Database.EnsureCreatedAsync();
		await PortableTaxonomyArchive.RestoreAsync(db, state, [], CancellationToken.None);
		await using var sqlite = new SqliteConnection("Data Source=:memory:"); await sqlite.OpenAsync();
		await PortableTaxonomyArchive.ExportAsync(db, sqlite, CancellationToken.None);
		var captured = await PortableTaxonomyArchive.ReadAsync(sqlite, CancellationToken.None);
		captured.MappingRevisions.Should().BeEquivalentTo(state.MappingRevisions);
		captured.MappingHeads.Should().BeEquivalentTo(state.MappingHeads);
		await PortableTaxonomyArchive.PreflightDestinationAsync(db, captured, CancellationToken.None);
		await PortableTaxonomyArchive.RestoreAsync(db, captured, [], CancellationToken.None);
		(await db.CategoryMappingRevisions.CountAsync()).Should().Be(3);
		var old = await PortableTaxonomyArchive.ReadAsync(sqlite, CancellationToken.None, 10);
		old.IncludesMappings.Should().BeFalse(); old.MappingRevisions.Should().BeEmpty();
		await PortableTaxonomyArchive.PreflightDestinationAsync(db, old, CancellationToken.None);
		await PortableTaxonomyArchive.RestoreAsync(db, old, [], CancellationToken.None);
		(await db.CategoryMappingRevisions.CountAsync()).Should().Be(3);
	}

	[Theory]
	[InlineData("cycle")]
	[InlineData("missing predecessor")]
	[InlineData("head")]
	[InlineData("operation")]
	[InlineData("source")]
	[InlineData("semantics")]
	[InlineData("taxonomy")]
	[InlineData("request")]
	public void MappingValidation_RejectsMalformedEvidence(string corruption)
	{
		var state = MappingState(); var row = state.MappingRevisions[0];
		switch (corruption)
		{
			case "cycle": row.PreviousRevisionId = state.MappingRevisions[^1].Id; break;
			case "missing predecessor": row.PreviousRevisionId = Guid.NewGuid(); break;
			case "head": state.MappingHeads[0].RevisionId = row.Id; break;
			case "operation": state.MappingRevisions[1].OperationId = row.OperationId; break;
			case "source": row.Description = "Changed"; break;
			case "semantics": row.SemanticFingerprint = new string('A', 64); break;
			case "taxonomy": row.TaxonomyRevisionId = Guid.NewGuid(); break;
			case "request": row.RequestHash = new string('A', 64); break;
		}
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>();
	}

	[Fact]
	public void MappingDestination_RejectsRewindAndConflictingEvidence()
	{
		var existing = MappingState();
		var incoming = existing with
		{
			MappingRevisions = [.. existing.MappingRevisions.Take(2)],
			MappingHeads = [new() { SourceFingerprint = existing.MappingHeads[0].SourceFingerprint, RevisionId = existing.MappingRevisions[1].Id }]
		};
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateDestination(existing, incoming)).Should().Throw<InvalidDataException>();
		incoming = MappingState();
		incoming.Identities.Clear(); incoming.Identities.AddRange(existing.Identities);
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateDestination(existing, incoming)).Should().Throw<InvalidDataException>();
	}
}
