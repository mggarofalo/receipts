namespace Application.Models.Reports;

public record ItemDescriptionItem(
	string Description,
	int Occurrences);

public record ItemDescriptionResult(
	List<ItemDescriptionItem> Items);
