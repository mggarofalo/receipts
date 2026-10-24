using System.Data.Common;
using Application.Exceptions;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class BackupImportConcurrencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Theory]
	[InlineData("transaction", true)]
	[InlineData("transaction", false)]
	[InlineData("item", true)]
	[InlineData("item", false)]
	[InlineData("adjustment", true)]
	[InlineData("adjustment", false)]
	public async Task Import_ParentSnapshot_RejectsMovesAndLocksPartialArchiveDestinations(string kind, bool competingMove)
	{
		Guid original = Guid.NewGuid(), destination = Guid.NewGuid(), concurrent = Guid.NewGuid(), partner = Guid.NewGuid();
		Guid child = Guid.NewGuid(), account = Guid.NewGuid(), card = Guid.NewGuid(), importedAccount = Guid.NewGuid(), pair = Guid.NewGuid();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Accounts.Add(new() { Id = account, Name = "Existing", IsActive = true });
			seed.Cards.Add(new() { Id = card, AccountId = account, CardCode = "1234", Name = "Existing", IsActive = true });
			seed.Receipts.AddRange(new[] { original, destination, concurrent, partner }.Select(id => new ReceiptEntity { Id = id, Date = new(2024, 1, 1), Location = "Before" }));
			switch (kind)
			{
				case "transaction": seed.Transactions.Add(new() { Id = child, ReceiptId = original, CardId = card, Amount = 1, AmountCurrency = Currency.USD, Date = new(2024, 1, 1) }); break;
				case "item": seed.ReceiptItems.Add(new() { Id = child, ReceiptId = original, Description = "Before", Category = "Food", Quantity = 1, UnitPrice = 1, TotalAmount = 1 }); break;
				default: seed.Adjustments.Add(new() { Id = child, ReceiptId = original, Amount = 1 }); break;
			}
			Guid[] endpoints = [.. new[] { destination, partner }.Order()];
			seed.AcceptedDuplicatePairs.Add(new() { Id = pair, ReceiptIdA = endpoints[0], ReceiptIdB = endpoints[1], AcceptedAt = DateTimeOffset.UtcNow });
			await seed.SaveChangesAsync();
		}

		string path = Path.Combine(Path.GetTempPath(), $"backup-parent-race-{Guid.NewGuid():N}.db");
		try
		{
			await using (SqliteConnection sqlite = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = false }.ToString()))
			{
				await sqlite.OpenAsync();
				await BackupService.CreateSchemaAsync(sqlite, CancellationToken.None);
				await using SqliteCommand sql = sqlite.CreateCommand();
				sql.CommandText = "INSERT INTO backup_metadata VALUES ('export_version', '5'); INSERT INTO accounts VALUES ($account, 'Imported earlier', 1); INSERT INTO receipts VALUES ($receipt, 'Imported', '2024-01-01', '0', 'USD', NULL, NULL);" + (kind switch
				{
					"transaction" => "INSERT INTO transactions VALUES ($child, $receipt, $card, '2', 'USD', '2024-01-01');",
					"item" => "INSERT INTO receipt_items VALUES ($child, $receipt, NULL, 'Imported', '1', '2', 'USD', '2', 'USD', 'Food', NULL, NULL, NULL);",
					_ => "INSERT INTO adjustments VALUES ($child, $receipt, 'Discount', '2', 'USD', 'Imported');",
				});
				sql.Parameters.AddWithValue("$account", importedAccount.ToString());
				sql.Parameters.AddWithValue("$receipt", destination.ToString());
				sql.Parameters.AddWithValue("$child", child.ToString());
				sql.Parameters.AddWithValue("$card", card.ToString());
				await sql.ExecuteNonQueryAsync();
				if (!competingMove)
				{
					sql.CommandText = "DELETE FROM receipts";
					await sql.ExecuteNonQueryAsync();
				}
			}

			// The import has read the child's old parent but has acquired no parent locks yet.
			// Commit a real competing move before letting its planned lock query execute.
			MoveBeforeLock interceptor = new(async cancellationToken =>
			{
				await using ApplicationDbContext move = fixture.CreateDbContext();
				if (kind == "item")
				{
					// An application writer waits for the import's NORM gate. This counterexample
					// deliberately exercises an independent SQL writer after the import snapshot.
					await move.Database.ExecuteSqlInterpolatedAsync($"""UPDATE receipts."ReceiptItems" SET "ReceiptId"={concurrent} WHERE "Id"={child}""", cancellationToken);
					return;
				}
				object entity = kind switch
				{
					"transaction" => await move.Transactions.SingleAsync(row => row.Id == child, cancellationToken),
					"item" => await move.ReceiptItems.SingleAsync(row => row.Id == child, cancellationToken),
					_ => await move.Adjustments.SingleAsync(row => row.Id == child, cancellationToken),
				};
				move.Entry(entity).Property("ReceiptId").CurrentValue = concurrent;
				await move.SaveChangesAsync(cancellationToken);
			});
			DestinationLockProbe probe = new(fixture, destination);
			DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(competingMove ? interceptor : probe).Options;
			BackupImportService service = new(new Factory(options), NullLogger<BackupImportService>.Instance);
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
			await using FileStream stream = File.OpenRead(path);
			Func<Task> import = async () => await service.ImportFromSqliteAsync(stream, timeout.Token);
			if (competingMove)
			{
				await import.Should().ThrowAsync<ConcurrencyConflictException>();
				interceptor.Moved.Should().BeTrue();
			}
			else
			{
				await import();
				probe.Protected.Should().BeTrue("a destination omitted from the archive receipts table must still be locked before graph writes");
			}

			await using ApplicationDbContext read = fixture.CreateDbContext();
			(await read.Accounts.AnyAsync(row => row.Id == importedAccount)).Should().Be(!competingMove);
			(await read.AuditLogs.AnyAsync(row => row.EntityId == importedAccount.ToString())).Should().Be(!competingMove);
			(await read.Receipts.SingleAsync(row => row.Id == destination)).Location.Should().Be("Before");
			(await read.AcceptedDuplicatePairs.AnyAsync(row => row.Id == pair)).Should().Be(competingMove || kind != "transaction");
			Guid actualParent = kind switch
			{
				"transaction" => await read.Transactions.Where(row => row.Id == child).Select(row => row.ReceiptId).SingleAsync(),
				"item" => await read.ReceiptItems.Where(row => row.Id == child).Select(row => row.ReceiptId).SingleAsync(),
				_ => await read.Adjustments.Where(row => row.Id == child).Select(row => row.ReceiptId).SingleAsync(),
			};
			actualParent.Should().Be(competingMove ? concurrent : destination);
		}
		finally
		{
			File.Delete(path);
		}
	}

	private sealed class DestinationLockProbe(PostgresFixture fixture, Guid destination) : DbCommandInterceptor
	{
		public bool Protected { get; private set; }
		private bool _checked;
		public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
		{
			if (!_checked && command.CommandText.Contains("FROM \"receipts\".\"Receipts\"", StringComparison.Ordinal) && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
			{
				_checked = true;
				await using NpgsqlConnection competitor = new(fixture.ConnectionString);
				await competitor.OpenAsync(cancellationToken);
				try
				{
					await using NpgsqlCommand probe = new("SELECT \"Id\" FROM receipts.\"Receipts\" WHERE \"Id\" = $1 FOR UPDATE NOWAIT", competitor);
					probe.Parameters.AddWithValue(destination);
					await probe.ExecuteScalarAsync(cancellationToken);
				}
				catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
				{
					Protected = true;
				}
			}
			return result;
		}
	}
	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
	}

	private sealed class MoveBeforeLock(Func<CancellationToken, Task> move) : DbCommandInterceptor
	{
		public bool Moved { get; private set; }
		public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			if (!Moved && command.CommandText.Contains("FROM \"receipts\".\"Receipts\"", StringComparison.Ordinal) && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
			{
				Moved = true;
				await move(cancellationToken);
			}
			return result;
		}
	}
}
