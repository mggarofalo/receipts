using System.Globalization;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

public partial class TemplatePricePrecisionTests
{
	[Fact]
	public async Task PortableBackup_PreservesDecimalText_AndFreshThenExistingPostgresPrices()
	{
		decimal?[] prices = [3.459m, 7.1234m, 3.45m, null];
		Guid[] ids = [.. prices.Select(_ => Guid.NewGuid())];
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			for (int i = 0; i < ids.Length; i++)
			{
				seed.ItemTemplates.Add(new() { Id = ids[i], Name = $"Portable price {ids[i]}", DefaultUnitPrice = prices[i], DefaultUnitPriceCurrency = prices[i].HasValue ? Currency.USD : null });
			}

			await seed.SaveChangesAsync();
		}
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		try
		{
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				for (int i = 0; i < ids.Length; i++)
				{
					await using SqliteCommand command = sqlite.CreateCommand();
					command.CommandText = "SELECT default_unit_price FROM item_templates WHERE id=$id";
					command.Parameters.AddWithValue("$id", ids[i].ToString());
					object? stored = await command.ExecuteScalarAsync();
					if (prices[i] is decimal price)
					{
						decimal.Parse((string)stored!, CultureInfo.InvariantCulture).Should().Be(price);
					}
					else
					{
						stored.Should().Be(DBNull.Value);
					}
				}
			}
			PostgresFixture target = new();
			try
			{
				await target.InitializeAsync();
				for (int pass = 0; pass < 2; pass++)
				{
					await using (FileStream stream = File.OpenRead(path))
					{
						await new BackupImportService(new Factory(target), NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, CancellationToken.None);
					}

					await using ApplicationDbContext read = target.CreateDbContext();
					for (int i = 0; i < ids.Length; i++)
					{
						ItemTemplateEntity row = await read.ItemTemplates.SingleAsync(row => row.Id == ids[i]);
						row.DefaultUnitPrice.Should().Be(prices[i]);
						if (pass == 0) { row.DefaultUnitPrice = 1.25m; row.DefaultUnitPriceCurrency = Currency.USD; }
					}
					if (pass == 0)
					{
						await read.SaveChangesAsync();
					}
				}
			}
			finally { await target.DisposeAsync(); }
		}
		finally { File.Delete(path); }
	}

	[Theory]
	[InlineData("100000000000000", false)]
	[InlineData("-100000000000000", false)]
	[InlineData("99999999999999.99995", false)]
	[InlineData("-99999999999999.99995", false)]
	[InlineData("99999999999999.99994", true)]
	[InlineData("-99999999999999.99994", true)]
	public async Task LegacyPriceBoundary_PreservesRepresentableValues_OrRollsBackEarlierAccountWritesAndAudits(string backupPrice, bool fits)
	{
		Guid templateId = Guid.NewGuid(), existingAccount = Guid.NewGuid(), newAccount = Guid.NewGuid();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Accounts.AddRange(new AccountEntity { Id = existingAccount, Name = "Backup name", IsActive = true }, new AccountEntity { Id = newAccount, Name = "New backup account", IsActive = true });
			seed.ItemTemplates.Add(new() { Id = templateId, Name = $"Legacy price {templateId}", DefaultUnitPrice = 1.25m, DefaultUnitPriceCurrency = Currency.USD });
			await seed.SaveChangesAsync();
		}
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		try
		{
			await using (SqliteConnection sqlite = new($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using SqliteCommand command = sqlite.CreateCommand();
				command.CommandText = "UPDATE backup_metadata SET value='4' WHERE key='export_version'; UPDATE item_templates SET default_unit_price=$price WHERE id=$id";
				command.Parameters.AddWithValue("$price", backupPrice);
				command.Parameters.AddWithValue("$id", templateId.ToString());
				await command.ExecuteNonQueryAsync();
			}
			int auditCount;
			await using (ApplicationDbContext target = fixture.CreateDbContext())
			{
				(await target.Accounts.SingleAsync(row => row.Id == existingAccount)).Name = "Target name";
				target.Accounts.Remove(await target.Accounts.SingleAsync(row => row.Id == newAccount));
				await target.SaveChangesAsync();
				auditCount = await target.AuditLogs.CountAsync();
			}
			await using FileStream stream = File.OpenRead(path);
			Func<Task> import = () => new BackupImportService(new Factory(fixture), NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, CancellationToken.None);
			if (fits)
			{
				await import();
			}
			else
			{
				(await import.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("default unit price outside the supported range");
			}

			await using ApplicationDbContext verify = fixture.CreateDbContext();
			(await verify.Accounts.SingleAsync(row => row.Id == existingAccount)).Name.Should().Be(fits ? "Backup name" : "Target name");
			(await verify.Accounts.AnyAsync(row => row.Id == newAccount)).Should().Be(fits);
			decimal expectedPrice = fits ? decimal.Round(decimal.Parse(backupPrice, CultureInfo.InvariantCulture), 4, MidpointRounding.AwayFromZero) : 1.25m;
			(await verify.ItemTemplates.SingleAsync(row => row.Id == templateId)).DefaultUnitPrice.Should().Be(expectedPrice);
			if (fits)
			{
				(await verify.AuditLogs.CountAsync()).Should().BeGreaterThan(auditCount);
			}
			else
			{
				(await verify.AuditLogs.CountAsync()).Should().Be(auditCount);
			}
		}
		finally { File.Delete(path); }
	}
}
