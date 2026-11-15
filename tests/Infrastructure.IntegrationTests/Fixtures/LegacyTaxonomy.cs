using System.Security.Cryptography;
using Application.Taxonomy;
using Domain.Taxonomy;
using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Fixtures;

/// <summary>
/// Appends categories to the head revision without the editor, which refuses a third level (RECEIPTS-1119).
/// Deployments recorded before that limit can still hold deeper trees, so tests of that data seed it here.
/// </summary>
public static class LegacyTaxonomy
{
	public static async Task<TaxonomyRevision> AppendAsync(PostgresFixture fixture, string actor, params TaxonomyNode[] added)
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		await using var transaction = await db.Database.BeginTransactionAsync();
		TaxonomyHeadEntity head = await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE").SingleAsync();
		TaxonomyRevisionEntity current = await db.TaxonomyRevisions.AsNoTracking().SingleAsync(row => row.Id == head.RevisionId);
		List<TaxonomyNode> nodes = await db.TaxonomyNodes.AsNoTracking().Where(row => row.RevisionId == current.Id)
			.Select(row => new TaxonomyNode(row.CategoryId, row.ParentId, row.Name, row.Definition, row.Archived)).ToListAsync();
		nodes.AddRange(added);
		TaxonomyRevisionEntity revision = new()
		{
			Id = Guid.NewGuid(),
			Sequence = current.Sequence + 1,
			PreviousRevisionId = current.Id,
			RecordedAt = DateTimeOffset.UtcNow,
			Actor = actor,
			Reason = "Legacy tree recorded before the two-level limit",
			OperationId = Guid.NewGuid(),
			AffectedCount = added.Length
		};
		db.TaxonomyRevisions.Add(revision);
		db.TaxonomyIdentities.AddRange(added.Select(node => new TaxonomyIdentityEntity { Id = node.Id }));
		db.TaxonomyNodes.AddRange(nodes.Select(node => new TaxonomyNodeEntity
		{
			RevisionId = revision.Id,
			CategoryId = node.Id,
			ParentId = node.ParentId,
			Name = node.Name,
			Definition = node.Definition,
			Archived = node.Archived
		}));
		// A portable export requires every revision's operation, as TaxonomyService records it.
		db.TaxonomyOperations.Add(new()
		{
			Actor = actor,
			OperationId = revision.OperationId,
			RequestHash = Convert.ToHexString(SHA256.HashData(revision.OperationId.ToByteArray())),
			RevisionId = revision.Id,
			AffectedCount = revision.AffectedCount,
			RecordedAt = revision.RecordedAt,
			Reason = revision.Reason
		});
		head.RevisionId = revision.Id;
		await db.SaveChangesAsync();
		await transaction.CommitAsync();
		return new(revision.Id, revision.Sequence, revision.PreviousRevisionId, revision.RecordedAt, revision.Actor,
			revision.Reason, revision.OperationId, nodes.OrderBy(node => node.Id).ToArray(), revision.AffectedCount);
	}
}
