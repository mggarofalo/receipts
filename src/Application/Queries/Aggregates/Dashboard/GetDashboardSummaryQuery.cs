using Application.Interfaces;
using Application.Models.Dashboard;
using Application.Models.Reports;

namespace Application.Queries.Aggregates.Dashboard;

public record GetDashboardSummaryQuery(DateOnly StartDate, DateOnly EndDate, CategoryReportView CategoryView) : IQuery<DashboardSummaryResult>;
