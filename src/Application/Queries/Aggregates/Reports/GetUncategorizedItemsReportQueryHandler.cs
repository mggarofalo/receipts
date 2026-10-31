using Application.Classification;
using Application.Interfaces.Services;
using Application.Models.Reports;
using Mediator;

namespace Application.Queries.Aggregates.Reports;

/// <summary>Jev's answers are shown only while assistance could suggest, as on the suggestions endpoints.</summary>
public class GetUncategorizedItemsReportQueryHandler(IReportService reportService,
	ICategoryAssistanceSettingsStore settingsStore, ICategoryAssistanceCapabilitySource capabilitySource)
	: IRequestHandler<GetUncategorizedItemsReportQuery, UncategorizedItemsResult>
{
	public async ValueTask<UncategorizedItemsResult> Handle(GetUncategorizedItemsReportQuery request, CancellationToken cancellationToken)
	{
		bool canSuggest = CategoryAssistancePolicy.CanSuggest(await settingsStore.GetAsync(cancellationToken),
			await capabilitySource.GetAsync(cancellationToken));
		UncategorizedItemsResult result = await reportService.GetUncategorizedItemsAsync(
			request.SortBy,
			request.SortDirection,
			request.Page,
			request.PageSize,
			request.SuggestionFilter,
			canSuggest,
			cancellationToken);
		return result with { SuggestionsAvailable = canSuggest };
	}
}
