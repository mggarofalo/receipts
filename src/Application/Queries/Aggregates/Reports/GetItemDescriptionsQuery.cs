using Application.Interfaces;
using Application.Models.Reports;

namespace Application.Queries.Aggregates.Reports;

public record GetItemDescriptionsQuery(
	string Search,
	int Limit) : IQuery<ItemDescriptionResult>;
