using System.Data.Common;
using System.Globalization;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests;

// Class-local fixture: backup reads every table, so unrelated integration-test data
// must not become part of this test's source database.
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class BackupSnapshotTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Theory]
	[InlineData("none")]
	[InlineData("create")]
	[InlineData("update")]
	public async Task Export_ConcurrentAggregateWrite_ContainsOneConsistentSnapshot(string mutation)
	{
		Guid receiptId = Guid.NewGuid(), itemId = Guid.NewGuid();
		Guid lateReceiptId = Guid.NewGuid(), lateItemId = Guid.NewGuid();
		string oldDescription = $"Snapshot item {itemId}";
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			AddAggregate(seed, receiptId, itemId, "Before", oldDescription, 1, 3);
			await seed.SaveChangesAsync();
		}

		AfterReceiptSelect boundary = new(async () =>
		{
			if (mutation == "none")
			{
				return;
			}
			await using ApplicationDbContext concurrent = fixture.CreateDbContext();
			if (mutation == "create")
			{
				AddAggregate(concurrent, lateReceiptId, lateItemId, "Late", $"Late item {lateItemId}", 2, 5);
			}
			else
			{
				ReceiptEntity receipt = await concurrent.Receipts.SingleAsync(row => row.Id == receiptId);
				ReceiptItemEntity item = await concurrent.ReceiptItems.IgnoreAutoIncludes().SingleAsync(row => row.Id == itemId);
				receipt.Location = "After";
				receipt.TaxAmount = 2;
				item.Description = $"Changed item {itemId}";
				item.UnitPrice = 5;
				item.TotalAmount = 5;
			}
			await concurrent.SaveChangesAsync();
		});
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
			.AddInterceptors(boundary).Options;
		ExportPathLogger logger = new();
		BackupService service = new(new Factory(options), logger);
		try
		{
			string path = await service.ExportToSqliteAsync();
			using (FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
			{
				exclusive.Length.Should().BeGreaterThan(0, "the completed artifact belongs to its caller and must remain readable without global pool clearing");
			}
			boundary.Fired.Should().BeTrue("the concurrent write must run after the receipt SELECT and before item export");
			await using (ApplicationDbContext source = fixture.CreateDbContext())
			{
				if (mutation == "create")
				{
					(await source.Receipts.AnyAsync(row => row.Id == lateReceiptId)).Should().BeTrue();
					(await source.ReceiptItems.AnyAsync(row => row.Id == lateItemId && row.ReceiptId == lateReceiptId)).Should().BeTrue();
				}
				else if (mutation == "update")
				{
					(await source.Receipts.SingleAsync(row => row.Id == receiptId)).Location.Should().Be("After");
					(await source.ReceiptItems.SingleAsync(row => row.Id == itemId)).UnitPrice.Should().Be(5);
				}
			}

			await using SqliteConnection sqlite = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
			await sqlite.OpenAsync();
			await using (SqliteCommand foreignKeys = sqlite.CreateCommand())
			{
				foreignKeys.CommandText = "PRAGMA foreign_key_check";
				await using SqliteDataReader violations = await foreignKeys.ExecuteReaderAsync();
				(await violations.ReadAsync()).Should().BeFalse("the actual exported SQLite file must have no dangling foreign keys");
			}
			await using (SqliteCommand aggregate = sqlite.CreateCommand())
			{
				aggregate.CommandText = "SELECT r.location, r.tax_amount, i.description, i.unit_price, i.total_amount FROM receipts r JOIN receipt_items i ON i.receipt_id = r.id WHERE r.id = $receipt AND i.id = $item";
				aggregate.Parameters.AddWithValue("$receipt", receiptId.ToString());
				aggregate.Parameters.AddWithValue("$item", itemId.ToString());
				await using SqliteDataReader row = await aggregate.ExecuteReaderAsync();
				(await row.ReadAsync()).Should().BeTrue();
				row.GetString(0).Should().Be("Before");
				decimal.Parse(row.GetString(1), CultureInfo.InvariantCulture).Should().Be(1);
				row.GetString(2).Should().Be(oldDescription, "parent and child must represent the same source snapshot");
				decimal.Parse(row.GetString(3), CultureInfo.InvariantCulture).Should().Be(3);
				decimal.Parse(row.GetString(4), CultureInfo.InvariantCulture).Should().Be(3);
				(await row.ReadAsync()).Should().BeFalse();
			}
			await using SqliteCommand lateRows = sqlite.CreateCommand();
			lateRows.CommandText = "SELECT (SELECT COUNT(*) FROM receipts WHERE id = $receipt) + (SELECT COUNT(*) FROM receipt_items WHERE id = $item)";
			lateRows.Parameters.AddWithValue("$receipt", lateReceiptId.ToString());
			lateRows.Parameters.AddWithValue("$item", lateItemId.ToString());
			Convert.ToInt64(await lateRows.ExecuteScalarAsync(), CultureInfo.InvariantCulture).Should().Be(0);
			if (mutation == "create")
			{
				// Import this actual snapshot into a separately migrated PostgreSQL database.
				PostgresFixture target = new();
				try
				{
					await target.InitializeAsync();
					BackupImportService importer = new(new Factory(target.CreateOptions()), NullLogger<BackupImportService>.Instance);
					await using (FileStream stream = File.OpenRead(path))
					{
						await importer.ImportFromSqliteAsync(stream, CancellationToken.None);
					}
					await using ApplicationDbContext restored = target.CreateDbContext();
					ReceiptEntity restoredReceipt = await restored.Receipts.SingleAsync(row => row.Id == receiptId);
					ReceiptItemEntity restoredItem = await restored.ReceiptItems.SingleAsync(row => row.Id == itemId);
					restoredReceipt.Location.Should().Be("Before");
					restoredReceipt.TaxAmount.Should().Be(1);
					restoredItem.ReceiptId.Should().Be(receiptId);
					restoredItem.Description.Should().Be(oldDescription);
					restoredItem.UnitPrice.Should().Be(3);
					(await restored.Receipts.AnyAsync(row => row.Id == lateReceiptId)).Should().BeFalse();
					(await restored.ReceiptItems.AnyAsync(row => row.Id == lateItemId)).Should().BeFalse();
				}
				finally
				{
					await target.DisposeAsync();
				}
			}
		}
		finally
		{
			// Capture only this export's structured path; never scan global temp files.
			if (logger.Path is { } path)
			{
				File.Delete(path);
				File.Delete(path + "-journal");
				File.Delete(path + "-wal");
				File.Delete(path + "-shm");
			}
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Export_SourceReadFailureOrCancellation_DeletesOwnedArtifactAndAllowsNextExport(bool cancel)
	{
		using CancellationTokenSource cancellation = new();
		ExportPathLogger logger = new();
		InvalidOperationException expected = new("Injected source read failure");
		bool openedDestinationObserved = false;
		AfterReceiptSelect boundary = new(() =>
		{
			logger.Path.Should().NotBeNull();
			openedDestinationObserved = File.Exists(logger.Path);
			if (cancel)
			{
				cancellation.Cancel();
				cancellation.Token.ThrowIfCancellationRequested();
			}
			throw expected;
		});
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(boundary).Options;
		BackupService service = new(new Factory(options), logger);
		try
		{
			Func<Task> export = () => service.ExportToSqliteAsync(cancellation.Token);
			if (cancel)
			{
				var failure = await export.Should().ThrowAsync<OperationCanceledException>();
				failure.Which.CancellationToken.Should().Be(cancellation.Token);
			}
			else
			{
				var failure = await export.Should().ThrowAsync<InvalidOperationException>();
				failure.Which.Should().BeSameAs(expected, "cleanup must preserve the original source failure");
			}
			boundary.Fired.Should().BeTrue();
			openedDestinationObserved.Should().BeTrue("this must exercise cleanup after SQLite opened, not merely before file creation");
			AssertAbsent(logger.Path!);

			ExportPathLogger recoveryLog = new();
			try
			{
				BackupService recovery = new(new Factory(fixture.CreateOptions()), recoveryLog);
				string path = await recovery.ExportToSqliteAsync();
				using (FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
				{
					exclusive.Length.Should().BeGreaterThan(0);
				}
				File.Delete(path);
				AssertAbsent(path);
			}
			finally
			{
				DeleteOwnedFiles(recoveryLog.Path);
			}
		}
		finally
		{
			DeleteOwnedFiles(logger.Path);
		}
	}

	private static void AssertAbsent(string path)
	{
		foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
		{
			File.Exists(path + suffix).Should().BeFalse($"the failed export must clean its exact owned artifact {suffix}");
		}
	}

	private static void DeleteOwnedFiles(string? path)
	{
		if (path is null)
		{
			return;
		}
		foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
		{
			File.Delete(path + suffix);
		}
	}

	private static void AddAggregate(ApplicationDbContext context, Guid receiptId, Guid itemId, string location, string description, decimal tax, decimal amount)
	{
		context.Receipts.Add(new ReceiptEntity { Id = receiptId, Location = location, Date = new(2024, 1, 1), TaxAmount = tax });
		context.ReceiptItems.Add(new ReceiptItemEntity { Id = itemId, ReceiptId = receiptId, Description = description, Quantity = 1, UnitPrice = amount, TotalAmount = amount, Category = "Food" });
	}

	private sealed class AfterReceiptSelect(Func<Task> action) : DbCommandInterceptor
	{
		public bool Fired { get; private set; }
		public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
		{
			if (!Fired && (command.CommandText.Contains("FROM receipts.\"Receipts\"", StringComparison.Ordinal)
				|| command.CommandText.Contains("FROM \"receipts\".\"Receipts\"", StringComparison.Ordinal)))
			{
				Fired = true;
				await action();
			}
			return result;
		}
	}

	private sealed class ExportPathLogger : ILogger<BackupService>
	{
		public string? Path { get; private set; }
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => true;
		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (state is IEnumerable<KeyValuePair<string, object?>> values)
			{
				Path ??= values.FirstOrDefault(value => value.Key == "Path").Value as string;
			}
		}
	}

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
	}
}
