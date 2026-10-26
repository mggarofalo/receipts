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

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class CategoryMappingConcurrencyTests(PostgresFixture fixture)
{
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}

	[Fact]
	public async Task CompetingApprovalsSerializeAndReplayOriginalEvidenceAfterRevocation()
	{
		string actor = "mapping-concurrency-" + Guid.NewGuid();
		Guid category = Guid.NewGuid();
		Guid taxonomy;
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			taxonomy = await db.TaxonomyHeads.Select(row => row.RevisionId).SingleAsync();
			db.Users.Add(new ApplicationUser { Id = actor, UserName = actor });
			IdentityRole? role = await db.Roles.SingleOrDefaultAsync(row => row.Name == AppRoles.Admin);
			if (role == null) { role = new IdentityRole(AppRoles.Admin); db.Roles.Add(role); }
			db.UserRoles.Add(new IdentityUserRole<string> { UserId = actor, RoleId = role.Id });
			await db.SaveChangesAsync();
		}
		TaxonomyService taxonomyService = new(new Factory(fixture), TimeProvider.System);
		taxonomy = (await taxonomyService.ChangeAsync(actor, new(taxonomy, Guid.NewGuid(), "Create mapping test category",
			new[] { new TaxonomyEdit("create", category, Name: "Mapping test " + category) }))).Id;
		NormalizationSourceKey source = new("Concurrent mapping store", Guid.NewGuid().ToString(), null);
		CategoryMappingCommand command = new(source, NormalizationCatalogPolicy.Hash(source), null, taxonomy, category, Guid.NewGuid(), "Reviewed");
		CategoryMappingService service = new(new Factory(fixture), TimeProvider.System);
		async Task<(CategoryMappingRevision? Revision, int? Status)> Attempt(CategoryMappingCommand request)
		{
			try { return (await service.ChangeAsync(actor, request), null); }
			catch (TaxonomyException error) { return (null, error.Status); }
		}
		var results = await Task.WhenAll(Attempt(command), Attempt(command with { OperationId = Guid.NewGuid() }));
		CategoryMappingRevision winner = results.Single(result => result.Revision != null).Revision!;
		results.Single(result => result.Status != null).Status.Should().Be(409);
		CategoryMappingCommand winningCommand = command with { OperationId = winner.OperationId };
		var replays = await Task.WhenAll(service.ChangeAsync(actor, winningCommand), service.ChangeAsync(actor, winningCommand));
		replays.Should().OnlyContain(replay => replay == winner);
		await service.ChangeAsync(actor, command with { CategoryId = null, ExpectedMappingRevisionId = winner.Id, OperationId = Guid.NewGuid() });
		(await service.ChangeAsync(actor, winningCommand)).Should().Be(winner);
		(await service.ResolveAsync(source, command.SourceFingerprint)).Disposition.Should().Be("revoked");
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await verify.CategoryMappingRevisions.CountAsync(row => row.SourceFingerprint == command.SourceFingerprint)).Should().Be(2);
	}
}
