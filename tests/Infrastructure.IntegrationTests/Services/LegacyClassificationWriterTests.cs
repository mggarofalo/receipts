using Application.Interfaces.Services;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class LegacyClassificationWriterTests : IClassFixture<PostgresFixture>
{
	private readonly PostgresFixture fixture;
	public LegacyClassificationWriterTests(PostgresFixture fixture) => this.fixture = fixture;
	private sealed class Actor : ICurrentUserAccessor
	{
		public string? UserId => "legacy-writer";
		public Guid? ApiKeyId => null;
		public string? IpAddress => null;
		public string? UserAgent => null;
	}
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => new(fixture.CreateOptions(), new Actor()); }

	[Fact]
	public async Task ManualLegacySavesBeforeAndAfterBackfillAreAtomicAndDoNotRequireProvidersOrExplicitFlag()
	{
		await using var fixture = await this.fixture.CreateIsolatedAsync("20261030134404_AddEnvelopes");
		var factory = new Factory(fixture);
		var repository = new ReceiptItemRepository(factory);
		var classifications = new ClassificationService(factory, TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = true }));
		Guid receiptId = Guid.NewGuid();
		await using var db = fixture.CreateDbContext();
		db.Receipts.Add(new ReceiptEntity { Id = receiptId, Location = "Manual writer", Date = new(2020, 1, 1) });
		await db.SaveChangesAsync();
		ReceiptItemEntity New(string category, string? child = null) => new()
		{ Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Manual item", Category = category, Subcategory = child, Quantity = 1, UnitPrice = 2, TotalAmount = 2 };
		var original = New("Food", "Dairy");
		await repository.CreateAsync([original], default);
		(await db.Classifications.CountAsync()).Should().Be(0);
		Guid genesis = (await db.TaxonomyHeads.SingleAsync()).RevisionId;
		Func<Task> premature = () => classifications.AssignAsync("legacy-writer", original.Id, new(null, genesis, null, Guid.NewGuid(), "Premature"));
		(await premature.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		var migration = new LegacyTaxonomyMigrationService(factory);
		var plan = await migration.PreflightAsync(new("writer-test", Guid.NewGuid(), "legacy-writer", DateTimeOffset.UtcNow));
		await migration.ApplyAsync(LegacyMigrationFreeze.Source(plan));
		var first = (await classifications.HistoryAsync(original.Id, null, 10)).Items.Single();
		original.Description = "Fact edit";
		await repository.UpdateAsync([original], default);
		(await classifications.HistoryAsync(original.Id, null, 10)).Items.Single().Id.Should().Be(first.Id);

		var resolved = New("Food", "Dairy");
		var unresolved = New("  unknown  ", "");
		Guid requestId = Guid.NewGuid();
		await repository.CreateAsync([resolved, unresolved], [], requestId, default);
		await repository.CreateAsync([resolved, unresolved], [], requestId, default);
		var resolvedHistory = (await classifications.HistoryAsync(resolved.Id, null, 10)).Items.Single();
		resolvedHistory.CategoryId.Should().Be(first.CategoryId);
		resolvedHistory.Source.Should().Be("legacy_write");
		resolvedHistory.LegacyResolution.Should().Be("resolved_path");
		var unknown = (await classifications.HistoryAsync(unresolved.Id, null, 10)).Items.Single();
		unknown.CategoryId.Should().BeNull(); unknown.LegacyResolution.Should().Be("unresolved_path");
		unknown.LegacyCategory.Should().Be("  unknown  "); unknown.LegacySubcategory.Should().Be("");
		resolved.Subcategory = null;
		await repository.UpdateAsync([resolved], default);
		var broad = (await classifications.HistoryAsync(resolved.Id, null, 10)).Items.First();
		broad.Sequence.Should().Be(2); broad.Path.Should().ContainSingle();
		resolved.Category = "unmatched"; resolved.Description = "Must roll back";
		Func<Task> ambiguous = () => repository.UpdateAsync([resolved], default);
		(await ambiguous.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		(await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == resolved.Id)).Description.Should().Be("Manual item");
		(await classifications.HistoryAsync(resolved.Id, null, 10)).Items.Should().HaveCount(2);
		var receiptMapper = new Infrastructure.Mapping.ReceiptMapper();
		var itemMapper = new Infrastructure.Mapping.ReceiptItemMapper();
		var complete = new CompleteReceiptWriter(factory, receiptMapper, new Infrastructure.Mapping.TransactionMapper(), itemMapper, new Infrastructure.Mapping.AdjustmentMapper());
		var completeReceipt = receiptMapper.ToDomain(new ReceiptEntity { Id = Guid.NewGuid(), Location = "No provider complete save", Date = new(2020, 1, 1) });
		var completeItem = itemMapper.ToDomain(New("Food", "Dairy"));
		Guid completeRequest = Guid.NewGuid();
		var saved = await complete.CreateAsync(completeReceipt, [], [completeItem], [], completeRequest, default);
		var replay = await complete.CreateAsync(completeReceipt, [], [completeItem], [], completeRequest, default);
		replay.Receipt.Id.Should().Be(saved.Receipt.Id);
		var completeHistory = (await classifications.HistoryAsync(saved.Items.Single().Id, null, 10)).Items.Single();
		completeHistory.Source.Should().Be("legacy_write"); completeHistory.CategoryId.Should().Be(first.CategoryId);
		(await migration.ReadStatusAsync()).Backfilled.Should().BeTrue();
		db.Database.HasPendingModelChanges().Should().BeFalse();

		// An unrelated direct insert invalidates explicit admission, including when in trash.
		var orphan = New("Food"); db.ReceiptItems.Add(orphan); await db.SaveChangesAsync();
		await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"DeletedAt\" = {DateTimeOffset.UtcNow} WHERE \"Id\" = {orphan.Id}");
		Func<Task> gap = () => classifications.AssignAsync("legacy-writer", original.Id, new(null, first.TaxonomyRevisionId, first.Id, Guid.NewGuid(), "Cannot cross gap"));
		(await gap.Should().ThrowAsync<TaxonomyException>()).Which.Message.Should().Contain("missing classification evidence");

		// A downgrade must fail at the retained-history guard before dropping legacy-write evidence.
		string[] applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
		const string legacyEvidenceMigration = "20261025191938_AddLegacyWriteClassificationEvidence";
		applied.Should().Contain(legacyEvidenceMigration);
		applied.Should().Equal(db.Database.GetMigrations().TakeWhile(x => string.CompareOrdinal(x, "20261030134404_AddEnvelopes") <= 0));
		int legacyEvidenceIndex = Array.IndexOf(applied, legacyEvidenceMigration);
		legacyEvidenceIndex.Should().BeGreaterThan(0);
		int mappingIndex = Array.IndexOf(applied, "20261026102840_AddApprovedCategoryMappings");
		mappingIndex.Should().BeGreaterThan(legacyEvidenceIndex);
		try
		{
			Func<Task> downgrade = () => db.GetService<IMigrator>().MigrateAsync(applied[legacyEvidenceIndex - 1]);
			var refusal = (await downgrade.Should().ThrowAsync<PostgresException>()).Which;
			refusal.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
			refusal.MessageText.Should().Be("Mapping history is retained; restore a verified backup explicitly instead.");
			// Later migrations may commit their Down before the mapping-history guard refuses.
			(await db.Database.GetAppliedMigrationsAsync()).Should().Equal(applied.Take(mappingIndex + 1));
			var retained = await db.Classifications.AsNoTracking().SingleAsync(x => x.Id == unknown.Id);
			retained.LegacyCategory.Should().Be("  unknown  ");
			retained.LegacySubcategory.Should().Be("");
			retained.LegacyResolution.Should().Be("unresolved_path");
			(await classifications.HistoryAsync(unresolved.Id, null, 10)).Items.Single().Should().BeEquivalentTo(unknown);
		}
		finally
		{
			await db.GetService<IMigrator>().MigrateAsync(applied[^1]);
		}
	}
}
