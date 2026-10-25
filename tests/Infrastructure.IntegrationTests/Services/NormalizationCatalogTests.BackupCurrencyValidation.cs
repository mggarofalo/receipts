using Application.Normalization;
using Common;
using FluentAssertions;
using FluentAssertions.Execution;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.NormalizationAi;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData("receipts", "tax_amount_currency", "999")]
	[InlineData("receipt_items", "unit_price_currency", "999")]
	[InlineData("receipt_items", "total_amount_currency", "999")]
	[InlineData("transactions", "amount_currency", "999")]
	[InlineData("adjustments", "amount_currency", "999")]
	[InlineData("item_templates", "default_unit_price_currency", "999")]
	[InlineData("receipts", "tax_amount_currency", "0")]
	[InlineData("receipts", "tax_amount_currency", "-1")]
	[InlineData("receipts", "tax_amount_currency", "CAD")]
	[InlineData("item_templates", "default_unit_price_currency", "0")]
	[InlineData("item_templates", "default_unit_price_currency", "-1")]
	[InlineData("item_templates", "default_unit_price_currency", "CAD")]
	public async Task PortableBackup_UnsupportedCurrencyRejectsAndRollsBackMoneyRelationshipsAndRestoreFence(
		string table, string column, string currency)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Currency rejection product");
		var ids = await ItemsAsync(Source, 1);
		var item = (await ReadAsync(ids)).Single();
		var accountId = Guid.NewGuid();
		var cardId = Guid.NewGuid();
		var templateId = Guid.NewGuid();
		var transactionId = Guid.NewGuid();
		var adjustmentId = Guid.NewGuid();
		var preferenceRevision = Guid.NewGuid();
		await using (var seed = fixture.CreateDbContext())
		{
			seed.Accounts.Add(new() { Id = accountId, Name = "Original account", IsActive = true });
			seed.Cards.Add(new() { Id = cardId, AccountId = accountId, Name = "Original card", CardCode = cardId.ToString(), IsActive = true });
			seed.ItemTemplates.Add(new() { Id = templateId, Name = "Original template", DefaultUnitPrice = 3.459m, DefaultUnitPriceCurrency = Currency.USD });
			seed.Transactions.Add(new() { Id = transactionId, ReceiptId = item.ReceiptId, CardId = cardId, Amount = 3.12m, AmountCurrency = Currency.USD, Date = new(2025, 1, 15) });
			seed.Adjustments.Add(new() { Id = adjustmentId, ReceiptId = item.ReceiptId, Type = AdjustmentType.Discount, Amount = 0.25m, AmountCurrency = Currency.USD });
			var preference = await seed.NormalizationAssistancePreferences.SingleOrDefaultAsync();
			if (preference is null)
			{
				preference = new NormalizationAssistancePreferenceEntity();
				seed.NormalizationAssistancePreferences.Add(preference);
			}
			preference.OwnerUserId = Actor;
			preference.Enabled = true;
			preference.Revision = preferenceRevision;
			await seed.SaveChangesAsync();
		}
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		var facts = await FactsAsync(ids);
		int auditCount;
		await using (var baseline = fixture.CreateDbContext()) { auditCount = await baseline.AuditLogs.CountAsync(); }
		string exported = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		string corrupted = Path.GetTempFileName();
		try
		{
			File.Copy(exported, corrupted, overwrite: true);
			await using (var archive = new SqliteConnection($"Data Source={corrupted};Pooling=False"))
			{
				await archive.OpenAsync();
				await using var command = archive.CreateCommand();
				// Fixed theory inputs identify the column; only the disposable archive is modified.
				command.CommandText = $"UPDATE {table} SET {column}=$currency";
				command.Parameters.AddWithValue("$currency", currency);
				(await command.ExecuteNonQueryAsync()).Should().BeGreaterThan(0);
			}
			await using var stream = File.OpenRead(corrupted);
			var failure = await Record.ExceptionAsync(() => new BackupImportService(new Factory(fixture), NullLogger<BackupImportService>.Instance)
				.ImportFromSqliteAsync(stream, CancellationToken.None));

			using var assertions = new AssertionScope();
			failure.Should().BeOfType<InvalidOperationException>("portable currency values must be supported enum names, never numeric enum representations");
			(await FactsAsync(ids)).Should().BeEquivalentTo(facts);
			await using var verify = fixture.CreateDbContext();
			var receipt = await verify.Receipts.SingleAsync(x => x.Id == item.ReceiptId);
			receipt.Location.Should().Be(Source.Location);
			receipt.TaxAmount.Should().Be(0m);
			receipt.TaxAmountCurrency.Should().Be(Currency.USD);
			var template = await verify.ItemTemplates.SingleAsync(x => x.Id == templateId);
			template.DefaultUnitPrice.Should().Be(3.459m);
			template.DefaultUnitPriceCurrency.Should().Be(Currency.USD);
			var transaction = await verify.Transactions.SingleAsync(x => x.Id == transactionId);
			transaction.ReceiptId.Should().Be(item.ReceiptId);
			transaction.CardId.Should().Be(cardId);
			transaction.Amount.Should().Be(3.12m);
			transaction.AmountCurrency.Should().Be(Currency.USD);
			var adjustment = await verify.Adjustments.SingleAsync(x => x.Id == adjustmentId);
			adjustment.ReceiptId.Should().Be(item.ReceiptId);
			adjustment.Amount.Should().Be(0.25m);
			adjustment.AmountCurrency.Should().Be(Currency.USD);
			(await verify.Cards.SingleAsync(x => x.Id == cardId)).AccountId.Should().Be(accountId);
			(await verify.AuditLogs.CountAsync()).Should().Be(auditCount);
			var retainedPreference = await verify.NormalizationAssistancePreferences.SingleAsync();
			retainedPreference.Enabled.Should().BeTrue("a rejected archive must not commit the restore fence");
			retainedPreference.Revision.Should().Be(preferenceRevision);
			var retainedOperation = (await service.GetOperationAsync(Actor, preview.Id, CancellationToken.None))!;
			retainedOperation.State.Should().Be("preview");
			retainedOperation.Revision.Should().Be(preview.Revision);
		}
		finally { File.Delete(corrupted); File.Delete(exported); }
	}
}
