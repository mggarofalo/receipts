using System.Text.Json;
using FluentAssertions;
using Infrastructure.Entities.Organization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class OrganizationDiscoveryTests
{
	[Fact]
	public async Task EmptyAndExactMultipleSourcePagesNeverStopDrainBeforeOtherSourcesAreAcknowledged()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			Guid receipt = Guid.NewGuid();
			await Seed(fixture, Enumerable.Range(0, 4).Select(_ => Item(receipt, Guid.NewGuid())).ToArray());
			Guid run = await Start(fixture); await Drain(fixture, run);
			await using var db = fixture.CreateDbContext();
			db.OrganizationDirtySources.AddRange(
				new OrganizationDirtySourceEntity { Id = Guid.Empty, Store = "Test store", Description = "No such purchase" },
				new OrganizationDirtySourceEntity { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Store = "Test store", Description = "Repeated purchase" },
				new OrganizationDirtySourceEntity { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), Store = "Other store", Description = "Repeated purchase" });
			await db.SaveChangesAsync();
			(await Store(fixture).DiscoverAsync(run, 2, default)).Should().BeGreaterThan(0, "acknowledging an empty source is progress while later sources remain");
			(await db.OrganizationDirtySources.CountAsync()).Should().Be(2);
			await Drain(fixture, run, 2);
			(await db.OrganizationDirtySources.CountAsync()).Should().Be(0, "exact-multiple sources require an empty terminal page and cannot strand the next source");
			(await Store(fixture).DiscoverAsync(run, 2, default)).Should().Be(0);
		}
		finally { await fixture.DisposeAsync(); }
	}

	[Theory]
	[InlineData("raw")]
	[InlineData("normalized")]
	public async Task IndexedExpansionRechecksExactCodeWhitespaceAndStoreAfterHashFiltering(string kind)
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			Guid receipt = Guid.NewGuid();
			var items = new[] { Item(receipt, Guid.NewGuid(), "Coffee"), Item(receipt, Guid.NewGuid(), " Coffee "), Item(receipt, Guid.NewGuid(), "Coffee"), Item(receipt, Guid.NewGuid(), "Coffee"), Item(receipt, Guid.NewGuid(), "\tCoffee\t"), Item(receipt, Guid.NewGuid(), "\u00a0Coffee\u00a0") };
			items[2].ReceiptItemCode = ""; items[3].ReceiptItemCode = " ";
			await Seed(fixture, items);
			Guid otherReceipt = Guid.NewGuid(); var other = Item(otherReceipt, Guid.NewGuid(), "Coffee"); await Seed(fixture, other);
			await using var db = fixture.CreateDbContext();
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"Receipts\" SET \"Location\" = 'Other store' WHERE \"Id\" = {otherReceipt}");
			Guid run = await Start(fixture); await Drain(fixture, run);
			// Capture only the invalidated source; no sweep/inbox can mask overmatching.
			await db.OrganizationItems.ExecuteDeleteAsync();
			await db.Database.ExecuteSqlInterpolatedAsync($"SELECT organization.enqueue_source({kind}, 'Test store', 'Coffee', NULL)");
			await Drain(fixture, run, 2);
			Guid[] expected = (kind == "raw" ? items.Take(1) : items.Take(4)).Select(x => x.Id).ToArray();
			(await db.OrganizationItems.Select(x => x.ItemId).ToListAsync()).Should().BeEquivalentTo(expected);
			// Delimiter ambiguity intentionally gives two distinct exact tuples the same index hash.
			var a = Item(receipt, Guid.NewGuid(), "a\u001fb"); a.ReceiptItemCode = "c";
			var b = Item(receipt, Guid.NewGuid(), "a"); b.ReceiptItemCode = "b\u001fc";
			db.ReceiptItems.AddRange(a, b); await db.SaveChangesAsync(); await Drain(fixture, run);
			await db.OrganizationItems.ExecuteDeleteAsync();
			await db.Database.ExecuteSqlInterpolatedAsync($"SELECT organization.enqueue_source({kind}, 'Test store', {a.Description}, {a.ReceiptItemCode})");
			await Drain(fixture, run, 2);
			(await db.OrganizationItems.Select(x => x.ItemId).ToListAsync()).Should().Equal(a.Id);
		}
		finally { await fixture.DisposeAsync(); }
	}

	[Fact]
	public async Task InboxHashCollisionRefusesDifferentExactTupleWithoutOverwritingPendingSource()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			await using var db = fixture.CreateDbContext();
			await db.Database.ExecuteSqlRawAsync("SELECT organization.enqueue_source('raw', 'Test store', 'Coffee', NULL)");
			await db.Database.ExecuteSqlRawAsync("UPDATE organization.\"DirtySources\" SET \"Description\" = 'Different tuple'");
			Func<Task> collision = () => db.Database.ExecuteSqlRawAsync("SELECT organization.enqueue_source('raw', 'Test store', 'Coffee', NULL)");
			(await collision.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Be("organization_source_hash_collision");
			(await db.OrganizationDirtySources.SingleAsync()).Description.Should().Be("Different tuple");
		}
		finally { await fixture.DisposeAsync(); }
	}

	[Fact]
	public async Task SourceCandidateQueriesUseExpressionIndexesForSelectiveSyntheticHistory()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			Guid receipt = Guid.NewGuid();
			await Seed(fixture, Enumerable.Range(0, 1200).Select(n => Item(receipt, Guid.NewGuid(), $"Synthetic purchase {n}")).ToArray());
			await using var db = fixture.CreateDbContext();
			await db.Database.ExecuteSqlRawAsync("ANALYZE receipts.\"ReceiptItems\"");
			await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
			foreach (string kind in new[] { "Raw", "Normalized" })
			{
				string expression = kind == "Raw" ? "md5(\"Description\" || chr(31) || coalesce(\"ReceiptItemCode\", ''))" : "md5(btrim(\"Description\") || chr(31) || coalesce(nullif(btrim(\"ReceiptItemCode\"), ''), ''))";
				string exact = kind == "Raw" ? "\"Description\"" : "btrim(\"Description\")";
				await using var command = new NpgsqlCommand($"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) SELECT \"Id\", \"ReceiptId\", \"Description\", \"ReceiptItemCode\" FROM receipts.\"ReceiptItems\" WHERE \"Id\" >= '00000000-0000-0000-0000-000000000000' AND {expression} = md5('Synthetic purchase 617' || chr(31) || '') AND {exact} COLLATE \"C\" = 'Synthetic purchase 617' AND \"ReceiptItemCode\" IS NULL ORDER BY \"Id\" LIMIT 31", connection);
				string plan = (string)(await command.ExecuteScalarAsync())!;
				plan.Should().Contain($"IX_Organization_Item{kind}Source");
				using var document = JsonDocument.Parse(plan);
				document.RootElement[0].GetProperty("Plan").GetProperty("Actual Rows").GetInt32().Should().Be(1);
				string artifact = Path.Combine(Path.GetTempPath(), $"receipts-1078-{kind.ToLowerInvariant()}-source-plan.json");
				await File.WriteAllTextAsync(artifact, plan);
			}
		}
		finally { await fixture.DisposeAsync(); }
	}
}
