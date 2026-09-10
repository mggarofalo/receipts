using FluentAssertions;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class AuditLogTests(PostgresFixture fixture)
{
	[Fact]
	public async Task Create_Entity_GeneratesAuditLog()
	{
		// Arrange — Cards now require a valid parent Account (RECEIPTS-575).
		await using ApplicationDbContext context = fixture.CreateDbContext();
		AccountEntity parent = AccountEntityGenerator.Generate();
		CardEntity account = CardEntityGenerator.Generate();
		account.AccountId = parent.Id;
		context.Accounts.Add(parent);

		// Act
		context.Cards.Add(account);
		await context.SaveChangesAsync();

		// Assert — an audit log entry should exist for the creation
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		AuditLogEntity? auditLog = await readContext.AuditLogs
			.OrderByDescending(a => a.ChangedAt)
			.FirstOrDefaultAsync(a => a.EntityId == account.Id.ToString()
				&& a.EntityType == "Card");

		auditLog.Should().NotBeNull();
		auditLog!.Action.Should().Be(AuditAction.Create);
		auditLog.EntityId.Should().Be(account.Id.ToString());

		List<FieldChange> changes = auditLog.GetChanges();
		changes.Should().Contain(c => c.FieldName == "Name" && c.NewValue == account.Name);
		changes.Should().Contain(c => c.FieldName == "CardCode" && c.NewValue == account.CardCode);
	}

	[Fact]
	public async Task Update_Entity_GeneratesAuditLogWithFieldChanges()
	{
		// Arrange — Cards now require a valid parent Account (RECEIPTS-575).
		await using ApplicationDbContext context = fixture.CreateDbContext();
		AccountEntity parent = AccountEntityGenerator.Generate();
		CardEntity account = CardEntityGenerator.Generate();
		account.AccountId = parent.Id;
		context.Accounts.Add(parent);
		context.Cards.Add(account);
		await context.SaveChangesAsync();

		// Act — update the account name
		account.Name = "Updated Card Name";
		await context.SaveChangesAsync();

		// Assert — an Update audit log should exist with the field change
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		AuditLogEntity? auditLog = await readContext.AuditLogs
			.OrderByDescending(a => a.ChangedAt)
			.FirstOrDefaultAsync(a => a.EntityId == account.Id.ToString()
				&& a.EntityType == "Card"
				&& a.Action == AuditAction.Update);

		auditLog.Should().NotBeNull();
		List<FieldChange> changes = auditLog!.GetChanges();
		changes.Should().ContainSingle(c => c.FieldName == "Name")
			.Which.NewValue.Should().Be("Updated Card Name");
	}

	[Fact]
	public async Task SoftDelete_Entity_GeneratesDeleteAuditLog()
	{
		// Arrange
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();

		// Act — soft-delete the receipt
		context.Receipts.Remove(receipt);
		await context.SaveChangesAsync();

		// Assert — a Delete audit log should exist
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		AuditLogEntity? auditLog = await readContext.AuditLogs
			.OrderByDescending(a => a.ChangedAt)
			.FirstOrDefaultAsync(a => a.EntityId == receipt.Id.ToString()
				&& a.EntityType == "Receipt"
				&& a.Action == AuditAction.Delete);

		auditLog.Should().NotBeNull();
	}

	[Fact]
	public async Task AuditLog_Excludes_AuditLogEntities()
	{
		// Arrange — the audit system should not audit itself.
		// Cards now require a valid parent Account (RECEIPTS-575).
		await using ApplicationDbContext context = fixture.CreateDbContext();
		AccountEntity parent = AccountEntityGenerator.Generate();
		CardEntity account = CardEntityGenerator.Generate();
		account.AccountId = parent.Id;
		context.Accounts.Add(parent);

		// Act — create an entity (which triggers audit log creation)
		context.Cards.Add(account);
		await context.SaveChangesAsync();

		// Assert — no audit log should exist for AuditLogEntity type
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		List<AuditLogEntity> selfAuditLogs = await readContext.AuditLogs
			.Where(a => a.EntityType == "AuditLog" || a.EntityType == "AuditLogEntity")
			.ToListAsync();

		selfAuditLogs.Should().BeEmpty("the audit system should not audit its own entries");
	}

	[Fact]
	public async Task FullLifecycle_CreateUpdateDelete_ProducesThreeAuditEntries()
	{
		// Arrange
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();

		// Act — Create
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();

		// Act — Update
		receipt.Location = "Updated Location";
		await context.SaveChangesAsync();

		// Act — Soft-delete
		context.Receipts.Remove(receipt);
		await context.SaveChangesAsync();

		// Assert — three audit entries for this entity
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		List<AuditLogEntity> auditLogs = await readContext.AuditLogs
			.Where(a => a.EntityId == receipt.Id.ToString() && a.EntityType == "Receipt")
			.OrderBy(a => a.ChangedAt)
			.ToListAsync();

		auditLogs.Should().HaveCount(3);
		auditLogs[0].Action.Should().Be(AuditAction.Create);
		auditLogs[1].Action.Should().Be(AuditAction.Update);
		auditLogs[2].Action.Should().Be(AuditAction.Delete);
	}
}
