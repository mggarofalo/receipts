using Application.Normalization;
using Common;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.NormalizationAi;
using Infrastructure.NormalizationAi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>Durable observation of pending purchases. Assistance state never confers product trust.</summary>
public sealed partial class NormalizationReviewInboxService(
	IDbContextFactory<ApplicationDbContext> factory,
	NormalizationAiService ai,
	IOptions<NormalizationAiOptions> options,
	TimeProvider clock) : INormalizationReviewInboxService
{
	private static NormalizationCatalogException Error(int status, string code, string detail) => new(status, code, detail);
	private async Task AuthorizeAsync(ApplicationDbContext context, string actor, CancellationToken ct)
	{
		DateTimeOffset now = clock.GetUtcNow();
		if (!await context.Users.AnyAsync(user => user.Id == actor && !(user.LockoutEnabled && user.LockoutEnd > now)
			&& context.UserRoles.Any(member => member.UserId == actor && context.Roles.Any(role => role.Id == member.RoleId && role.Name == AppRoles.Admin)), ct))
		{ throw Error(403, "admin_required", "An enabled administrator is required to review purchases."); }
	}
	private bool IsOwner(string actor) => options.Value.Enabled && actor == options.Value.OwnerUserId;
	private static NormalizationAssistancePreferences Preferences(NormalizationAssistancePreferenceEntity? value, bool owner)
		=> new(owner, value?.Enabled ?? false, value?.Model, value?.DailyLimit ?? 10, value?.Revision ?? Guid.Empty);
	public async Task<NormalizationAssistancePreferences> GetPreferencesAsync(string actor, CancellationToken cancellationToken)
	{
		await using var context = await factory.CreateDbContextAsync(cancellationToken);
		await AuthorizeAsync(context, actor, cancellationToken);
		var value = await context.NormalizationAssistancePreferences.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
		return Preferences(value?.OwnerUserId == options.Value.OwnerUserId ? value : null, IsOwner(actor));
	}
	public async Task<NormalizationAssistancePreferences> UpdatePreferencesAsync(string actor, NormalizationAssistancePreferenceUpdate update, CancellationToken cancellationToken)
	{
		NormalizationAssistancePolicy.ValidatePreferences(update);
		await using var context = await factory.CreateDbContextAsync(cancellationToken);
		await using var lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
		await AuthorizeAsync(context, actor, cancellationToken);
		if (!IsOwner(actor)) { throw Error(403, "owner_required", "Only the configured ChatGPT owner can enable post-save suggestions."); }
		var value = await context.NormalizationAssistancePreferences.SingleOrDefaultAsync(cancellationToken);
		Guid current = value?.OwnerUserId == actor ? value.Revision : Guid.Empty;
		if (current != update.ExpectedRevision) { throw Error(409, "preferences_changed", "Assistance settings changed. Refresh before saving."); }
		if (value is null) { value = new(); context.NormalizationAssistancePreferences.Add(value); }
		value.OwnerUserId = actor;
		value.Enabled = update.Enabled;
		value.Model = update.Model?.Trim();
		value.DailyLimit = update.DailyLimit;
		value.Revision = Guid.NewGuid();
		await CancelOutstandingAsync(context, "settings_changed", clock.GetUtcNow(), cancellationToken);
		await context.SaveChangesAsync(cancellationToken);
		await lease.CommitAsync(cancellationToken);
		return Preferences(value, true);
	}
	internal static async Task CancelOutstandingAsync(ApplicationDbContext context, string reason, DateTimeOffset now, CancellationToken ct)
	{
		var attempts = await context.NormalizationAssistanceAttempts.Where(attempt => attempt.RunId == null && (attempt.State == "pending" || attempt.State == "admitting")).ToListAsync(ct);
		foreach (var attempt in attempts) { attempt.State = "paused"; attempt.ErrorCode = reason; attempt.Revision = Guid.NewGuid(); attempt.LeaseExpiresAt = null; }
		Guid[] ids = await context.NormalizationAssistanceAttempts.Where(attempt => attempt.RunId != null).Select(attempt => attempt.RunId!.Value).ToArrayAsync(ct);
		var runs = await context.NormalizationAiRuns.Where(run => ids.Contains(run.Id) && (run.State == "queued" || run.State == "running")).ToListAsync(ct);
		foreach (var run in runs)
		{
			run.State = "cancelled"; run.ErrorCode = reason; run.Revision = Guid.NewGuid();
			run.ClaimToken = null; run.LeaseExpiresAt = null; run.CompletedAt = now;
			run.ErrorDetail = "Post-save assistance settings or connection context changed. Review this source manually or explicitly request suggestions again.";
		}
	}
	public async Task RetryAdmissionAsync(string actor, string sourceHash, Guid expectedRevision, CancellationToken cancellationToken)
	{
		await using var context = await factory.CreateDbContextAsync(cancellationToken);
		await using var lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
		await AuthorizeAsync(context, actor, cancellationToken);
		if (!IsOwner(actor)) { throw Error(403, "owner_required", "Only the configured ChatGPT owner can retry assistance."); }
		var preference = await context.NormalizationAssistancePreferences.SingleOrDefaultAsync(cancellationToken);
		var attempt = await context.NormalizationAssistanceAttempts.SingleOrDefaultAsync(value => value.OwnerUserId == actor && value.SourceHash == sourceHash, cancellationToken);
		if (attempt is null) { throw Error(404, "attempt_not_found", "The assistance attempt was not found."); }
		if (attempt.Revision != expectedRevision || attempt.RunId != null || attempt.State is not ("paused" or "auth_required") || preference is not { Enabled: true } || preference.OwnerUserId != actor)
		{ throw Error(409, "attempt_changed", "Refresh the inbox. Only a paused request that never started inference can be retried here."); }
		attempt.State = "pending"; attempt.Revision = Guid.NewGuid(); attempt.PreferenceRevision = preference.Revision;
		attempt.Model = preference.Model!; attempt.AdmissionAttempts = 0; attempt.NextAttemptAt = clock.GetUtcNow();
		attempt.ErrorCode = null; attempt.ErrorDetail = null;
		await context.SaveChangesAsync(cancellationToken);
		await lease.CommitAsync(cancellationToken);
	}
	public async Task<NormalizationReviewInboxPage> ListAsync(string actor, string? query, int offset, int limit, CancellationToken cancellationToken)
	{
		if (offset < 0 || limit is < 1 or > 100 || query?.Length > 200) { throw Error(400, "invalid_page", "Use a nonnegative offset, page size 1–100 and search up to 200 characters."); }
		await using var context = await factory.CreateDbContextAsync(cancellationToken);
		await AuthorizeAsync(context, actor, cancellationToken);
		var facts = from work in context.NormalizationReviewWork.AsNoTracking()
					join item in context.ReceiptItems.IgnoreAutoIncludes() on work.ItemId equals item.Id
					join receipt in context.Receipts on item.ReceiptId equals receipt.Id
					where item.DeletedAt == null && receipt.DeletedAt == null && item.NormalizationReviewState == NormalizationReviewState.NeedsReview && work.SourceHash != null
					select new { work.SourceHash, work.CreatedAt, ItemId = item.Id, ReceiptId = receipt.Id, receipt.Date, Location = receipt.Location.Trim(' '), Description = item.Description.Trim(' '), Code = item.ReceiptItemCode == null || item.ReceiptItemCode.Trim(' ') == "" ? null : item.ReceiptItemCode.Trim(' ') };
		if (!string.IsNullOrWhiteSpace(query)) { string term = query.Trim().ToLowerInvariant(); facts = facts.Where(row => row.Description.ToLower().Contains(term) || row.Location.ToLower().Contains(term)); }
		var groups = facts.GroupBy(row => new { row.SourceHash, row.Location, row.Description, row.Code }).Select(group => new { group.Key, Count = group.Count(), Receipts = group.Select(row => row.ReceiptId).Distinct().Count(), FirstSeen = group.Min(row => row.CreatedAt) });
		int total = await groups.CountAsync(cancellationToken);
		var page = await groups.OrderBy(row => row.FirstSeen).ThenBy(row => row.Key.SourceHash).Skip(offset).Take(limit).ToListAsync(cancellationToken);
		string[] hashes = page.Select(row => row.Key.SourceHash!).ToArray();
		var samples = await context.NormalizationReviewWork.Where(work => hashes.Contains(work.SourceHash!)).Select(work => work.SourceHash).Distinct()
			.Select(hash => new
			{
				Hash = hash!,
				Receipts = facts.Where(row => row.SourceHash == hash).OrderByDescending(row => row.Date).ThenBy(row => row.ItemId).Take(5)
				.Select(row => new NormalizationReviewReceipt(row.ReceiptId, row.ItemId, row.Date)).ToList()
			}).ToDictionaryAsync(row => row.Hash, row => row.Receipts, cancellationToken);
		var attempts = await context.NormalizationAssistanceAttempts.AsNoTracking().Where(value => hashes.Contains(value.SourceHash) && value.OwnerUserId == options.Value.OwnerUserId).ToDictionaryAsync(value => value.SourceHash, cancellationToken);
		Guid[] runIds = attempts.Values.Where(value => value.RunId != null).Select(value => value.RunId!.Value).ToArray();
		var runs = await context.NormalizationAiRuns.AsNoTracking().Where(run => runIds.Contains(run.Id)).ToDictionaryAsync(run => run.Id, cancellationToken);
		List<NormalizationReviewInboxSource> result = [];
		foreach (var group in page)
		{
			string hash = group.Key.SourceHash!;
			var receipts = samples.GetValueOrDefault(hash) ?? [];
			attempts.TryGetValue(hash, out var attempt);
			var run = attempt?.RunId is Guid id ? runs.GetValueOrDefault(id) : null;
			result.Add(new(hash, new(group.Key.Location, group.Key.Description, group.Key.Code), group.Count, group.Receipts, group.FirstSeen, receipts,
				run?.State ?? attempt?.State ?? "manual", attempt?.Revision, run?.Id, run?.ErrorCode ?? attempt?.ErrorCode, run?.ErrorDetail ?? attempt?.ErrorDetail));
		}
		return new(result, total, offset, limit);
	}
}
