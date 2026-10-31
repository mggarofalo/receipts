using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Normalization;
using Common;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.NormalizationAi;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed partial class NormalizationCatalogService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	TimeProvider timeProvider,
	ICommittedChangePublisher publisher,
	IEmbeddingService? embeddingService = null,
	ILogger<NormalizationCatalogService>? logger = null) : INormalizationCatalogService
{
	private static NormalizationCatalogException Error(int status, string code, string detail) => new(status, code, detail);
	private async Task AuthorizeAsync(ApplicationDbContext context, string actor, CancellationToken ct)
	{
		DateTimeOffset now = timeProvider.GetUtcNow();
		if (!await context.Users.AnyAsync(user => user.Id == actor && !(user.LockoutEnabled && user.LockoutEnd > now)
			&& context.UserRoles.Any(member => member.UserId == user.Id
				&& context.Roles.Any(role => role.Id == member.RoleId && role.Name == AppRoles.Admin)), ct))
		{ throw Error(403, "admin_required", "An enabled administrator is required to manage the normalization catalog."); }
	}
	private static IQueryable<ReceiptItemEntity> Live(ApplicationDbContext context)
		=> context.ReceiptItems.IgnoreAutoIncludes().Where(item => item.DeletedAt == null
			&& context.Receipts.Any(receipt => receipt.Id == item.ReceiptId && receipt.DeletedAt == null));
	internal static IQueryable<ReceiptItemEntity> SourceItems(ApplicationDbContext context, NormalizationSourceKey source)
		=> Live(context).Where(item => item.Description.Trim(' ') == source.Description
			&& (item.ReceiptItemCode == null || item.ReceiptItemCode.Trim(' ') == "" ? null : item.ReceiptItemCode.Trim(' ')) == source.ItemCode
			&& context.Receipts.Any(receipt => receipt.Id == item.ReceiptId && receipt.Location.Trim(' ') == source.Location));
	private static NormalizationSourceKey Key(NormalizationCatalogOperationEntity operation)
		=> new(operation.Location, operation.Description, operation.ItemCode);
	private static NormalizationSourceKey Key(NormalizationSourceMappingEntity mapping)
		=> new(mapping.Location, mapping.Description, mapping.ItemCode);
	private static void RequireRevision(NormalizationCatalogOperationEntity operation, Guid revision)
	{
		if (operation.Revision != revision) { throw Error(409, "operation_changed", "The operation changed. Refresh before continuing."); }
	}
	internal static async Task<CatalogAssignmentDecision> ReadDecisionAsync(ApplicationDbContext context, ReceiptItemEntity item, bool includeLegacy, CancellationToken ct)
		=> (await ReadDecisionsAsync(context, [item.Id], includeLegacy, ct)).GetValueOrDefault(item.Id)
			?? NormalizationCatalogPolicy.Classify(new(item.NormalizationOrigin, false, false, false, false, false, false), includeLegacy);

	private static async Task<Dictionary<Guid, CatalogAssignmentDecision>> ReadDecisionsAsync(ApplicationDbContext context, Guid[] ids, bool includeLegacy, CancellationToken ct)
	{
		var rows = await context.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().Where(current => ids.Contains(current.Id))
			.Select(current => new
			{
				current.Id,
				Facts = new CatalogAssignmentFacts(current.NormalizationOrigin, current.DeletedAt == null,
				context.Receipts.Any(receipt => receipt.Id == current.ReceiptId && receipt.DeletedAt == null),
				context.NormalizedDescriptions.Any(target => target.Id == current.NormalizedDescriptionId && target.Status == NormalizedDescriptionStatus.Rejected),
				context.NormalizedDescriptions.Any(target => target.Status == NormalizedDescriptionStatus.Rejected && target.CanonicalName.ToLower() == current.Description.Trim(' ').ToLower()),
				context.ItemTemplates.Any(template => template.DeletedAt == null && template.NormalizedDescriptionId != null && template.NormalizedDescriptionId == current.NormalizedDescriptionId),
				context.NormalizationSourceMappings.Any(mapping => mapping.Id == current.NormalizationMappingId && mapping.State == "revoked"
					&& current.NormalizationAssignmentId != null && mapping.TargetId == current.NormalizedDescriptionId),
				current.NormalizationReviewState != NormalizationReviewState.NeedsReview, current.NormalizedDescriptionId != null)
			}).ToListAsync(ct);
		return rows.ToDictionary(row => row.Id, row => NormalizationCatalogPolicy.Classify(row.Facts, includeLegacy));
	}

	public async Task<NormalizationSourcePage> ListSourcesAsync(string actor, string? query, string disposition, int offset, int limit, CancellationToken cancellationToken)
	{
		if (offset < 0 || limit is < 1 or > 200 || disposition is not ("all" or "needs-review" or "mapped" or "protected" or "excluded"))
		{ throw Error(400, "invalid_page", "Choose a valid disposition, nonnegative offset and page size from 1 to 200."); }
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await AuthorizeAsync(context, actor, cancellationToken);
		var facts = from item in Live(context).AsNoTracking()
					join receipt in context.Receipts on item.ReceiptId equals receipt.Id
					where item.Description.Trim(' ') != ""
					select new
					{
						Location = receipt.Location.Trim(' '),
						Description = item.Description.Trim(' '),
						Code = item.ReceiptItemCode == null || item.ReceiptItemCode.Trim(' ') == "" ? null : item.ReceiptItemCode.Trim(' '),
						NormalizationOrigin = item.NormalizationReviewState == NormalizationReviewState.NeedsReview
							? item.NormalizedDescriptionId != null ? "legacy_unknown" : item.NormalizationOrigin == "automatic" ? "automatic" : "unassigned"
							: item.NormalizationReviewState == NormalizationReviewState.Excluded ? "excluded" : item.NormalizationOrigin,
						Blocked = (item.NormalizationReviewState != NormalizationReviewState.NeedsReview
							&& item.NormalizationReviewState != NormalizationReviewState.Excluded
							&& item.NormalizationOrigin != "mapped" && item.NormalizationOrigin != "excluded")
							|| context.NormalizedDescriptions.Any(target => target.Status == NormalizedDescriptionStatus.Rejected
								&& (target.Id == item.NormalizedDescriptionId || target.CanonicalName.ToLower() == item.Description.Trim(' ').ToLower())),
						Revisable = (item.NormalizationOrigin == "mapped" || item.NormalizationOrigin == "excluded")
							&& item.NormalizationAssignmentId != null && context.NormalizationSourceMappings.Any(mapping => mapping.Id == item.NormalizationMappingId
								&& mapping.State == "revoked" && mapping.TargetId == item.NormalizedDescriptionId)
					};
		if (!string.IsNullOrWhiteSpace(query)) { string term = query.Trim().ToLowerInvariant(); facts = facts.Where(row => row.Description.ToLower().Contains(term)); }
		var groups = facts.GroupBy(row => new { row.Location, row.Description, row.Code }).Select(group => new
		{
			group.Key.Location,
			group.Key.Description,
			group.Key.Code,
			Total = group.Count(),
			Unassigned = group.Count(row => row.NormalizationOrigin == "unassigned" && !row.Blocked && !row.Revisable),
			Legacy = group.Count(row => row.NormalizationOrigin == "legacy_unknown" && !row.Blocked && !row.Revisable),
			Automatic = group.Count(row => row.NormalizationOrigin == "automatic" && !row.Blocked && !row.Revisable),
			Mapped = group.Count(row => row.NormalizationOrigin == "mapped" && !row.Revisable && !row.Blocked),
			Excluded = group.Count(row => row.NormalizationOrigin == "excluded" && !row.Revisable && !row.Blocked),
			Revisable = group.Count(row => row.Revisable && !row.Blocked),
			Protected = group.Count(row => row.Blocked)
		});
		groups = disposition switch
		{
			"needs-review" => groups.Where(group => group.Unassigned + group.Legacy + group.Automatic + group.Revisable > 0),
			"mapped" => groups.Where(group => group.Mapped > 0),
			"protected" => groups.Where(group => group.Protected > 0),
			"excluded" => groups.Where(group => group.Excluded > 0),
			_ => groups
		};
		int total = await groups.CountAsync(cancellationToken);
		var liveItems = Live(context);
		var projectedPage = await groups.OrderBy(group => group.Description).ThenBy(group => group.Location).ThenBy(group => group.Code)
			.Skip(offset).Take(limit).Select(group => new
			{
				Group = group,
				LinkedTargetCount = liveItems.Where(item => item.Description.Trim(' ') == group.Description
					&& (item.ReceiptItemCode == null || item.ReceiptItemCode.Trim(' ') == "" ? null : item.ReceiptItemCode.Trim(' ')) == group.Code
					&& context.Receipts.Any(receipt => receipt.Id == item.ReceiptId && receipt.Location.Trim(' ') == group.Location)
					&& item.NormalizedDescriptionId != null).Select(item => item.NormalizedDescriptionId).Distinct().Count(),
				LinkedTargets = context.NormalizedDescriptions.Where(target => liveItems.Any(item => item.NormalizedDescriptionId == target.Id
					&& item.Description.Trim(' ') == group.Description
					&& (item.ReceiptItemCode == null || item.ReceiptItemCode.Trim(' ') == "" ? null : item.ReceiptItemCode.Trim(' ')) == group.Code
					&& context.Receipts.Any(receipt => receipt.Id == item.ReceiptId && receipt.Location.Trim(' ') == group.Location)))
					.OrderBy(target => target.DisplayLabel ?? target.CanonicalName).ThenBy(target => target.Id).Take(5)
					.Select(target => new NormalizationLinkedTarget(target.Id, target.DisplayLabel ?? target.CanonicalName, target.Status)).ToList()
			}).ToListAsync(cancellationToken);
		var page = projectedPage.Select(row => row.Group).ToArray();
		string[] hashes = page.Select(row => NormalizationCatalogPolicy.Hash(new(row.Location, row.Description, row.Code))).ToArray();
		var mappings = await context.NormalizationSourceMappings.Where(mapping => mapping.State == "active" && hashes.Contains(mapping.SourceHash))
			.Select(mapping => new { mapping.SourceHash, mapping.Id }).ToDictionaryAsync(mapping => mapping.SourceHash, mapping => mapping.Id, cancellationToken);
		List<NormalizationSourceGroup> result = [];
		foreach (var projected in projectedPage)
		{
			var row = projected.Group;
			NormalizationSourceKey source = new(row.Location, row.Description, row.Code);
			string hash = NormalizationCatalogPolicy.Hash(source);
			Guid? mapping = mappings.TryGetValue(hash, out Guid mappingId) ? mappingId : null;
			result.Add(new(source, row.Total, row.Unassigned, row.Legacy, row.Automatic, row.Protected + row.Mapped + row.Excluded, mapping, row.Revisable, projected.LinkedTargets, projected.LinkedTargetCount));
		}
		return new(result, total);
	}

	public async Task<NormalizationCatalogCoverage> GetCoverageAsync(string actor, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await AuthorizeAsync(context, actor, cancellationToken);
		var facts = Live(context).Select(item => new
		{
			Origin = item.NormalizationReviewState == NormalizationReviewState.Excluded ? "excluded" : item.NormalizationOrigin,
			Reviewed = item.NormalizationReviewState != NormalizationReviewState.NeedsReview,
			Linked = item.NormalizedDescriptionId != null,
			Rejected = context.NormalizedDescriptions.Any(target => target.Status == NormalizedDescriptionStatus.Rejected
				&& (target.Id == item.NormalizedDescriptionId || target.CanonicalName.ToLower() == item.Description.Trim(' ').ToLower())),
			Revisable = (item.NormalizationOrigin == "mapped" || item.NormalizationOrigin == "excluded")
				&& item.NormalizationAssignmentId != null && context.NormalizationSourceMappings.Any(mapping => mapping.Id == item.NormalizationMappingId
					&& mapping.State == "revoked" && mapping.TargetId == item.NormalizedDescriptionId)
		});
		var counts = await facts.Select(item => new
		{
			Origin = item.Rejected ? "protected" : item.Revisable ? "revisable"
				: !item.Reviewed ? item.Linked ? "legacy_unknown" : item.Origin == "automatic" ? "automatic" : "unassigned"
				: item.Origin == "mapped" || item.Origin == "excluded" ? item.Origin : "reviewed"
		}).GroupBy(item => item.Origin).Select(group => new { Origin = group.Key, Count = group.Count() })
			.ToDictionaryAsync(row => row.Origin, row => row.Count, cancellationToken);
		var live = Live(context);
		int verifiedItems = await live.CountAsync(item => item.NormalizationReviewState == NormalizationReviewState.Verified, cancellationToken);
		int excludedItems = await live.CountAsync(item => item.NormalizationReviewState == NormalizationReviewState.Excluded, cancellationToken);
		var pending = live.Where(item => item.NormalizationReviewState == NormalizationReviewState.NeedsReview);
		int pendingItems = await pending.CountAsync(cancellationToken);
		int pendingSources = await (from item in pending
									join receipt in context.Receipts on item.ReceiptId equals receipt.Id
									select new { Location = receipt.Location.Trim(' '), Description = item.Description.Trim(' '), Code = item.ReceiptItemCode == null || item.ReceiptItemCode.Trim(' ') == "" ? null : item.ReceiptItemCode.Trim(' ') }).Distinct().CountAsync(cancellationToken);
		DateTimeOffset? oldest = await (from work in context.NormalizationReviewWork
										join item in pending on work.ItemId equals item.Id
										select (DateTimeOffset?)work.CreatedAt).MinAsync(cancellationToken);
		return new(counts.Values.Sum(), counts.GetValueOrDefault("unassigned"), counts.GetValueOrDefault("legacy_unknown"),
			counts.GetValueOrDefault("automatic"), counts.GetValueOrDefault("reviewed"), counts.GetValueOrDefault("mapped"),
			counts.GetValueOrDefault("protected"), counts.GetValueOrDefault("excluded"),
			counts.GetValueOrDefault("revisable"), verifiedItems, pendingItems, pendingSources, oldest, excludedItems);
	}

	public async Task<NormalizationCatalogOperation> PreviewAsync(string actor, NormalizationCatalogPreview request, CancellationToken cancellationToken)
	{
		NormalizationSourceKey source = NormalizationCatalogPolicy.Normalize(request.Source);
		string? name = request.NewCanonicalName?.Trim();
		if (request.ClientRequestId == Guid.Empty || (request.Exclude
			? request.TargetId.HasValue || !string.IsNullOrEmpty(name)
			: request.TargetId.HasValue == !string.IsNullOrEmpty(name))
			|| name?.Length > 200 || name?.Any(char.IsControl) == true)
		{ throw Error(400, "invalid_decision", "Choose an existing target, a new name, or explicitly exclude this source."); }
		string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request with { Source = source, NewCanonicalName = name }))));
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
		await AuthorizeAsync(context, actor, cancellationToken);
		NormalizationCatalogOperationEntity? previous = await context.NormalizationCatalogOperations.SingleOrDefaultAsync(value => value.Actor == actor && value.ClientRequestId == request.ClientRequestId, cancellationToken);
		if (previous is not null)
		{
			if (previous.RequestHash != hash) { throw Error(409, "request_conflict", "This request ID was used with a different decision."); }
			return await ModelAsync(context, previous, cancellationToken);
		}
		NormalizationCatalogOperationEntity operation = new()
		{
			Id = Guid.NewGuid(),
			Actor = actor,
			ClientRequestId = request.ClientRequestId,
			RequestHash = hash,
			Location = source.Location,
			Description = source.Description,
			ItemCode = source.ItemCode,
			TargetId = request.TargetId,
			TargetRevision = request.TargetRevision,
			NewCanonicalName = name,
			IncludeLegacyUnknown = request.IncludeLegacyUnknown,
			Exclude = request.Exclude,
			CreatedAt = timeProvider.GetUtcNow()
		};
		operation.SuggestionRunId = request.SuggestionRunId;
		operation.SuggestionProposalId = request.SuggestionProposalId;
		operation.SuggestionRunRevision = request.SuggestionRunRevision;
		await ValidateSuggestionAsync(context, actor, operation, cancellationToken);
		await ValidateTargetAsync(context, operation, cancellationToken);
		context.NormalizationCatalogOperations.Add(operation);
		await context.SaveChangesAsync(cancellationToken);
		Guid? after = null;
		while (true)
		{
			var query = SourceItems(context, source).AsNoTracking();
			if (after.HasValue) { Guid cursor = after.Value; query = query.Where(item => item.Id.CompareTo(cursor) > 0); }
			List<ReceiptItemEntity> batch = await query.OrderBy(item => item.Id).Take(NormalizationCatalogPolicy.ChunkSize).ToListAsync(cancellationToken);
			if (batch.Count == 0) { break; }
			Guid[] receiptIds = batch.Select(item => item.ReceiptId).Distinct().ToArray();
			var receiptRevisions = await context.Receipts.Where(receipt => receiptIds.Contains(receipt.Id))
				.ToDictionaryAsync(receipt => receipt.Id, receipt => receipt.NormalizationRevision, cancellationToken);
			var decisions = await ReadDecisionsAsync(context, batch.Select(item => item.Id).ToArray(), operation.IncludeLegacyUnknown, cancellationToken);
			foreach (ReceiptItemEntity item in batch)
			{
				Guid receiptRevision = receiptRevisions[item.ReceiptId];
				bool eligible = decisions[item.Id].Eligible;
				context.NormalizationCatalogMembers.Add(new()
				{
					Id = Guid.NewGuid(),
					OperationId = operation.Id,
					ItemId = item.Id,
					ReceiptId = item.ReceiptId,
					SourceRevision = item.NormalizationRevision,
					ReceiptRevision = receiptRevision,
					PreviousTargetId = item.NormalizedDescriptionId,
					PreviousOrigin = item.NormalizationOrigin,
					PreviousMappingId = item.NormalizationMappingId,
					PreviousAssignmentId = item.NormalizationAssignmentId,
					State = eligible ? "pending" : "protected"
				});
			}
			await context.SaveChangesAsync(cancellationToken);
			foreach (var entry in context.ChangeTracker.Entries<NormalizationCatalogMemberEntity>().ToArray()) { entry.State = EntityState.Detached; }
			after = batch[^1].Id;
		}
		await lease.CommitAsync(cancellationToken);
		return await ModelAsync(context, operation, cancellationToken);
	}

	private static async Task ValidateTargetAsync(ApplicationDbContext context, NormalizationCatalogOperationEntity operation, CancellationToken ct)
	{
		if (operation.Exclude) { return; }
		if (operation.TargetId is Guid id)
		{
			if (!await context.NormalizedDescriptions.AnyAsync(target => target.Id == id && target.Status == NormalizedDescriptionStatus.Active
				&& target.NormalizationRevision == operation.TargetRevision, ct))
			{ throw Error(409, "target_changed", "Choose a current active target and its revision."); }
		}
		else
		{
			string name = operation.NewCanonicalName!.ToLowerInvariant();
			var existing = await context.NormalizedDescriptions.Where(target => target.CanonicalName.ToLower() == name
				|| (target.DisplayLabel ?? target.CanonicalName).ToLower() == name).OrderBy(target => target.Id).FirstOrDefaultAsync(ct);
			if (existing is not null)
			{
				bool pending = existing.Status == NormalizedDescriptionStatus.PendingReview;
				throw new NormalizationCatalogException(409, pending ? "pending_identity_requires_review" : "name_exists",
					pending ? "This name belongs to an identity awaiting review. Review and explicitly approve it before choosing it for a mapping."
						: "This name already exists. Choose its active identity, or review its current status.",
					new Dictionary<string, object?>
					{
						["normalizedDescriptionId"] = existing.Id,
						["normalizedDescriptionStatus"] = pending ? "pendingReview" : existing.Status == NormalizedDescriptionStatus.Active ? "active" : "rejected"
					});
			}
		}
	}

	private static async Task ValidateSuggestionAsync(ApplicationDbContext context, string actor, NormalizationCatalogOperationEntity operation, CancellationToken ct)
	{
		int references = (operation.SuggestionRunId.HasValue ? 1 : 0) + (operation.SuggestionProposalId.HasValue ? 1 : 0)
			+ (operation.SuggestionRunRevision.HasValue ? 1 : 0);
		if (references == 0) { return; }
		if (references != 3) { throw Error(400, "invalid_suggestion", "Supply all three suggestion references together."); }
		var run = await context.NormalizationAiRuns.Include(run => run.Proposals).SingleOrDefaultAsync(run => run.Id == operation.SuggestionRunId, ct);
		var proposal = run?.Proposals.SingleOrDefault(proposal => proposal.Id == operation.SuggestionProposalId);
		if (proposal?.LabelOnly == true) { throw Error(409, "label_review_required", "This suggestion is only for a global display label; it cannot approve item identity mappings."); }
		if (run is null || run.OwnerUserId != actor || run.State != "ready" || run.Revision != operation.SuggestionRunRevision
			|| run.PromptVersion != Application.NormalizationAi.NormalizationCatalogAiPolicy.Version || proposal is null || proposal.State != "pending"
			|| proposal.SourceLocation != operation.Location || proposal.Description != operation.Description || proposal.SourceItemCode != operation.ItemCode)
		{ throw Error(409, "suggestion_changed", "The source suggestion changed. Refresh it before preparing another preview."); }
		// The user may deliberately override the proposed target; approval records the reviewed decision.
	}

	private static async Task<NormalizationCatalogOperation> ModelAsync(ApplicationDbContext context, NormalizationCatalogOperationEntity operation, CancellationToken ct)
	{
		var counts = await context.NormalizationCatalogMembers.Where(member => member.OperationId == operation.Id).GroupBy(member => member.State)
			.Select(group => new { State = group.Key, Count = group.Count() }).ToDictionaryAsync(row => row.State, row => row.Count, ct);
		string? targetName = operation.TargetId is Guid targetId
			? await context.NormalizedDescriptions.Where(target => target.Id == targetId).Select(target => target.DisplayLabel ?? target.CanonicalName).SingleOrDefaultAsync(ct)
			: operation.NewCanonicalName;
		int total = counts.Values.Sum();
		return new(operation.Id, operation.Revision, operation.State, Key(operation), operation.MappingId, operation.TargetId,
			operation.NewCanonicalName, operation.IncludeLegacyUnknown, total, total - counts.GetValueOrDefault("protected"),
			counts.GetValueOrDefault("protected"), counts.GetValueOrDefault("applied"), counts.GetValueOrDefault("conflicted"),
			counts.GetValueOrDefault("pending"), counts.GetValueOrDefault("undone"), operation.CreatedAt, targetName, operation.Exclude,
			operation.ErrorCode, operation.ErrorDetail);
	}
	private static async Task<NormalizationSourceMapping> MappingModelAsync(ApplicationDbContext context, NormalizationSourceMappingEntity mapping, CancellationToken ct)
		=> new(mapping.Id, mapping.Revision, Key(mapping), mapping.TargetId,
			mapping.TargetId is Guid id ? await context.NormalizedDescriptions.Where(target => target.Id == id).Select(target => target.DisplayLabel ?? target.CanonicalName).SingleOrDefaultAsync(ct) : null,
			mapping.State, mapping.ApprovedAt);

	public async Task<NormalizationCatalogOperation?> GetOperationAsync(string actor, Guid id, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await AuthorizeAsync(context, actor, cancellationToken);
		var operation = await context.NormalizationCatalogOperations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
		return operation is null ? null : await ModelAsync(context, operation, cancellationToken);
	}
	public async Task<IReadOnlyList<NormalizationCatalogOperation>> ListOperationsAsync(string actor, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await AuthorizeAsync(context, actor, cancellationToken);
		var operations = await context.NormalizationCatalogOperations.AsNoTracking().OrderByDescending(value => value.CreatedAt).Take(50).ToListAsync(cancellationToken);
		List<NormalizationCatalogOperation> result = [];
		foreach (var operation in operations) { result.Add(await ModelAsync(context, operation, cancellationToken)); }
		return result;
	}
	public async Task<IReadOnlyList<NormalizationSourceMapping>> ListMappingsAsync(string actor, NormalizationSourceKey source, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await AuthorizeAsync(context, actor, cancellationToken);
		string hash = NormalizationCatalogPolicy.Hash(source);
		var mappings = await context.NormalizationSourceMappings.Where(mapping => mapping.SourceHash == hash).OrderByDescending(mapping => mapping.ApprovedAt).ToListAsync(cancellationToken);
		List<NormalizationSourceMapping> result = [];
		foreach (var mapping in mappings) { result.Add(await MappingModelAsync(context, mapping, cancellationToken)); }
		return result;
	}
}
