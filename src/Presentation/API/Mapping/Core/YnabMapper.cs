using API.Generated.Dtos;
using Application.Commands.Ynab.PushTransactions;
using Application.Models.Ynab;
using Riok.Mapperly.Abstractions;
using AppYnabMemoSyncOutcome = Application.Models.Ynab.YnabMemoSyncOutcome;
using AppYnabTransactionCandidate = Application.Models.Ynab.YnabTransactionCandidate;
using DtoYnabMemoSyncOutcome = API.Generated.Dtos.YnabMemoSyncOutcome;
using DtoYnabTransactionCandidate = API.Generated.Dtos.YnabTransactionCandidate;

namespace API.Mapping.Core;

[Mapper]
public partial class YnabMapper
{
	[MapperIgnoreTarget(nameof(YnabConnectionStatusResponse.AdditionalProperties))]
	public YnabConnectionStatusResponse ToConnectionStatusResponse(YnabConnectionStatus source)
	{
		return new YnabConnectionStatusResponse
		{
			IsConfigured = source.IsConfigured,
			IsConnected = source.IsConnected,
			LastSuccessfulSyncUtc = source.LastSuccessfulSyncUtc,
		};
	}

	[MapperIgnoreTarget(nameof(YnabBudgetSummary.AdditionalProperties))]
	public partial YnabBudgetSummary ToBudgetSummary(YnabBudget source);

	[MapperIgnoreTarget(nameof(YnabBudgetListResponse.AdditionalProperties))]
	public YnabBudgetListResponse ToBudgetListResponse(List<YnabBudget> budgets)
	{
		return new YnabBudgetListResponse
		{
			Data = budgets.Select(ToBudgetSummary).ToList(),
		};
	}

	[MapperIgnoreTarget(nameof(YnabBudgetSettingsResponse.AdditionalProperties))]
	public YnabBudgetSettingsResponse ToBudgetSettingsResponse(YnabBudgetSelection source)
	{
		return new YnabBudgetSettingsResponse
		{
			SelectedBudgetId = source.SelectedBudgetId,
		};
	}

	[MapperIgnoreTarget(nameof(YnabSyncRecordResponse.AdditionalProperties))]
	public YnabSyncRecordResponse ToSyncRecordResponse(YnabSyncRecordDto source)
	{
		return new YnabSyncRecordResponse
		{
			Id = source.Id,
			LocalTransactionId = source.LocalTransactionId,
			YnabTransactionId = source.YnabTransactionId,
			YnabBudgetId = source.YnabBudgetId,
			YnabAccountId = source.YnabAccountId,
			SyncType = Enum.Parse<YnabSyncRecordResponseSyncType>(source.SyncType.ToString()),
			SyncStatus = Enum.Parse<YnabSyncRecordResponseSyncStatus>(source.SyncStatus.ToString()),
			SyncedAtUtc = source.SyncedAtUtc,
			LastError = source.LastError,
			CreatedAt = source.CreatedAt,
			UpdatedAt = source.UpdatedAt,
		};
	}

	[MapperIgnoreTarget(nameof(YnabAccountSummary.AdditionalProperties))]
	public YnabAccountSummary ToAccountSummary(YnabAccount source)
	{
		return new YnabAccountSummary
		{
			Id = source.Id,
			Name = source.Name,
			Type = source.Type,
			OnBudget = source.OnBudget,
			Closed = source.Closed,
			Balance = source.Balance,
		};
	}

	[MapperIgnoreTarget(nameof(YnabAccountListResponse.AdditionalProperties))]
	public YnabAccountListResponse ToAccountListResponse(List<YnabAccount> accounts)
	{
		return new YnabAccountListResponse
		{
			Data = accounts.Select(ToAccountSummary).ToList(),
		};
	}

	[MapperIgnoreTarget(nameof(YnabAccountMappingResponse.AdditionalProperties))]
	public YnabAccountMappingResponse ToAccountMappingResponse(YnabAccountMappingDto source)
	{
		return new YnabAccountMappingResponse
		{
			Id = source.Id,
			ReceiptsAccountId = source.ReceiptsAccountId,
			YnabAccountId = source.YnabAccountId,
			YnabAccountName = source.YnabAccountName,
			YnabBudgetId = source.YnabBudgetId,
			CreatedAt = source.CreatedAt,
			UpdatedAt = source.UpdatedAt,
		};
	}

	[MapperIgnoreTarget(nameof(YnabAccountMappingListResponse.AdditionalProperties))]
	public YnabAccountMappingListResponse ToAccountMappingListResponse(List<YnabAccountMappingDto> mappings)
	{
		return new YnabAccountMappingListResponse
		{
			Data = mappings.Select(ToAccountMappingResponse).ToList(),
		};
	}

	[MapperIgnoreTarget(nameof(YnabCategorySummary.AdditionalProperties))]
	public YnabCategorySummary ToCategorySummary(YnabCategory source)
	{
		return new YnabCategorySummary
		{
			Id = source.Id,
			Name = source.Name,
			CategoryGroupId = source.CategoryGroupId,
			CategoryGroupName = source.CategoryGroupName,
			Hidden = source.Hidden,
		};
	}

	[MapperIgnoreTarget(nameof(YnabCategoryListResponse.AdditionalProperties))]
	public YnabCategoryListResponse ToCategoryListResponse(List<YnabCategory> categories)
	{
		return new YnabCategoryListResponse
		{
			Data = categories.Select(ToCategorySummary).ToList(),
		};
	}

	[MapperIgnoreTarget(nameof(YnabCategoryMappingResponse.AdditionalProperties))]
	public YnabCategoryMappingResponse ToCategoryMappingResponse(YnabCategoryMappingDto source)
	{
		return new YnabCategoryMappingResponse
		{
			Id = source.Id,
			ReceiptsCategory = source.ReceiptsCategory,
			YnabCategoryId = source.YnabCategoryId,
			YnabCategoryName = source.YnabCategoryName,
			YnabCategoryGroupName = source.YnabCategoryGroupName,
			YnabBudgetId = source.YnabBudgetId,
			CreatedAt = source.CreatedAt,
			UpdatedAt = source.UpdatedAt,
		};
	}

	[MapperIgnoreTarget(nameof(YnabCategoryMappingListResponse.AdditionalProperties))]
	public YnabCategoryMappingListResponse ToCategoryMappingListResponse(List<YnabCategoryMappingDto> mappings)
	{
		return new YnabCategoryMappingListResponse
		{
			Data = mappings.Select(ToCategoryMappingResponse).ToList(),
		};
	}

	[MapperIgnoreTarget(nameof(YnabMemoSyncResultItem.AdditionalProperties))]
	public YnabMemoSyncResultItem ToMemoSyncResultItem(YnabMemoSyncResult source)
	{
		return new YnabMemoSyncResultItem
		{
			LocalTransactionId = source.LocalTransactionId,
			ReceiptId = source.ReceiptId,
			Outcome = Enum.Parse<DtoYnabMemoSyncOutcome>(source.Outcome.ToString()),
			YnabTransactionId = source.YnabTransactionId,
			Error = source.Error,
			AmbiguousCandidates = source.AmbiguousCandidates?.Select(ToTransactionCandidate).ToList(),
		};
	}

	[MapperIgnoreTarget(nameof(DtoYnabTransactionCandidate.AdditionalProperties))]
	public DtoYnabTransactionCandidate ToTransactionCandidate(AppYnabTransactionCandidate source)
	{
		return new DtoYnabTransactionCandidate
		{
			Id = source.Id,
			Date = source.Date,
			Amount = source.Amount,
			Memo = source.Memo,
			PayeeName = source.PayeeName,
			AccountId = source.AccountId,
		};
	}

	[MapperIgnoreTarget(nameof(YnabMemoSyncResponse.AdditionalProperties))]
	public YnabMemoSyncResponse ToMemoSyncResponse(List<YnabMemoSyncResult> results)
	{
		return new YnabMemoSyncResponse
		{
			Results = results.Select(ToMemoSyncResultItem).ToList(),
		};
	}

	[MapperIgnoreTarget(nameof(PushYnabTransactionsResponse.AdditionalProperties))]
	public PushYnabTransactionsResponse ToPushTransactionsResponse(PushYnabTransactionsResult source)
	{
		return new PushYnabTransactionsResponse
		{
			Success = source.Success,
			PushedTransactions = source.PushedTransactions
				.Select(ToPushedTransactionInfo)
				.ToList(),
			UnmappedCategories = source.UnmappedCategories,
			Error = source.Error,
		};
	}

	[MapperIgnoreTarget(nameof(API.Generated.Dtos.PushedTransactionInfo.AdditionalProperties))]
	public API.Generated.Dtos.PushedTransactionInfo ToPushedTransactionInfo(Application.Commands.Ynab.PushTransactions.PushedTransactionInfo source)
	{
		return new API.Generated.Dtos.PushedTransactionInfo
		{
			LocalTransactionId = source.LocalTransactionId,
			YnabTransactionId = source.YnabTransactionId,
			Milliunits = source.Milliunits,
			SubTransactionCount = source.SubTransactionCount,
		};
	}

	[MapperIgnoreTarget(nameof(StaleMappingsResponse.AdditionalProperties))]
	public StaleMappingsResponse ToStaleMappingsResponse(StaleMappingsResult source)
	{
		return new StaleMappingsResponse
		{
			StaleAccountMappingCount = source.StaleAccountMappingCount,
			StaleCategoryMappingCount = source.StaleCategoryMappingCount,
			CurrentBudgetId = source.CurrentBudgetId,
		};
	}

	[MapperIgnoreTarget(nameof(ClearStaleMappingsResponse.AdditionalProperties))]
	public ClearStaleMappingsResponse ToClearStaleMappingsResponse(ClearStaleMappingsResult source)
	{
		return new ClearStaleMappingsResponse
		{
			DeletedAccountMappings = source.DeletedAccountMappings,
			DeletedCategoryMappings = source.DeletedCategoryMappings,
		};
	}

	[MapperIgnoreTarget(nameof(ReceiptYnabSyncStatus.AdditionalProperties))]
	public ReceiptYnabSyncStatus ToReceiptSyncStatus(ReceiptYnabSyncStatusDto source)
	{
		return new ReceiptYnabSyncStatus
		{
			ReceiptId = source.ReceiptId,
			SyncStatus = Enum.Parse<ReceiptYnabSyncStatusValue>(source.SyncStatus.ToString()),
		};
	}

	[MapperIgnoreTarget(nameof(ReceiptYnabSyncStatusListResponse.AdditionalProperties))]
	public ReceiptYnabSyncStatusListResponse ToReceiptSyncStatusListResponse(List<ReceiptYnabSyncStatusDto> statuses)
	{
		return new ReceiptYnabSyncStatusListResponse
		{
			Data = statuses.Select(ToReceiptSyncStatus).ToList(),
		};
	}

	[MapperIgnoreTarget(nameof(BulkPushYnabTransactionsResponse.AdditionalProperties))]
	public BulkPushYnabTransactionsResponse ToBulkPushTransactionsResponse(BulkPushYnabTransactionsResult source)
	{
		return new BulkPushYnabTransactionsResponse
		{
			Results = source.Results.Select(r => new API.Generated.Dtos.ReceiptPushResult
			{
				ReceiptId = r.ReceiptId,
				Result = ToPushTransactionsResponse(r.Result),
			}).ToList(),
		};
	}

	[MapperIgnoreTarget(nameof(API.Generated.Dtos.SplitLine.AdditionalProperties))]
	public API.Generated.Dtos.SplitLine ToSplitLine(Application.Models.Ynab.SplitLine source)
	{
		return new API.Generated.Dtos.SplitLine
		{
			YnabCategoryId = source.YnabCategoryId,
			CategoryName = source.CategoryName,
			Milliunits = source.Milliunits,
		};
	}

	[MapperIgnoreTarget(nameof(API.Generated.Dtos.TransactionSplitComparison.AdditionalProperties))]
	public API.Generated.Dtos.TransactionSplitComparison ToTransactionSplitComparison(
		Application.Models.Ynab.TransactionSplitComparison source)
	{
		return new API.Generated.Dtos.TransactionSplitComparison
		{
			LocalTransactionId = source.LocalTransactionId,
			AccountName = source.AccountName,
			TotalMilliunits = source.TotalMilliunits,
			Expected = source.Expected.Select(ToSplitLine).ToList(),
			Actual = source.Actual?.Select(ToSplitLine).ToList(),
			ActualFetchError = source.ActualFetchError,
			Matches = source.Matches,
		};
	}

	[MapperIgnoreTarget(nameof(ReceiptYnabSplitComparisonResponse.AdditionalProperties))]
	public ReceiptYnabSplitComparisonResponse ToSplitComparisonResponse(
		Application.Models.Ynab.ReceiptYnabSplitComparisonResult source)
	{
		return new ReceiptYnabSplitComparisonResponse
		{
			CanComputeExpected = source.CanComputeExpected,
			ExpectedUnavailableReason = source.ExpectedUnavailableReason,
			UnmappedCategories = source.UnmappedCategories,
			TransactionComparisons = source.TransactionComparisons.Select(ToTransactionSplitComparison).ToList(),
		};
	}

	[MapperIgnoreTarget(nameof(YnabRateLimitStatusResponse.AdditionalProperties))]
	public YnabRateLimitStatusResponse ToRateLimitStatusResponse(YnabRateLimitStatus source)
	{
		return new YnabRateLimitStatusResponse
		{
			RemainingRequests = source.RemainingRequests,
			MaxRequests = source.MaxRequests,
			RequestsUsed = source.RequestsUsed,
			WindowResetAt = source.WindowResetAt,
			OldestRequestAt = source.OldestRequestAt,
		};
	}

	[MapperIgnoreTarget(nameof(YnabSyncEventResponse.AdditionalProperties))]
	public YnabSyncEventResponse ToSyncEventResponse(YnabSyncEventDto source)
	{
		return new YnabSyncEventResponse
		{
			Id = source.Id,
			OccurredAt = source.OccurredAt,
			// Generated enum names follow NSwag's schema-name convention
			// (YnabSyncEventEventType, YnabSyncEventOutcome) regardless of which
			// response/request the property belongs to.
			EventType = Enum.Parse<API.Generated.Dtos.YnabSyncEventEventType>(source.EventType.ToString()),
			Outcome = Enum.Parse<API.Generated.Dtos.YnabSyncEventOutcome>(source.Outcome.ToString()),
			LocalTransactionId = source.LocalTransactionId,
			ReceiptId = source.ReceiptId,
			YnabBudgetId = source.YnabBudgetId,
			YnabTransactionId = source.YnabTransactionId,
			ErrorMessage = source.ErrorMessage,
		};
	}

	[MapperIgnoreTarget(nameof(YnabSyncEventsResponse.AdditionalProperties))]
	public YnabSyncEventsResponse ToSyncEventsResponse(YnabSyncEventsPage source)
	{
		return new YnabSyncEventsResponse
		{
			Data = source.Events.Select(ToSyncEventResponse).ToList(),
			TotalCount = source.TotalCount,
		};
	}

	[MapperIgnoreTarget(nameof(YnabStatusResponse.AdditionalProperties))]
	public YnabStatusResponse ToStatusResponse(YnabStatusResult source)
	{
		return new YnabStatusResponse
		{
			IsConfigured = source.IsConfigured,
			IsConnected = source.IsConnected,
			SelectedBudgetId = source.SelectedBudgetId,
			LastSuccessUtc = source.LastSuccessUtc,
			LastFailureUtc = source.LastFailureUtc,
			Pushes24h = source.Pushes24h,
			Successes24h = source.Successes24h,
			Failures24h = source.Failures24h,
			Pushes7d = source.Pushes7d,
			Successes7d = source.Successes7d,
			Failures7d = source.Failures7d,
			Pushes30d = source.Pushes30d,
			Successes30d = source.Successes30d,
			Failures30d = source.Failures30d,
			RateLimit = ToRateLimitStatusResponse(source.RateLimit),
		};
	}
}
