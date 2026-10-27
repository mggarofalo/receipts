using Application.Interfaces.Services;
using Application.Taxonomy;
using Common;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class CompleteReceiptCutoverTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	private const string ActorId = "receipt-cutover-admin";
	private sealed class Actor : ICurrentUserAccessor
	{
		public string? UserId => ActorId;
		public Guid? ApiKeyId => null;
		public string? IpAddress => null;
		public string? UserAgent => null;
	}
	private sealed class Factory(PostgresFixture database) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(database.CreateOptions(), new Actor());
	}

	private CompleteReceiptWriter Writer(ClassificationOptions? options = null) => new(new Factory(fixture),
		new ReceiptMapper(), new TransactionMapper(), new ReceiptItemMapper(), new AdjustmentMapper(),
		Options.Create(options ?? new ClassificationOptions()));

	private async Task<(Guid Revision, Guid Category)> PrepareAsync(bool existingReceipt)
	{
		await using var db = fixture.CreateDbContext();
		var role = new IdentityRole { Id = Guid.NewGuid().ToString(), Name = AppRoles.Admin };
		db.Users.Add(new ApplicationUser { Id = ActorId, UserName = ActorId });
		db.Roles.Add(role);
		db.UserRoles.Add(new() { UserId = ActorId, RoleId = role.Id });
		if (existingReceipt)
		{
			var receipt = ReceiptEntityGenerator.Generate();
			db.Receipts.Add(receipt);
			db.ReceiptItems.Add(ReceiptItemEntityGenerator.Generate(receipt.Id));
		}
		await db.SaveChangesAsync();
		var migration = new LegacyTaxonomyMigrationService(new Factory(fixture));
		var plan = await migration.PreflightAsync(new("receipt-cutover", Guid.NewGuid(), ActorId, DateTimeOffset.UtcNow));
		await migration.ApplyAsync(LegacyMigrationFreeze.Source(plan));
		var tree = new TaxonomyService(new Factory(fixture), TimeProvider.System);
		var head = (await tree.ReadAsync(null))!;
		Guid category = Guid.NewGuid();
		var revision = await tree.ChangeAsync(ActorId, new(head.Id, Guid.NewGuid(), "Receipt cutover category",
			[new TaxonomyEdit("create", category, Name: "Cutover groceries")]));
		return (revision.Id, category);
	}

	private static (Domain.Core.Receipt Receipt, Domain.Core.ReceiptItem Item) Draft(Guid revision, Guid? category, bool explicitChoice = true)
	{
		var receipt = ReceiptEntityGenerator.Generate();
		var item = new ReceiptItemMapper().ToDomain(ReceiptItemEntityGenerator.Generate(receipt.Id));
		item.Category = explicitChoice ? "Cutover groceries" : "";
		item.Subcategory = null;
		item.Classification = explicitChoice ? new(category, revision, null, Guid.NewGuid(), "Receipt entry category") : null;
		return (new ReceiptMapper().ToDomain(receipt), item);
	}

	// Rehearse a real portable export/restore before treating this deployment as ready.
	private async Task VerifyPortableBackupAsync()
	{
		var factory = new Factory(fixture);
		string path = await new BackupService(factory, NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		try
		{
			await using var target = await fixture.CreateIsolatedAsync();
			await using var stream = File.OpenRead(path);
			await new BackupImportService(new Factory(target), NullLogger<BackupImportService>.Instance)
				.ImportFromSqliteAsync(stream, default);
			await using var sourceDb = fixture.CreateDbContext();
			await using var targetDb = target.CreateDbContext();
			(await targetDb.LegacyMigrations.CountAsync()).Should().Be(await sourceDb.LegacyMigrations.CountAsync());
			(await targetDb.Classifications.Select(x => x.Id).ToListAsync()).Should()
				.BeEquivalentTo(await sourceDb.Classifications.Select(x => x.Id).ToListAsync());
			(await targetDb.CurrentClassifications.Select(x => x.AssignmentId).ToListAsync()).Should()
				.BeEquivalentTo(await sourceDb.CurrentClassifications.Select(x => x.AssignmentId).ToListAsync());
		}
		finally { File.Delete(path); }
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, true)]
	[InlineData(false, false)]
	[InlineData(true, false)]
	public async Task DefaultConfiguration_AfterMigrationAndBackupRehearsal_SavesAndReplaysAfterRestart(bool upgraded, bool explicitChoice)
	{
		var seed = await PrepareAsync(upgraded);
		await VerifyPortableBackupAsync();
		var draft = Draft(seed.Revision, seed.Category, explicitChoice);
		Guid requestId = Guid.NewGuid();
		var saved = await Writer().CreateAsync(draft.Receipt, [], [draft.Item], [], requestId, default);
		// New services and contexts model the application restarting with unchanged configuration.
		var replay = await Writer().CreateAsync(draft.Receipt, [], [draft.Item], [], requestId, default);
		replay.Receipt.Id.Should().Be(saved.Receipt.Id);
		replay.Items.Single().Id.Should().Be(saved.Items.Single().Id);
		await using var db = fixture.CreateDbContext();
		var assignment = await db.Classifications.SingleAsync(x => x.ReceiptItemId == saved.Items.Single().Id);
		assignment.CategoryId.Should().Be(explicitChoice ? seed.Category : null);
		(await db.Receipts.CountAsync()).Should().Be(upgraded ? 2 : 1);
	}

	[Fact]
	public async Task FreshDatabaseWithoutInitialMigration_RejectsExplicitChoiceAtomically()
	{
		await using var db = fixture.CreateDbContext();
		(await db.LegacyMigrations.AnyAsync()).Should().BeFalse("schema migrations do not complete the legacy data cutover");
		Guid revision = (await db.TaxonomyHeads.SingleAsync()).RevisionId;
		var draft = Draft(revision, null);
		await AssertAtomicRejectionAsync(Writer(new() { WritesEnabled = true }), draft, "*completed legacy taxonomy backfill*");
	}
	[Fact]
	public async Task ExplicitOptOut_RejectsCategoryWithoutPersistingAnyReceiptOrRequest()
	{
		var seed = await PrepareAsync(true);
		var draft = Draft(seed.Revision, seed.Category);
		await AssertAtomicRejectionAsync(Writer(new() { WritesEnabled = false }), draft, "*disabled by server configuration*Classification__WritesEnabled*restart the API*");
	}

	[Fact]
	public async Task IncompleteBackfill_RejectsCategoryWithoutPersistingAnyReceiptOrRequest()
	{
		var seed = await PrepareAsync(true);
		await using (var db = fixture.CreateDbContext())
		{
			var receipt = ReceiptEntityGenerator.Generate();
			db.Receipts.Add(receipt);
			db.ReceiptItems.Add(ReceiptItemEntityGenerator.Generate(receipt.Id));
			await db.SaveChangesAsync();
		}
		var draft = Draft(seed.Revision, seed.Category);
		await AssertAtomicRejectionAsync(Writer(new() { WritesEnabled = true }), draft, "*backfill is incomplete*");
	}

	private async Task AssertAtomicRejectionAsync(CompleteReceiptWriter writer,
		(Domain.Core.Receipt Receipt, Domain.Core.ReceiptItem Item) draft, string message)
	{
		await using var db = fixture.CreateDbContext();
		int receipts = await db.Receipts.CountAsync(), items = await db.ReceiptItems.CountAsync();
		int assignments = await db.Classifications.CountAsync(), requests = await db.ReceiptWriteRequests.CountAsync();
		Guid operation = draft.Item.Classification!.OperationId;
		Func<Task> save = async () => { await writer.CreateAsync(draft.Receipt, [], [draft.Item], [], Guid.NewGuid(), default); };
		(await save.Should().ThrowAsync<TaxonomyException>().WithMessage(message)).Which.Status.Should().Be(409);
		(await db.Receipts.CountAsync()).Should().Be(receipts);
		(await db.ReceiptItems.CountAsync()).Should().Be(items);
		(await db.Classifications.CountAsync()).Should().Be(assignments);
		(await db.ReceiptWriteRequests.CountAsync()).Should().Be(requests);
		draft.Item.Classification.OperationId.Should().Be(operation);
	}
}

