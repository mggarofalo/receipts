using Application.Classification;
using Application.Taxonomy;
using Common;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed partial class CategoryAssistancePersistenceTests(PostgresFixture fixture)
{
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }
	private CategoryAssistanceSettingsStore Store() => new(new Factory(fixture), TimeProvider.System);
	private async Task<string> Admin()
	{
		string actor = "category-assistance-" + Guid.NewGuid();
		await using var db = fixture.CreateDbContext();
		db.Users.Add(new ApplicationUser { Id = actor, UserName = actor });
		var role = await db.Roles.FirstOrDefaultAsync(x => x.Name == AppRoles.Admin);
		if (role == null) { role = new IdentityRole(AppRoles.Admin); db.Roles.Add(role); }
		db.UserRoles.Add(new IdentityUserRole<string> { UserId = actor, RoleId = role.Id });
		await db.SaveChangesAsync(); return actor;
	}
	[Fact]
	public async Task ConcurrentCasHasOneWinnerAndAuditAndRollbackAreAtomic()
	{
		string actor = await Admin(); string other = await Admin();
		var before = await Store().GetAsync();
		async Task<int> Attempt(string admin)
		{
			// A decision change rotates the revision, so the compare-and-set has exactly one winner.
			try { await Store().UpdateAsync(admin, before.Revision, before with { AutoApply = !before.AutoApply }); return 200; }
			catch (TaxonomyException e) { return e.Status; }
		}
		(await Task.WhenAll(Attempt(actor), Attempt(other))).Should().BeEquivalentTo([200, 409]);
		await using var verify = fixture.CreateDbContext();
		var current = await Store().GetAsync();
		current.Revision.Should().NotBe(before.Revision);
		var audit = await verify.CategoryAssistanceSettingsAudits.SingleAsync(x => x.Revision == current.Revision);
		audit.Actor.Should().BeOneOf(actor, other);
		audit.PreviousRevision.Should().Be(before.Revision);
		await using (var db = fixture.CreateDbContext())
		await using (var transaction = await db.Database.BeginTransactionAsync())
		{
			await CategoryAssistanceSettingsStore.UpdateInTransactionAsync(db, actor, current.Revision,
				current with { Enabled = true, Provider = "jev", Model = "jev-1.14.0" }, DateTimeOffset.UtcNow);
			await transaction.RollbackAsync();
		}
		(await Store().GetAsync()).Should().Be(current);
		(await verify.CategoryAssistanceSettingsAudits.CountAsync(x => x.PreviousRevision == current.Revision)).Should().Be(0);
		await Store().UpdateAsync(actor, current.Revision, current with { AutoApply = before.AutoApply });
	}
	[Fact]
	public async Task ConsentIntentIdempotenceAndReceiptRollbackUseRealTransaction()
	{
		string actor = await Admin(); var settings = await Store().GetAsync();
		settings = await Store().UpdateAsync(actor, settings.Revision, settings with { Enabled = false });
		Guid receiptId = Guid.NewGuid(), itemId = Guid.NewGuid(), save = Guid.NewGuid();
		await using (var db = fixture.CreateDbContext())
		{
			db.Receipts.Add(new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2020, 1, 1), TaxAmountCurrency = Currency.USD });
			db.ReceiptItems.Add(new ReceiptItemEntity { Id = itemId, ReceiptId = receiptId, Description = "Unknown purchase", Category = "", UnitPriceCurrency = Currency.USD, TotalAmountCurrency = Currency.USD });
			await db.SaveChangesAsync();
		}
		async Task<bool> Observe(Guid id, bool intent = false, bool excluded = false, bool rollback = false)
		{
			await using var db = fixture.CreateDbContext(); await using var tx = await db.Database.BeginTransactionAsync();
			bool result = await CategoryAssistanceObservationWriter.RecordNoChoiceSaveAsync(db, actor, id, itemId, intent, excluded);
			if (rollback) { await tx.RollbackAsync(); } else { await tx.CommitAsync(); }
			return result;
		}
		(await Observe(save)).Should().BeFalse();
		settings = await Store().UpdateAsync(actor, settings.Revision, settings with { Enabled = true, Provider = "jev", Model = "jev-1.14.0" });
		(await Observe(save, intent: true)).Should().BeFalse(); (await Observe(save, excluded: true)).Should().BeFalse();
		(await Observe(save, rollback: true)).Should().BeTrue();
		await using var verify = fixture.CreateDbContext();
		(await verify.CategoryAssistanceObservations.CountAsync(x => x.ReceiptItemId == itemId)).Should().Be(0);
		(await Observe(save)).Should().BeTrue(); (await Observe(save)).Should().BeFalse();
		var observation = await verify.CategoryAssistanceObservations.AsNoTracking().SingleAsync(x => x.ReceiptItemId == itemId);
		observation.SettingsRevision.Should().Be(settings.Revision); observation.Location.Should().Be("Store");
		// Receipt edits and evidence share the same caller transaction, including rollback.
		await using (var db = fixture.CreateDbContext())
		await using (var tx = await db.Database.BeginTransactionAsync())
		{
			var item = await db.ReceiptItems.SingleAsync(x => x.Id == itemId); item.Description = "Edited source";
			await db.SaveChangesAsync();
			(await CategoryAssistanceObservationWriter.RecordNoChoiceSaveAsync(db, actor, Guid.NewGuid(), itemId, false, false)).Should().BeTrue();
			await tx.RollbackAsync();
		}
		(await verify.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == itemId)).Description.Should().Be("Unknown purchase");
		(await verify.CategoryAssistanceObservations.CountAsync(x => x.ReceiptItemId == itemId)).Should().Be(1);
		await Store().UpdateAsync(actor, settings.Revision, settings with { Enabled = false });
	}
}
