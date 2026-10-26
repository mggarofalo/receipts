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
		(string actor, CategoryMappingCommand command, CategoryMappingService service) = await CreateApprovalCommandAsync();
		CategoryMappingRevision approval = await service.ChangeAsync(actor, command);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		string[] applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
		applied[^1].Should().Be("20261026102840_AddApprovedCategoryMappings");
		CategoryMappingRevisionEntity original = await db.CategoryMappingRevisions.AsNoTracking().SingleAsync(row => row.Id == approval.Id);

		Func<Task> downgrade = () => db.GetService<IMigrator>().MigrateAsync(applied[^2]);
		(await downgrade.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("Mapping history is retained");

		(await db.Database.GetAppliedMigrationsAsync()).Should().Equal(applied);
		(await db.CategoryMappingRevisions.AsNoTracking().SingleAsync(row => row.Id == approval.Id)).Should().BeEquivalentTo(original);
		(await db.CategoryMappingHeads.SingleAsync(row => row.SourceFingerprint == command.SourceFingerprint)).RevisionId.Should().Be(approval.Id);
		(await service.ChangeAsync(actor, command)).Should().Be(approval);
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
