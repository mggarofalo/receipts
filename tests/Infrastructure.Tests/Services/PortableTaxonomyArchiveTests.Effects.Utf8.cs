using System.Text;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	[Theory]
	[InlineData("kind", false)]
	[InlineData("id", false)]
	[InlineData("payload", false)]
	[InlineData("kind", true)]
	[InlineData("id", true)]
	[InlineData("payload", true)]
	public async Task Effects_CandidateReaderRejectsInvalidUtf8OrBlobStorage(string field, bool blob)
	{
		var (history, evidence) = EffectState();
		var row = PortableTaxonomyArchive.WriteEffectEvidence(evidence)[0];
		await using var db = new SqliteConnection("Data Source=:memory:");
		await db.OpenAsync();
		await using var cmd = db.CreateCommand();
		cmd.CommandText = "CREATE TABLE organization_effect_archive(kind TEXT,id TEXT,payload TEXT)";
		await cmd.ExecuteNonQueryAsync();
		string Cell(string name) => name == field ? (blob ? "$bytes" : "CAST($bytes AS TEXT)") : "$" + name;
		cmd.CommandText = $"INSERT INTO organization_effect_archive VALUES({Cell("kind")},{Cell("id")},{Cell("payload")})";
		cmd.Parameters.AddWithValue("$kind", row.Kind);
		cmd.Parameters.AddWithValue("$id", row.Id);
		cmd.Parameters.AddWithValue("$payload", row.Payload);
		byte[] bytes = Encoding.UTF8.GetBytes(field switch { "kind" => row.Kind, "id" => row.Id, _ => row.Payload });
		if (!blob) { bytes[^1] = 0xFF; }
		cmd.Parameters.AddWithValue("$bytes", bytes);
		await cmd.ExecuteNonQueryAsync();
		Func<Task> read = () => PortableTaxonomyArchive.ReadEffectsCandidateAsync(db, history, CancellationToken.None);
		await read.Should().ThrowAsync<InvalidDataException>().WithMessage(blob ? "*storage class*" : "*UTF-8*");
	}
}
