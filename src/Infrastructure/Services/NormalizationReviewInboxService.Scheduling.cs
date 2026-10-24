using Application.Normalization;
using Application.NormalizationAi;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.NormalizationAi;
using Infrastructure.NormalizationAi;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public sealed partial class NormalizationReviewInboxService
{
	/// <summary>Repairs lost notifications from database facts, without enrolling untouched historical purchases.</summary>
	public async Task ReconcileAsync(CancellationToken cancellationToken)
	{
		await using var context = await factory.CreateDbContextAsync(cancellationToken);
		await using var lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
		await context.NormalizationReviewWork.Where(work => !context.ReceiptItems.Any(item => item.Id == work.ItemId && item.DeletedAt == null
			&& item.NormalizationReviewState == NormalizationReviewState.NeedsReview && context.Receipts.Any(receipt => receipt.Id == item.ReceiptId && receipt.DeletedAt == null)))
			.ExecuteDeleteAsync(cancellationToken);
		var rows = await (from item in context.ReceiptItems.IgnoreAutoIncludes()
						  join receipt in context.Receipts on item.ReceiptId equals receipt.Id
						  where item.DeletedAt == null && receipt.DeletedAt == null && item.NormalizationReviewState == NormalizationReviewState.NeedsReview
							  && ((item.NormalizationSourceHash != null && !context.NormalizationReviewWork.Any(work => work.ItemId == item.Id))
								  || context.NormalizationReviewWork.Any(work => work.ItemId == item.Id && (work.SourceHash == null || work.SourceRevision != item.NormalizationRevision)))
						  orderby item.Id
						  select new { item.Id, item.ReceiptId, item.NormalizationRevision, item.Description, item.ReceiptItemCode, receipt.Location }).Take(500).ToListAsync(cancellationToken);
		Guid[] ids = rows.Select(row => row.Id).ToArray();
		var markers = await context.NormalizationReviewWork.Where(work => ids.Contains(work.ItemId)).ToDictionaryAsync(work => work.ItemId, cancellationToken);
		DateTimeOffset now = clock.GetUtcNow();
		foreach (var row in rows)
		{
			string hash = NormalizationCatalogPolicy.Hash(NormalizationCatalogPolicy.Normalize(new(row.Location, row.Description, row.ReceiptItemCode)));
			if (!markers.TryGetValue(row.Id, out var work)) { work = new() { ItemId = row.Id, CreatedAt = now }; context.NormalizationReviewWork.Add(work); }
			if (work.SourceHash != null && work.SourceHash != hash) { work.CreatedAt = now; }
			work.SourceHash = hash; work.ReceiptId = row.ReceiptId; work.SourceRevision = row.NormalizationRevision; work.UpdatedAt = now;
		}
		var preferences = await context.NormalizationAssistancePreferences.SingleOrDefaultAsync(cancellationToken);
		if (preferences is { Enabled: true } && (!options.Value.Enabled || preferences.OwnerUserId != options.Value.OwnerUserId))
		{
			preferences.Enabled = false; preferences.Revision = Guid.NewGuid();
			await CancelOutstandingAsync(context, "owner_changed", clock.GetUtcNow(), cancellationToken);
		}
		await context.SaveChangesAsync(cancellationToken);
		await lease.CommitAsync(cancellationToken);
	}

	/// <summary>Admits at most one source per cycle. All inference remains in the existing durable AI worker.</summary>
	public async Task<bool> ScheduleNextAsync(CancellationToken cancellationToken)
	{
		NormalizationAssistanceAttemptEntity? selected;
		await using (var context = await factory.CreateDbContextAsync(cancellationToken))
		await using (var lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken))
		{
			var preference = await context.NormalizationAssistancePreferences.SingleOrDefaultAsync(cancellationToken);
			if (preference is not { Enabled: true } || !IsOwner(preference.OwnerUserId)) { return false; }
			await AuthorizeAsync(context, preference.OwnerUserId, cancellationToken);
			DateTimeOffset now = clock.GetUtcNow();
			selected = await context.NormalizationAssistanceAttempts.Where(value => value.OwnerUserId == preference.OwnerUserId && value.RunId == null
				&& ((value.State == "pending" && value.NextAttemptAt <= now) || (value.State == "admitting" && value.LeaseExpiresAt <= now)))
				.OrderBy(value => value.CreatedAt).FirstOrDefaultAsync(cancellationToken);
			if (selected is null)
			{
				DateTimeOffset day = new(now.UtcDateTime.Date, TimeSpan.Zero);
				if (await context.NormalizationAssistanceAttempts.CountAsync(value => value.OwnerUserId == preference.OwnerUserId && value.CreatedAt >= day, cancellationToken) >= preference.DailyLimit) { return false; }
				var source = await (from work in context.NormalizationReviewWork
									join item in context.ReceiptItems.IgnoreAutoIncludes() on work.ItemId equals item.Id
									join receipt in context.Receipts on item.ReceiptId equals receipt.Id
									where work.SourceHash != null && work.SourceRevision == item.NormalizationRevision && item.DeletedAt == null && receipt.DeletedAt == null
										&& item.NormalizationReviewState == NormalizationReviewState.NeedsReview
										&& !context.NormalizationAssistanceAttempts.Any(value => value.OwnerUserId == preference.OwnerUserId && value.SourceHash == work.SourceHash)
									orderby work.CreatedAt, work.ItemId
									select new { work.SourceHash, receipt.Location, item.Description, item.ReceiptItemCode }).FirstOrDefaultAsync(cancellationToken);
				if (source is null) { return false; }
				var key = NormalizationCatalogPolicy.Normalize(new(source.Location, source.Description, source.ReceiptItemCode));
				selected = new()
				{
					OwnerUserId = preference.OwnerUserId,
					SourceHash = source.SourceHash!,
					Location = key.Location,
					Description = key.Description,
					ItemCode = key.ItemCode,
					PreferenceRevision = preference.Revision,
					Model = preference.Model!,
					CreatedAt = now,
					NextAttemptAt = now
				};
				context.NormalizationAssistanceAttempts.Add(selected);
			}
			if (selected.AdmissionAttempts >= NormalizationAssistancePolicy.MaximumAdmissionAttempts) { selected.State = "paused"; selected.ErrorCode = "admission_limit"; }
			else if (selected.PreferenceRevision != preference.Revision) { selected.State = "paused"; selected.ErrorCode = "settings_changed"; }
			else { selected.State = "admitting"; selected.AdmissionAttempts++; selected.LeaseExpiresAt = now.AddMinutes(5); }
			selected.Revision = Guid.NewGuid();
			await context.SaveChangesAsync(cancellationToken);
			await lease.CommitAsync(cancellationToken);
		}
		if (selected.State != "admitting") { return true; }
		try { await ai.StartAutomaticSourceAsync(selected, cancellationToken); }
		catch (NormalizationAiException exception) { await AdmissionFailedAsync(selected, exception.Code, exception.Message, cancellationToken); }
		catch (NormalizationCatalogException exception) { await AdmissionFailedAsync(selected, exception.Code, exception.Message, cancellationToken); }
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (Exception) { await AdmissionFailedAsync(selected, "admission_unavailable", "Assistance could not be admitted. Your saved purchases remain available for manual review.", cancellationToken); }
		// Unexpected transport/process errors leave a persisted lease and stable request ID. No inference is retried here.
		return true;
	}
	private async Task AdmissionFailedAsync(NormalizationAssistanceAttemptEntity claim, string code, string detail, CancellationToken ct)
	{
		await using var context = await factory.CreateDbContextAsync(ct);
		await using var lease = await NormalizationAiStoreLock.AcquireAsync(context, ct);
		var attempt = await context.NormalizationAssistanceAttempts.SingleAsync(value => value.Id == claim.Id, ct);
		if (attempt.Revision != claim.Revision || attempt.RunId != null) { return; }
		attempt.State = code is "not_connected" or "not_configured" or "connection_changed" ? "auth_required"
			: code is "source_changed" or "source_reserved" or "invalid_model" || attempt.AdmissionAttempts >= NormalizationAssistancePolicy.MaximumAdmissionAttempts ? "paused" : "pending";
		attempt.ErrorCode = code; attempt.ErrorDetail = detail.Length > 1000 ? detail[..1000] : detail;
		attempt.NextAttemptAt = clock.GetUtcNow() + NormalizationAssistancePolicy.AdmissionBackoff(attempt.AdmissionAttempts);
		attempt.LeaseExpiresAt = null; attempt.Revision = Guid.NewGuid();
		await context.SaveChangesAsync(ct);
		await lease.CommitAsync(ct);
	}
}
