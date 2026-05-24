namespace Application.Models.Ynab;

public record YnabSyncEventsPage(IReadOnlyList<YnabSyncEventDto> Events, int TotalCount);
