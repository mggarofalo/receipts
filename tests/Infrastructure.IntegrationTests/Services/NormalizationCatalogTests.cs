using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Normalization;
using Common;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed partial class NormalizationCatalogTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	private const string Actor = "catalog-test-admin";
	private static readonly NormalizationSourceKey Source = new("Store A", "BF FRANKS 8CT", "123");
	private readonly Mock<ICommittedChangePublisher> publisher = new();

	[Fact]
	public async Task ApprovedHistoryMapping_UsesExactStoreDescriptionAndCodeAndPreservesPurchaseFacts()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks 8 count");
		var same = await ItemsAsync(Source, 3);
		var differentStore = await ItemsAsync(Source with { Location = "Store B" }, 1);
		var differentCode = await ItemsAsync(Source with { ItemCode = "456" }, 1);
		var missingCode = await ItemsAsync(Source with { ItemCode = null }, 1);
		var differentSize = await ItemsAsync(Source with { Description = "BF FRANKS 16CT" }, 1);
		var differentCase = await ItemsAsync(Source with { Description = "bf franks 8ct" }, 1);
		var all = same.Concat(differentStore).Concat(differentCode).Concat(missingCode)
			.Concat(differentSize).Concat(differentCase).ToArray();
		var facts = await FactsAsync(all);
		int audits = await AuditCountAsync(all);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);

		preview.Total.Should().Be(3);
		preview.Eligible.Should().Be(3);
		(await ReadAsync(all)).Should().OnlyContain(x => x.NormalizedDescriptionId == null);
		(await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Should().BeEmpty();
		(await AuditCountAsync(all)).Should().Be(audits);

		var approved = await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		approved.Applied.Should().Be(3);
		approved.Remaining.Should().Be(0);
		(await ReadAsync(same)).Should().OnlyContain(x => x.NormalizedDescriptionId == target.Id);
		(await ReadAsync(all.Except(same).ToArray())).Should().OnlyContain(x => x.NormalizedDescriptionId == null);
		(await FactsAsync(all)).Should().BeEquivalentTo(facts);
		(await AuditCountAsync(all)).Should().Be(audits + 3);
		var mapping = (await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Should().ContainSingle().Subject;
		mapping.TargetId.Should().Be(target.Id);
		mapping.State.Should().Be("active");
		await service.ResumeAsync(Actor, approved.Id, CancellationToken.None);
		(await AuditCountAsync(all)).Should().Be(audits + 3, "resuming completed history must not repeat writes");
	}

	[Theory]
	[InlineData(false, 1)]
	[InlineData(true, 2)]
	public async Task Preview_RequiresLegacyConsentAndAlwaysProtectsLaterHumanDecisions(bool includeLegacy, int expectedEligible)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Reviewed beef franks");
		var old = await CanonicalAsync("BF FRANKS 8CT");
		var unassigned = await ItemsAsync(Source, 1);
		var legacy = await ItemsAsync(Source, 1, "legacy_unknown", old.Id);
		var manual = await ItemsAsync(Source, 1, "manual", old.Id);
		var template = await ItemsAsync(Source, 1, "template", old.Id);
		var reviewed = await ItemsAsync(Source, 1, "reviewed", old.Id);
		await ReviewExistingAsync(manual.Concat(template).Concat(reviewed).ToArray(), old);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target) with { IncludeLegacyUnknown = includeLegacy }, CancellationToken.None);
		preview.Total.Should().Be(5);
		preview.Eligible.Should().Be(expectedEligible);
		preview.Protected.Should().Be(5 - expectedEligible);
		await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		(await ReadAsync(unassigned)).Single().NormalizedDescriptionId.Should().Be(target.Id);
		(await ReadAsync(legacy)).Single().NormalizedDescriptionId.Should().Be(includeLegacy ? target.Id : old.Id);
		(await ReadAsync(manual.Concat(template).Concat(reviewed).ToArray()))
			.Should().OnlyContain(x => x.NormalizedDescriptionId == old.Id);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Preview_ParentLocationChangeOrEditAndRevertCannotApplyStaleMembership(bool revert)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks");
		var ids = await ItemsAsync(Source, 1);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		await using (var db = fixture.CreateDbContext())
		{
			var receipt = await db.Receipts.SingleAsync();
			receipt.Location = "Store B";
			await db.SaveChangesAsync();
			if (revert)
			{
				receipt.Location = Source.Location;
				await db.SaveChangesAsync();
			}
		}
		int audits = await AuditCountAsync(ids);
		var result = await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		result.Conflicted.Should().Be(1);
		result.Applied.Should().Be(0);
		(await ReadAsync(ids)).Single().NormalizedDescriptionId.Should().BeNull();
		(await AuditCountAsync(ids)).Should().Be(audits);
	}

	[Fact]
	public async Task Undo_PreservesAnInterveningManualDecisionEvenWhenItSelectedTheSameTarget()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks");
		var ids = await ItemsAsync(Source, 2);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		var approved = await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		await ReviewExistingAsync([ids[0]], target);
		var undone = await service.UndoAsync(Actor, approved.Id, approved.Revision, CancellationToken.None);
		undone.Undone.Should().Be(1);
		var saved = await ReadAsync(ids);
		saved.Single(x => x.Id == ids[0]).NormalizedDescriptionId.Should().Be(target.Id);
		saved.Single(x => x.Id == ids[0]).NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		saved.Single(x => x.Id == ids[0]).NormalizationMappingId.Should().BeNull();
		saved.Single(x => x.Id == ids[1]).NormalizedDescriptionId.Should().BeNull();
	}

	private async Task ReviewExistingAsync(Guid[] ids, NormalizedDescriptionEntity target)
	{
		await using var db = new EntryActorFactory(fixture.CreateOptions(), Actor).CreateDbContext();
		foreach (var item in await db.ReceiptItems.Where(x => ids.Contains(x.Id)).ToListAsync())
		{
			item.NormalizationIntent = new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(),
				NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision,
				ExpectedItemRevision: item.NormalizationRevision);
		}
		await db.SaveChangesAsync();
	}

	private NormalizationCatalogService Service() => new(new Factory(fixture), TimeProvider.System, publisher.Object);
	private static NormalizationCatalogPreview Request(NormalizedDescriptionEntity target) =>
		new(Guid.NewGuid(), Source, target.Id, target.NormalizationRevision, null, false);

	private async Task StartFreshDatabaseAsync()
	{
		await ReplaceDatabaseAsync();
		await ResetAsync();
		await using var db = fixture.CreateDbContext();
		(await db.ReceiptItems.CountAsync()).Should().Be(0);
		(await db.NormalizedDescriptions.CountAsync()).Should().Be(0);
		(await db.NormalizationSourceMappings.CountAsync()).Should().Be(0);
	}

	private async Task ResetAsync()
	{
		publisher.Setup(x => x.PublishAsync(It.IsAny<CommittedEntityChange>())).Returns(Task.CompletedTask);
		await using var db = fixture.CreateDbContext();
		if (!await db.Users.AnyAsync(x => x.Id == Actor))
		{
			db.Users.Add(new ApplicationUser { Id = Actor, UserName = Actor, NormalizedUserName = Actor.ToUpperInvariant(), SecurityStamp = Guid.NewGuid().ToString(), CreatedAt = DateTimeOffset.UtcNow });
		}
		var role = await db.Roles.FirstOrDefaultAsync(x => x.Name == "Admin");
		if (role is null)
		{
			role = new IdentityRole("Admin") { NormalizedName = "ADMIN" };
			db.Roles.Add(role);
		}
		if (!await db.UserRoles.AnyAsync(x => x.UserId == Actor && x.RoleId == role.Id))
		{
			db.UserRoles.Add(new IdentityUserRole<string> { UserId = Actor, RoleId = role.Id });
		}
		await db.SaveChangesAsync();
	}

	private async Task<NormalizedDescriptionEntity> CanonicalAsync(string name)
	{
		await using var db = fixture.CreateDbContext();
		var entity = new NormalizedDescriptionEntity { Id = Guid.NewGuid(), CanonicalName = name, Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow };
		db.NormalizedDescriptions.Add(entity);
		await db.SaveChangesAsync();
		return entity;
	}

	private async Task<Guid[]> ItemsAsync(NormalizationSourceKey source, int count, string origin = "unassigned", Guid? target = null)
	{
		await using var db = fixture.CreateDbContext();
		var receipt = new ReceiptEntity { Id = Guid.NewGuid(), Location = source.Location, Date = new(2025, 1, 15), TaxAmountCurrency = Currency.USD };
		db.Receipts.Add(receipt);
		var items = Enumerable.Range(0, count).Select(i => new ReceiptItemEntity
		{
			Id = Guid.NewGuid(),
			ReceiptId = receipt.Id,
			Description = source.Description,
			ReceiptItemCode = source.ItemCode,
			Quantity = 1.2345m + i,
			UnitPrice = 2.3456m + i,
			UnitPriceCurrency = Currency.USD,
			TotalAmount = 2.90m + i,
			TotalAmountCurrency = Currency.USD,
			Category = "Food",
			NormalizedDescriptionId = target,
			NormalizationOrigin = origin,
		}).ToArray();
		db.ReceiptItems.AddRange(items);
		await db.SaveChangesAsync();
		return items.Select(x => x.Id).ToArray();
	}

	private async Task<List<ReceiptItemEntity>> ReadAsync(Guid[] ids)
	{
		await using var db = fixture.CreateDbContext();
		return await db.ReceiptItems.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync();
	}

	private async Task<object[]> FactsAsync(Guid[] ids) => (await ReadAsync(ids)).Select(x => (object)new
	{
		x.Id,
		x.ReceiptId,
		x.Description,
		x.ReceiptItemCode,
		x.Quantity,
		x.UnitPrice,
		x.UnitPriceCurrency,
		x.TotalAmount,
		x.TotalAmountCurrency,
		x.Category,
		x.Subcategory,
	}).ToArray();

	private async Task<int> AuditCountAsync(Guid[] ids)
	{
		await using var db = fixture.CreateDbContext();
		var keys = ids.Select(x => x.ToString()).ToArray();
		return await db.AuditLogs.CountAsync(x => x.EntityType == "ReceiptItem" && keys.Contains(x.EntityId));
	}

	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
