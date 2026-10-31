using System.Security.Cryptography;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Taxonomy;
using Common;
using Domain.Taxonomy;
using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public sealed class TaxonomyService(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock, ICommittedChangePublisher? publisher = null) : ITaxonomyService
{
	public async Task<TaxonomyRevision?> ReadAsync(Guid? revisionId, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await factory.CreateDbContextAsync(cancellationToken);
		Guid id = revisionId ?? await context.TaxonomyHeads.Select(head => head.RevisionId).SingleAsync(cancellationToken);
		return await ReadRevisionAsync(context, id, cancellationToken);
	}

	private static async Task<TaxonomyRevision?> ReadRevisionAsync(ApplicationDbContext context, Guid id, CancellationToken ct)
	{
		TaxonomyRevisionEntity? revision = await context.TaxonomyRevisions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
		if (revision == null) { return null; }
		List<TaxonomyNode> nodes = await context.TaxonomyNodes.AsNoTracking().Where(row => row.RevisionId == id).OrderBy(row => row.CategoryId)
			.Select(row => new TaxonomyNode(row.CategoryId, row.ParentId, row.Name, row.Definition, row.Archived)).ToListAsync(ct);
		return new(revision.Id, revision.Sequence, revision.PreviousRevisionId, revision.RecordedAt, revision.Actor,
			revision.Reason, revision.OperationId, nodes, revision.AffectedCount);
	}

	public async Task<TaxonomyRevision> ChangeAsync(string actor, TaxonomyChangeCommand command, CancellationToken cancellationToken = default)
	{
		if (command.OperationId == Guid.Empty || command.ExpectedRevisionId == Guid.Empty || string.IsNullOrWhiteSpace(command.Reason)
			|| command.Changes == null || command.Changes.Count == 0 || command.Changes.Any(edit => edit == null))
		{ throw new TaxonomyException(400, "Expected revision, operation ID, reason and changes are required."); }
		if (command.Reason.Contains('\0'))
		{ throw new TaxonomyException(400, "Reason must not contain NUL characters."); }
		string hash = RequestHash(command);
		await using ApplicationDbContext context = await factory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		// Global hierarchy lock always precedes any future assignment/item locks.
		TaxonomyHeadEntity head = await context.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE").SingleAsync(cancellationToken);
		DateTimeOffset now = clock.GetUtcNow();
		if (!await IsEnabledAdminAsync(context, actor, now, cancellationToken)) { throw new TaxonomyException(403, "An enabled administrator is required to manage taxonomy."); }
		TaxonomyOperationEntity? previous = await context.TaxonomyOperations.AsNoTracking().SingleOrDefaultAsync(row => row.Actor == actor && row.OperationId == command.OperationId, cancellationToken);
		if (previous != null)
		{
			if (previous.RequestHash != hash) { throw new TaxonomyException(409, "Operation ID was already used with a different request."); }
			TaxonomyRevision replay = (await ReadRevisionAsync(context, previous.RevisionId, cancellationToken))!;
			return replay with { AffectedCount = previous.AffectedCount };
		}
		// A restructure moves purchases against one pinned revision; an edit in the middle would strand the rest.
		if (await context.HistoricalOperations.AnyAsync(row => row.Kind == HistoricalOperationKind.RestructureV1
			&& (row.State == HistoricalOperationState.Preview || row.State == HistoricalOperationState.Applying
				|| row.State == HistoricalOperationState.Undoing), cancellationToken))
		{ throw new TaxonomyException(409, "A category restructure is still running. Finish or undo it before changing categories."); }
		(Guid resultId, int affected) = await AppendInTransactionAsync(context, head, actor, command, hash, now, cancellationToken);
		await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		if (affected != 0 && publisher != null)
		{
			await publisher.PublishAsync(new(CommittedEntityType.Taxonomy, CommittedChangeType.Updated));
		}
		return (await ReadRevisionAsync(context, resultId, cancellationToken))! with { AffectedCount = affected };
	}

	/// <summary>
	/// Appends the changed tree as a new revision, advances the locked head and records the operation. The caller
	/// owns the transaction, has locked <paramref name="head"/> and has authorized the actor.
	/// </summary>
	internal static async Task<(Guid RevisionId, int Affected)> AppendInTransactionAsync(ApplicationDbContext context,
		TaxonomyHeadEntity head, string actor, TaxonomyChangeCommand command, string hash, DateTimeOffset now, CancellationToken ct)
	{
		if (head.RevisionId != command.ExpectedRevisionId) { throw new TaxonomyException(409, "Taxonomy changed. Refresh before retrying."); }
		TaxonomyRevision current = (await ReadRevisionAsync(context, head.RevisionId, ct))!;
		IReadOnlyList<TaxonomyNode> nodes;
		try { nodes = TaxonomyTree.Apply(current.Nodes, command.Changes); }
		catch (TaxonomyValidationException error) { throw new TaxonomyException(400, error.Message); }
		Dictionary<Guid, TaxonomyNode> old = current.Nodes.ToDictionary(node => node.Id);
		int affected = nodes.Count(node => !old.TryGetValue(node.Id, out TaxonomyNode? prior) || prior != node);
		Guid resultId = current.Id;
		if (affected != 0)
		{
			resultId = Guid.NewGuid();
			context.TaxonomyRevisions.Add(new()
			{
				Id = resultId,
				Sequence = current.Sequence + 1,
				PreviousRevisionId = current.Id,
				RecordedAt = now,
				Actor = actor,
				Reason = command.Reason,
				OperationId = command.OperationId,
				AffectedCount = affected
			});
			context.TaxonomyIdentities.AddRange(nodes.Where(node => !old.ContainsKey(node.Id)).Select(node => new TaxonomyIdentityEntity { Id = node.Id }));
			context.TaxonomyNodes.AddRange(nodes.Select(node => new TaxonomyNodeEntity
			{
				RevisionId = resultId,
				CategoryId = node.Id,
				ParentId = node.ParentId,
				Name = node.Name,
				Definition = node.Definition,
				Archived = node.Archived
			}));
			head.RevisionId = resultId;
		}
		context.TaxonomyOperations.Add(new()
		{
			Actor = actor,
			OperationId = command.OperationId,
			RequestHash = hash,
			RevisionId = resultId,
			AffectedCount = affected,
			RecordedAt = now,
			Reason = command.Reason
		});
		return (resultId, affected);
	}

	internal static Task<bool> IsEnabledAdminAsync(ApplicationDbContext context, string actor, DateTimeOffset now, CancellationToken ct) =>
		context.Users.AnyAsync(user => user.Id == actor && !(user.LockoutEnabled && user.LockoutEnd > now)
			&& context.UserRoles.Any(member => member.UserId == user.Id && context.Roles.Any(role => role.Id == member.RoleId && role.Name == AppRoles.Admin)), ct);

	internal static string RequestHash(TaxonomyChangeCommand command) =>
		Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));
}
