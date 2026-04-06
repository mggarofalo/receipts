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
}
