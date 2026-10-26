using Application.Models.Reports;

namespace Application.Models.Search;

public record PurchaseSearchResult(IReadOnlyList<PurchaseSearchItem> Items, int Total, int Offset, int Limit)
{
	public CategoryReportView CategoryView { get; init; } = CategoryReportView.AsClassified;
	public Guid? CurrentRevisionId { get; init; }
	public DateTimeOffset? KnowledgeCutoff { get; init; }
	public CategoryReportAssignmentSelection AssignmentSelection { get; init; } = CategoryReportAssignmentSelection.CurrentPointer;
}
