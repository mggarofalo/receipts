using Application.Normalization;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

public sealed class CategoryMappingServiceTests
{
	private const string Actor = "category-reviewer";
	private static readonly NormalizationSourceKey Source = new("Store A", "BF FRANKS 8CT", "123");
	private readonly IDbContextFactory<ApplicationDbContext> factory = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser().Item1;
	private readonly Guid category = Guid.NewGuid();
	private readonly Guid parent = Guid.NewGuid();
	private Guid taxonomy;
	private CategoryMappingService Service => new(factory, TimeProvider.System);

	private async Task SeedAsync()
	{
		await using ApplicationDbContext db = factory.CreateDbContext();
		await db.Database.EnsureCreatedAsync();
		db.Users.Add(new ApplicationUser { Id = Actor, UserName = Actor });
		TaxonomyHeadEntity head = await db.TaxonomyHeads.SingleAsync();
		taxonomy = head.RevisionId;
		db.TaxonomyNodes.AddRange(new TaxonomyNodeEntity { RevisionId = taxonomy, CategoryId = parent, Name = "Food", Definition = "Food purchases" },
			new TaxonomyNodeEntity { RevisionId = taxonomy, CategoryId = category, ParentId = parent, Name = "Meat", Definition = "Meat products" });
		await db.SaveChangesAsync();
	}
	private CategoryMappingCommand Approve(Guid? expected = null) => new(Source, NormalizationCatalogPolicy.Hash(Source), expected, taxonomy, category, Guid.NewGuid(), "Explicit category review");

	[Fact]
	public async Task OnlyExplicitCategoryApprovalAuthorizesExactSourceReuse()
	{
		await SeedAsync();
		(await Service.ResolveAsync(Source, NormalizationCatalogPolicy.Hash(Source))).Disposition.Should().Be("unmapped");
		CategoryMappingRevision approval = await Service.ChangeAsync(Actor, Approve());
		CategoryMappingResolution actual = await Service.ResolveAsync(Source, approval.SourceFingerprint);
		actual.Disposition.Should().Be("approved");
		actual.CategoryId.Should().Be(category);
		actual.MappingRevisionId.Should().Be(approval.Id);
		foreach (NormalizationSourceKey other in new[] { Source with { Location = "Store B" }, Source with { Description = "BF FRANKS 16CT" },
			Source with { ItemCode = "456" }, Source with { ItemCode = null }, Source with { Description = "bf franks 8ct" }, Source with { Description = "\tBF FRANKS 8CT" } })
		{
			(await Service.ResolveAsync(other, NormalizationCatalogPolicy.Hash(other))).Disposition.Should().Be("unmapped");
			(await Service.ResolveAsync(other, approval.SourceFingerprint)).Disposition.Should().Be("stale_source");
		}
		(await Service.ResolveAsync(Source with { Location = " Store A ", ItemCode = " 123 " }, approval.SourceFingerprint)).Disposition.Should().Be("approved");
		(await Service.ResolveAsync(Source, "outdated", new(null, taxonomy))).Disposition.Should().Be("explicit");
		await using ApplicationDbContext verify = factory.CreateDbContext();
		verify.ReceiptItems.Should().BeEmpty();
		verify.NormalizationSourceMappings.Should().BeEmpty();
	}

	[Theory]
	[InlineData("rename", "approved")]
	[InlineData("unrelated", "approved")]
	[InlineData("move", "stale_category")]
	[InlineData("definition", "stale_category")]
	[InlineData("ancestor_definition", "stale_category")]
	[InlineData("archive", "unavailable_category")]
	[InlineData("archive_ancestor", "unavailable_category")]
	[InlineData("missing", "unavailable_category")]
	public async Task RevalidatesCurrentMeaningWhileRetainingApprovalProvenance(string edit, string disposition)
	{
		await SeedAsync();
		CategoryMappingRevision approval = await Service.ChangeAsync(Actor, Approve());
		Guid next = Guid.NewGuid();
		await using (ApplicationDbContext db = factory.CreateDbContext())
		{
			List<TaxonomyNodeEntity> nodes = await db.TaxonomyNodes.AsNoTracking().Where(node => node.RevisionId == taxonomy).ToListAsync();
			foreach (TaxonomyNodeEntity node in nodes)
			{
				node.RevisionId = next;
				if (node.CategoryId == category)
				{
					if (edit == "rename") { node.Name = "Renamed"; }
					if (edit == "move") { node.ParentId = null; }
					if (edit == "definition") { node.Definition = "Changed meaning"; }
					if (edit == "archive") { node.Archived = true; }
					if (edit == "missing") { continue; }
				}
				if (node.CategoryId == parent && edit == "ancestor_definition") { node.Definition = "Changed ancestor"; }
				if (node.CategoryId == parent && edit == "archive_ancestor") { node.Archived = true; }
				db.TaxonomyNodes.Add(node);
			}
			if (edit == "unrelated") { db.TaxonomyNodes.Add(new() { RevisionId = next, CategoryId = Guid.NewGuid(), Name = "Unrelated category", Definition = "Different branch" }); }
			db.TaxonomyHeads.Single().RevisionId = next;
			await db.SaveChangesAsync();
		}
		CategoryMappingResolution actual = await Service.ResolveAsync(Source, approval.SourceFingerprint);
		actual.Disposition.Should().Be(disposition);
		actual.TaxonomyRevisionId.Should().Be(next);
		actual.ApprovalTaxonomyRevisionId.Should().Be(taxonomy);
		actual.CategoryId.Should().Be(disposition == "approved" ? category : null);
		(await Service.ResolveAsync(Source, approval.SourceFingerprint, new(category, taxonomy))).Disposition.Should().Be("stale_explicit_choice");
		if (disposition == "unavailable_category")
		{
			(await Service.ResolveAsync(Source, approval.SourceFingerprint, new(category, next))).Disposition.Should().Be("unavailable_explicit_choice");
		}
	}

	[Fact]
	public async Task IdempotencyAndExpectedRevisionProtectApprovalAndRevocation()
	{
		await SeedAsync();
		CategoryMappingCommand command = Approve();
		CategoryMappingRevision approved = await Service.ChangeAsync(Actor, command);
		(await Service.ChangeAsync(Actor, command)).Should().Be(approved);
		Func<Task> reused = () => Service.ChangeAsync(Actor, command with { Reason = "Different request" });
		(await reused.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		Func<Task> stale = () => Service.ChangeAsync(Actor, Approve());
		(await stale.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		CategoryMappingRevision revoked = await Service.ChangeAsync(Actor, Approve(approved.Id) with { CategoryId = null });
		(await Service.ResolveAsync(Source, approved.SourceFingerprint)).Disposition.Should().Be("revoked");
		(await Service.ChangeAsync(Actor, command)).Should().Be(approved, "replay returns its original revision even after revocation");
		(await Service.ResolveAsync(Source, "stale", new(category, taxonomy))).Disposition.Should().Be("explicit");
		CategoryMappingRevision renewed = await Service.ChangeAsync(Actor, Approve(revoked.Id));
		renewed.PreviousRevisionId.Should().Be(revoked.Id);
		await using ApplicationDbContext verify = factory.CreateDbContext();
		verify.CategoryMappingRevisions.Count().Should().Be(3);
	}

	[Fact]
	public async Task StaleTaxonomySourceAndUnavailableTargetsCannotBeApproved()
	{
		await SeedAsync();
		foreach (CategoryMappingCommand invalid in new[] { Approve() with { ExpectedTaxonomyRevisionId = Guid.NewGuid() },
			Approve() with { SourceFingerprint = "outdated" }, Approve() with { CategoryId = Guid.NewGuid() } })
		{
			Func<Task> act = () => Service.ChangeAsync(Actor, invalid);
			(await act.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		}
		await using ApplicationDbContext verify = factory.CreateDbContext();
		verify.CategoryMappingRevisions.Should().BeEmpty();
	}
}
