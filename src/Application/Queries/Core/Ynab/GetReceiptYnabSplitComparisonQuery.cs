using Application.Models.Ynab;
using Mediator;

namespace Application.Queries.Core.Ynab;

public enum YnabComparisonCategoryView { AsClassified, CurrentHierarchy }

public record GetReceiptYnabSplitComparisonQuery(
	Guid ReceiptId,
	YnabComparisonCategoryView CategoryView = YnabComparisonCategoryView.AsClassified)
	: IRequest<ReceiptYnabSplitComparisonResult>;
