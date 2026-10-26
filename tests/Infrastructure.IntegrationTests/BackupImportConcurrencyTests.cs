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
	public async Task Import_CurrentArchive_FencesCompetingWriters_AndRejectsMissingParents(string kind, bool competingMove)
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
				await BackupService.CreateSchemaAsync(sqlite, CancellationToken.None, 10);
				await using SqliteCommand sql = sqlite.CreateCommand();
				sql.CommandText = "INSERT INTO backup_metadata VALUES ('export_version', '10'); INSERT INTO accounts VALUES ($account, 'Imported earlier', 1); INSERT INTO receipts VALUES ($receipt, 'Imported', '2024-01-01', '0', 'USD', NULL, NULL);" + (kind switch
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
				if (kind == "transaction")
				{
					sql.CommandText = "INSERT INTO accounts VALUES ($existing, 'Existing', 1); INSERT INTO cards VALUES ($card, '1234', 'Existing', 1, $existing)";
					sql.Parameters.AddWithValue("$existing", account.ToString());
					await sql.ExecuteNonQueryAsync();
				}
				if (!competingMove)
				{
					sql.CommandText = "DELETE FROM receipts";
					await sql.ExecuteNonQueryAsync();
				}
				await CurrentBackupFixture.CompleteAsync(sqlite, fixture);
			}

			// A direct SQL contender must fail NOWAIT while the restore owns its destination lock.
			DestinationLockProbe probe = new(fixture);
			DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(probe).Options;
			BackupImportService service = new(new Factory(options), NullLogger<BackupImportService>.Instance);
			// BackupImportLock is process-wide. In the full suite another restore can
			// hold its local lease for over 40 seconds before this import can start.
			using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
			await using FileStream stream = File.OpenRead(path);
			Func<Task> import = async () => await service.ImportFromSqliteAsync(stream, timeout.Token);
			if (competingMove)
			{
				await import();
				probe.Protected.Should().BeTrue("current restore fences competing writers before its child snapshot");
			}
			else
			{
				await import.Should().ThrowAsync<InvalidOperationException>().WithMessage("*dependency is missing*");
			}

			await using ApplicationDbContext read = fixture.CreateDbContext();
			(await read.Accounts.AnyAsync(row => row.Id == importedAccount)).Should().Be(competingMove);
			(await read.AuditLogs.AnyAsync(row => row.EntityId == importedAccount.ToString())).Should().Be(competingMove);
			(await read.Receipts.SingleAsync(row => row.Id == destination)).Location.Should().Be(competingMove ? "Imported" : "Before");
			(await read.AcceptedDuplicatePairs.AnyAsync(row => row.Id == pair)).Should().Be(!competingMove);
			Guid actualParent = kind switch
			{
				"transaction" => await read.Transactions.Where(row => row.Id == child).Select(row => row.ReceiptId).SingleAsync(),
				"item" => await read.ReceiptItems.Where(row => row.Id == child).Select(row => row.ReceiptId).SingleAsync(),
				_ => await read.Adjustments.Where(row => row.Id == child).Select(row => row.ReceiptId).SingleAsync(),
			};
			actualParent.Should().Be(competingMove ? destination : original);
		}
		finally
		{
			File.Delete(path);
		}
	}

	private sealed class DestinationLockProbe(PostgresFixture fixture) : DbCommandInterceptor
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
				await using NpgsqlTransaction transaction = await competitor.BeginTransactionAsync(cancellationToken);
				try
				{
					await using NpgsqlCommand probe = new("LOCK TABLE receipts.\"Receipts\" IN ROW EXCLUSIVE MODE NOWAIT", competitor, transaction);
					await probe.ExecuteNonQueryAsync(cancellationToken);
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

}
