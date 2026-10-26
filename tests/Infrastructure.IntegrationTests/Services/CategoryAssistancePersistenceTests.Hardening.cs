using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class CategoryAssistancePersistenceTests
{
	[Theory]
	[InlineData("missing")]
	[InlineData("non-admin")]
	[InlineData("locked")]
	[InlineData("deleted")]
	public async Task CurrentAuthorizationDenialLeavesSettingsAndAuditUntouched(string state)
	{
		string actor = state == "missing" ? Guid.NewGuid().ToString() : await Admin();
		await using (var db = fixture.CreateDbContext())
		{
			if (state == "non-admin")
			{
				db.UserRoles.RemoveRange(await db.UserRoles.Where(x => x.UserId == actor).ToListAsync());
			}

			if (state == "locked")
			{
				var user = await db.Users.SingleAsync(x => x.Id == actor);
				user.LockoutEnabled = true; user.LockoutEnd = DateTimeOffset.UtcNow.AddDays(1);
			}
			if (state == "deleted")
			{
				db.Users.Remove(await db.Users.SingleAsync(x => x.Id == actor));
			}

			await db.SaveChangesAsync();
		}
		var before = await Store().GetAsync();
		Func<Task> denied = () => Store().UpdateAsync(actor, before.Revision, before with { Enabled = false });
		(await denied.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(403);
		(await Store().GetAsync()).Should().Be(before);
		await using var verify = fixture.CreateDbContext();
		(await verify.CategoryAssistanceSettingsAudits.CountAsync(x => x.PreviousRevision == before.Revision)).Should().Be(0);
	}

	[Theory]
	[InlineData(0, 3)]
	[InlineData(101, 3)]
	[InlineData(10, 0)]
	[InlineData(10, 21)]
	public async Task InvalidLimitsCannotChangeSettingsOrCreateAudit(int daily, int pending)
	{
		string actor = await Admin(); var before = await Store().GetAsync();
		Func<Task> invalid = () => Store().UpdateAsync(actor, before.Revision,
			before with { DailyLimit = daily, PendingLimit = pending });
		(await invalid.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(400);
		(await Store().GetAsync()).Should().Be(before);
		await using var db = fixture.CreateDbContext();
		(await db.CategoryAssistanceSettingsAudits.CountAsync(x => x.PreviousRevision == before.Revision)).Should().Be(0);
	}

	[Fact]
	public async Task CallerTransactionWithTrackedOldSettingsReturnsDefinedCasConflict()
	{
		string actor = await Admin(); var before = await Store().GetAsync();
		await using var db = fixture.CreateDbContext();
		await db.CategoryAssistanceSettings.SingleAsync();
		var current = await Store().UpdateAsync(actor, before.Revision, before with { Enabled = false });
		await using var tx = await db.Database.BeginTransactionAsync();
		Func<Task> stale = () => CategoryAssistanceSettingsStore.UpdateInTransactionAsync(db, actor, before.Revision,
			before with { DailyLimit = 11 }, DateTimeOffset.UtcNow);
		(await stale.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		await tx.RollbackAsync();
		(await Store().GetAsync()).Should().Be(current);
	}

	[Fact]
	public async Task SettingsAuditRejectsBothUpdatesAndDeletesInPostgres()
	{
		string actor = await Admin(); var before = await Store().GetAsync();
		var after = await Store().UpdateAsync(actor, before.Revision, before with { Enabled = false });
		await using var db = fixture.CreateDbContext();
		Func<Task> update = () => db.Database.ExecuteSqlInterpolatedAsync(
			$"UPDATE category_assistance.\"SettingsAudit\" SET \"Actor\" = 'forged' WHERE \"Revision\" = {after.Revision}");
		Func<Task> delete = () => db.Database.ExecuteSqlInterpolatedAsync(
			$"DELETE FROM category_assistance.\"SettingsAudit\" WHERE \"Revision\" = {after.Revision}");
		(await update.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
		(await delete.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
		(await db.CategoryAssistanceSettingsAudits.SingleAsync(x => x.Revision == after.Revision)).Actor.Should().Be(actor);
	}

	[Fact]
	public async Task ConsentOffDoesNotWaitForOperationalSettingsWriteLock()
	{
		string actor = await Admin(); var settings = await Store().GetAsync();
		await Store().UpdateAsync(actor, settings.Revision, settings with { Enabled = false });
		await using var blocker = fixture.CreateDbContext();
		await using var held = await blocker.Database.BeginTransactionAsync();
		await blocker.Database.ExecuteSqlRawAsync("SELECT 1 FROM category_assistance.\"Settings\" WHERE \"Id\"=1 FOR UPDATE");
		await using var db = fixture.CreateDbContext();
		await using var tx = await db.Database.BeginTransactionAsync();
		await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '250ms'");
		// No source lookup is needed when consent is off, and ordinary saves must not acquire this lock.
		(await CategoryAssistanceObservationWriter.RecordNoChoiceSaveAsync(db, actor, Guid.NewGuid(), Guid.NewGuid(), false, false)).Should().BeFalse();
	}

	[Fact]
	public async Task EnrollmentRejectsDirtyLabelsDeletedSourcesAndInactiveActors()
	{
		string actor = await Admin(); var settings = await Store().GetAsync();
		await Store().UpdateAsync(actor, settings.Revision, settings with { Enabled = true, Provider = "jev", Model = "jev-1.14.0" });
		try
		{
			await using var db = fixture.CreateDbContext();
			Guid receiptId = Guid.NewGuid(), itemId = Guid.NewGuid();
			var receipt = new Infrastructure.Entities.Core.ReceiptEntity { Id = receiptId, Location = "Store", Date = new(2020, 1, 1), TaxAmountCurrency = Common.Currency.USD };
			var item = new Infrastructure.Entities.Core.ReceiptItemEntity { Id = itemId, ReceiptId = receiptId, Description = "Unknown", Category = "Ambiguous label", UnitPriceCurrency = Common.Currency.USD, TotalAmountCurrency = Common.Currency.USD };
			db.Receipts.Add(receipt); db.ReceiptItems.Add(item); await db.SaveChangesAsync();
			await using var tx = await db.Database.BeginTransactionAsync();
			async Task<bool> Observe(string writer) => await CategoryAssistanceObservationWriter.RecordNoChoiceSaveAsync(db, writer, Guid.NewGuid(), itemId, false, false);
			(await Observe(actor)).Should().BeFalse();
			item.Category = ""; item.Subcategory = "Ambiguous child"; await db.SaveChangesAsync();
			(await Observe(actor)).Should().BeFalse();
			item.Subcategory = null; item.DeletedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
			(await Observe(actor)).Should().BeFalse();
			item.DeletedAt = null; await db.SaveChangesAsync();
			receipt.DeletedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
			(await Observe(actor)).Should().BeFalse();
			receipt.DeletedAt = null; await db.SaveChangesAsync();
			(await Observe("missing-" + Guid.NewGuid())).Should().BeFalse();
			var user = await db.Users.SingleAsync(x => x.Id == actor); user.LockoutEnabled = true; user.LockoutEnd = DateTimeOffset.UtcNow.AddDays(1); await db.SaveChangesAsync();
			(await Observe(actor)).Should().BeFalse();
			(await db.CategoryAssistanceObservations.AnyAsync(x => x.ReceiptItemId == itemId)).Should().BeFalse();
			await tx.RollbackAsync();
		}
		finally
		{
			var current = await Store().GetAsync(); await Store().UpdateAsync(actor, current.Revision, current with { Enabled = false });
		}
	}

	[Fact]
	public async Task ConcurrentDisableRechecksDatabaseConsentEvenWhenEnabledSettingsAreTracked()
	{
		string actor = await Admin(); var settings = await Store().GetAsync();
		settings = await Store().UpdateAsync(actor, settings.Revision,
			settings with { Enabled = true, Provider = "jev", Model = "jev-1.14.0" });
		await using var db = fixture.CreateDbContext();
		await db.CategoryAssistanceSettings.SingleAsync(); // Stale enabled entity must not override the locked database row.
		Guid receiptId = Guid.NewGuid(), itemId = Guid.NewGuid();
		db.Receipts.Add(new Infrastructure.Entities.Core.ReceiptEntity { Id = receiptId, Location = "Concurrency store", Date = new(2020, 1, 1), TaxAmountCurrency = Common.Currency.USD });
		db.ReceiptItems.Add(new Infrastructure.Entities.Core.ReceiptItemEntity { Id = itemId, ReceiptId = receiptId, Description = "Concurrent purchase", Category = "", UnitPriceCurrency = Common.Currency.USD, TotalAmountCurrency = Common.Currency.USD });
		await db.SaveChangesAsync();
		await using var blocker = fixture.CreateDbContext();
		await using var held = await blocker.Database.BeginTransactionAsync();
		await CategoryAssistanceSettingsStore.UpdateInTransactionAsync(blocker, actor, settings.Revision,
			settings with { Enabled = false }, DateTimeOffset.UtcNow);
		await using var tx = await db.Database.BeginTransactionAsync();
		int process = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
		var observation = CategoryAssistanceObservationWriter.RecordNoChoiceSaveAsync(db, actor, Guid.NewGuid(), itemId, false, false);
		await using var monitor = new NpgsqlConnection(fixture.ConnectionString); await monitor.OpenAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (true)
		{
			await using var command = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid)) > 0", monitor);
			command.Parameters.AddWithValue("pid", process);
			if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!)
			{
				break;
			}

			await Task.Delay(10, timeout.Token);
		}
		await held.CommitAsync();
		(await observation).Should().BeFalse();
		await tx.CommitAsync();
		await using var verify = fixture.CreateDbContext();
		(await verify.CategoryAssistanceObservations.AnyAsync(x => x.ReceiptItemId == itemId)).Should().BeFalse();
	}

	[Fact]
	public async Task PortableExportContainsNoAssistanceOperationalTables()
	{
		string actor = await Admin(); var settings = await Store().GetAsync();
		await Store().UpdateAsync(actor, settings.Revision, settings with { Enabled = false });
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		try
		{
			await using var sqlite = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
			await sqlite.OpenAsync();
			await using var command = sqlite.CreateCommand();
			command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
			await using var reader = await command.ExecuteReaderAsync();
			var tables = new List<string>(); while (await reader.ReadAsync())
			{
				tables.Add(reader.GetString(0));
			}

			tables.Should().Contain("receipts");
			tables.Should().NotContain(x => x.Contains("assistance", StringComparison.OrdinalIgnoreCase)
				|| x.Equals("Settings", StringComparison.OrdinalIgnoreCase) || x.Equals("SettingsAudit", StringComparison.OrdinalIgnoreCase)
				|| x.Equals("Observations", StringComparison.OrdinalIgnoreCase));
		}
		finally { File.Delete(path); }
	}
}
