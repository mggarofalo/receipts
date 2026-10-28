using Application.Normalization;
using Application.Taxonomy;
using Common;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class CategoryMappingHistoryIntegrityTests(PostgresFixture fixture)
{
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}

	[Fact]
	public async Task MappingChangesWorkAfterLatestMigrationWithoutAuthorityJournal()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		var test = new CategoryMappingHistoryIntegrityTests(isolated);
		(string actor, CategoryMappingCommand command, CategoryMappingService service) = await test.CreateApprovalCommandAsync();
		CategoryMappingRevision approval = await service.ChangeAsync(actor, command);
		(await service.ResolveAsync(command.Source, command.SourceFingerprint)).CategoryId.Should().Be(command.CategoryId);
		(await service.ChangeAsync(actor, command)).Should().Be(approval);

		await using ApplicationDbContext db = isolated.CreateDbContext();
		(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('taxonomy.\"MappingAuthorityEvents\"') IS NULL AS \"Value\"").SingleAsync()).Should().BeTrue();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RevisionsRejectDirectMutationAndPreserveReplayWhileHeadsCanAdvance(bool delete)
	{
		(string actor, CategoryMappingCommand command, CategoryMappingService service) = await CreateApprovalCommandAsync();
		CategoryMappingRevision approval = await service.ChangeAsync(actor, command);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		CategoryMappingRevisionEntity original = await db.CategoryMappingRevisions.AsNoTracking().SingleAsync(row => row.Id == approval.Id);

		Func<Task> mutate = delete
			? () => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM taxonomy.\"CategoryMappingRevisions\" WHERE \"Id\" = {approval.Id}")
			: () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE taxonomy.\"CategoryMappingRevisions\" SET \"RequestHash\" = 'rewritten', \"Reason\" = 'rewritten' WHERE \"Id\" = {approval.Id}");
		(await mutate.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("immutable");
		(await db.CategoryMappingRevisions.AsNoTracking().SingleAsync(row => row.Id == approval.Id)).Should().BeEquivalentTo(original);
		(await service.ChangeAsync(actor, command)).Should().Be(approval);

		CategoryMappingRevision revocation = await service.ChangeAsync(actor, command with
		{
			ExpectedMappingRevisionId = approval.Id,
			CategoryId = null,
			OperationId = Guid.NewGuid(),
			Reason = "Revoke"
		});
		(await service.ResolveAsync(command.Source, command.SourceFingerprint)).Disposition.Should().Be("revoked");
		CategoryMappingRevision renewed = await service.ChangeAsync(actor, command with
		{
			ExpectedMappingRevisionId = revocation.Id,
			OperationId = Guid.NewGuid(),
			Reason = "Renew"
		});
		renewed.PreviousRevisionId.Should().Be(revocation.Id);
		revocation.PreviousRevisionId.Should().Be(approval.Id);
		(await db.CategoryMappingHeads.SingleAsync(row => row.SourceFingerprint == command.SourceFingerprint)).RevisionId.Should().Be(renewed.Id);
		(await db.CategoryMappingRevisions.CountAsync(row => row.SourceFingerprint == command.SourceFingerprint)).Should().Be(3);
		(await service.ResolveAsync(command.Source, command.SourceFingerprint)).CategoryId.Should().Be(command.CategoryId);
		(await service.ChangeAsync(actor, command)).Should().Be(approval);
		(await db.CategoryMappingRevisions.AsNoTracking().SingleAsync(row => row.Id == approval.Id)).Should().BeEquivalentTo(original);
	}

	[Fact]
	public async Task MigrationDowngradeRefusesToRemoveMappingHistoryAndPreservesReplay()
	{
		await using var isolated = await fixture.CreateIsolatedAsync("20261027110503_RaiseCategoryAssistanceDailyLimit");
		await new CategoryMappingHistoryIntegrityTests(isolated).AssertMappingDowngradeBoundary();
	}

	private async Task AssertMappingDowngradeBoundary()
	{
		(string actor, CategoryMappingCommand command, CategoryMappingService service) = await CreateApprovalCommandAsync();
		CategoryMappingRevision approval = await service.ChangeAsync(actor, command);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		string[] applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
		const string mappingMigration = "20261026102840_AddApprovedCategoryMappings";
		int mappingIndex = Array.IndexOf(applied, mappingMigration);
		mappingIndex.Should().BeGreaterThan(0);
		CategoryMappingRevisionEntity original = await db.CategoryMappingRevisions.AsNoTracking().SingleAsync(row => row.Id == approval.Id);

		try
		{
			Func<Task> downgrade = () => db.GetService<IMigrator>().MigrateAsync(applied[mappingIndex - 1]);
			var refusal = (await downgrade.Should().ThrowAsync<PostgresException>()).Which;
			refusal.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
			refusal.MessageText.Should().Be("Mapping history is retained; restore a verified backup explicitly instead.");

			// Later migrations may commit their Down before this retained-history boundary refuses.
			(await db.Database.GetAppliedMigrationsAsync()).Should().Equal(applied.Take(mappingIndex + 1));
			(await db.CategoryMappingRevisions.AsNoTracking().SingleAsync(row => row.Id == approval.Id)).Should().BeEquivalentTo(original);
			(await db.CategoryMappingHeads.SingleAsync(row => row.SourceFingerprint == command.SourceFingerprint)).RevisionId.Should().Be(approval.Id);
			(await service.ChangeAsync(actor, command)).Should().Be(approval);
		}
		finally
		{
			await db.GetService<IMigrator>().MigrateAsync(applied[^1]);
		}
	}

	private async Task<(string Actor, CategoryMappingCommand Command, CategoryMappingService Service)> CreateApprovalCommandAsync()
	{
		string actor = "mapping-integrity-" + Guid.NewGuid();
		Guid category = Guid.NewGuid();
		await using ApplicationDbContext db = fixture.CreateDbContext();
		Guid taxonomy = await db.TaxonomyHeads.Select(row => row.RevisionId).SingleAsync();
		db.Users.Add(new ApplicationUser { Id = actor, UserName = actor });
		IdentityRole? role = await db.Roles.SingleOrDefaultAsync(row => row.Name == AppRoles.Admin);
		if (role == null)
		{
			role = new IdentityRole(AppRoles.Admin);
			db.Roles.Add(role);
		}
		db.UserRoles.Add(new IdentityUserRole<string> { UserId = actor, RoleId = role.Id });
		await db.SaveChangesAsync();
		TaxonomyService taxonomyService = new(new Factory(fixture), TimeProvider.System);
		taxonomy = (await taxonomyService.ChangeAsync(actor, new(taxonomy, Guid.NewGuid(), "Create integrity test category",
			[new TaxonomyEdit("create", category, Name: "Mapping integrity " + category)]))).Id;
		NormalizationSourceKey source = new("Integrity store", Guid.NewGuid().ToString(), "item");
		return (actor, new(source, NormalizationCatalogPolicy.Hash(source), null, taxonomy, category, Guid.NewGuid(), "Reviewed"),
			new CategoryMappingService(new Factory(fixture), TimeProvider.System));
	}
}
