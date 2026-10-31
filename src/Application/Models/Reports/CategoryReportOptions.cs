namespace Application.Models.Reports;

public sealed record CategoryReportOptions(
	CategoryReportView View = CategoryReportView.AsClassified,
	CategoryReportRollup Rollup = CategoryReportRollup.AssignedCategory,
	Guid? CategoryId = null,
	DateTimeOffset? KnowledgeCutoff = null,
	Guid? CapturedCurrentRevisionId = null,
	// Budget filters narrow the purchases before categorizing; category trends applies them.
	Guid? EnvelopeId = null,
	Guid? OccasionId = null);
