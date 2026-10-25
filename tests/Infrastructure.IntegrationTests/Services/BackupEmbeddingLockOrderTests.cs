using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class BackupEmbeddingLockOrderTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Fact]
	public async Task Import_WaitsForMixedEmbeddingBatch_WithoutHoldingItsLaterSources()
	{
		Guid canonicalId = Guid.NewGuid();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		ItemTemplateEntity template = ItemTemplateEntityGenerator.Generate();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Receipts.Add(receipt);
			seed.ReceiptItems.Add(item);
			seed.ItemTemplates.Add(template);
			seed.NormalizedDescriptions.Add(new()
			{
				Id = canonicalId,
				CanonicalName = "Mixed embedding source",
				CreatedAt = DateTimeOffset.UtcNow
			});
			await seed.SaveChangesAsync();
		}

		Factory factory = new(fixture);
		string path = await new BackupService(factory, NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
		try
		{
			await using NpgsqlConnection worker = new(fixture.ConnectionString);
			await worker.OpenAsync(timeout.Token);
			await using NpgsqlTransaction transaction = await worker.BeginTransactionAsync(timeout.Token);
			// Reproduce the real embedding write phase: canonical, template, then receipt item.
			await using NpgsqlCommand canonical = new("SELECT \"Id\" FROM matching.\"NormalizedDescriptions\" WHERE \"Id\"=@id FOR UPDATE", worker, transaction);
			canonical.Parameters.AddWithValue("id", canonicalId);
			await canonical.ExecuteNonQueryAsync(timeout.Token);

			await using FileStream input = File.OpenRead(path);
			Task import = new BackupImportService(factory, NullLogger<BackupImportService>.Instance)
				.ImportFromSqliteAsync(input, timeout.Token);
			await using NpgsqlCommand waiting = new("""
                SELECT EXISTS (SELECT 1 FROM pg_locks
                WHERE relation = 'matching."NormalizedDescriptions"'::regclass
                AND mode = 'ExclusiveLock' AND NOT granted
                AND pg_backend_pid() = ANY(pg_blocking_pids(pid)))
                """, worker, transaction);
			bool observedWait = false;
			while (!import.IsCompleted && !observedWait)
			{
				observedWait = (bool)(await waiting.ExecuteScalarAsync(timeout.Token))!;
				if (!observedWait) { await Task.Delay(20, timeout.Token); }
			}
			observedWait.Should().BeTrue("restore must wait for the in-flight canonical source lock");

			await using NpgsqlCommand laterSources = new("""
                SET LOCAL lock_timeout = '3s';
                SELECT "Id" FROM library."ItemTemplates" WHERE "Id"=@template FOR UPDATE;
                SELECT "Id" FROM receipts."ReceiptItems" WHERE "Id"=@item FOR UPDATE;
                UPDATE matching."NormalizedDescriptions" SET "EmbeddingModelVersion"='completed-worker' WHERE "Id"=@canonical;
                """, worker, transaction);
			laterSources.Parameters.AddWithValue("template", template.Id);
			laterSources.Parameters.AddWithValue("item", item.Id);
			laterSources.Parameters.AddWithValue("canonical", canonicalId);
			await laterSources.ExecuteNonQueryAsync(timeout.Token);
			canonical.CommandText = "SELECT \"EmbeddingModelVersion\" FROM matching.\"NormalizedDescriptions\" WHERE \"Id\"=@id";
			(await canonical.ExecuteScalarAsync(timeout.Token)).Should().Be("completed-worker");
			await transaction.CommitAsync(timeout.Token);
			await import;

			await using ApplicationDbContext verify = fixture.CreateDbContext();
			(await verify.Receipts.SingleAsync(row => row.Id == receipt.Id, timeout.Token)).Location.Should().Be(receipt.Location);
			(await verify.NormalizedDescriptions.SingleAsync(row => row.Id == canonicalId, timeout.Token))
				.EmbeddingModelVersion.Should().BeNull("portable restore invalidates rebuildable vectors after the worker commits");
		}
		finally { File.Delete(path); }
	}

	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
