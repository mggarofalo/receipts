using System.Text.Json.Nodes;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData("PendingReview")]
	[InlineData("Rejected")]
	[InlineData("wrong_scope")]
	public async Task InvalidCatalogArchive_RollsBackFactsTargetsAndMappingChanges(string corruption)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks");
		var ids = await ItemsAsync(Source, 1);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		var before = await FactsAsync(ids);
		var mapping = (await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Single();
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		try
		{
			await using (var archive = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
			{
				await archive.OpenAsync();
				await using var command = archive.CreateCommand();
				if (corruption == "wrong_scope")
				{
					command.CommandText = "SELECT payload FROM normalization_catalog WHERE kind='mapping'";
					var payload = JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!;
					var otherScope = Source with { Location = "Other store" };
					payload["Location"] = otherScope.Location;
					payload["SourceHash"] = NormalizationCatalogPolicy.Hash(otherScope);
					command.CommandText = "UPDATE normalization_catalog SET payload=$payload WHERE kind='mapping'";
					command.Parameters.AddWithValue("$payload", payload.ToJsonString());
				}
				else
				{
					command.CommandText = "UPDATE normalized_descriptions SET status=$status WHERE id=$id";
					command.Parameters.AddWithValue("$status", corruption);
					command.Parameters.AddWithValue("$id", target.Id.ToString());
				}
				(await command.ExecuteNonQueryAsync()).Should().Be(1);
				command.Parameters.Clear();
				// Keep scope otherwise valid; altering description-independent financial facts proves rollback too.
				command.CommandText = "UPDATE receipt_items SET quantity=99";
				(await command.ExecuteNonQueryAsync()).Should().Be(1);
			}
			await using var stream = File.OpenRead(path);
			Func<Task> import = () => new BackupImportService(new Factory(fixture), NullLogger<BackupImportService>.Instance)
				.ImportFromSqliteAsync(stream, CancellationToken.None);
			await import.Should().ThrowAsync<InvalidOperationException>();
			(await FactsAsync(ids)).Should().BeEquivalentTo(before, "all imported changes must roll back with invalid provenance");
			await using var verify = fixture.CreateDbContext();
			(await verify.NormalizedDescriptions.SingleAsync(x => x.Id == target.Id)).Status.Should().Be(NormalizedDescriptionStatus.Active);
			var durable = await verify.NormalizationSourceMappings.SingleAsync(x => x.Id == mapping.Id);
			durable.Location.Should().Be(Source.Location);
			durable.State.Should().Be("active");
			(await service.GetOperationAsync(Actor, preview.Id, CancellationToken.None))!.State.Should().Be("completed");
		}
		finally { File.Delete(path); }
	}
}

