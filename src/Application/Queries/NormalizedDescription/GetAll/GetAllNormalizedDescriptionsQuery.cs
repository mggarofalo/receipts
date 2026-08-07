using Application.Interfaces;
using Application.Models;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;

namespace Application.Queries.NormalizedDescription.GetAll;

/// <summary>
/// One page of canonical normalized-description rows, optionally filtered by status and search
/// term (RECEIPTS-879).
/// </summary>
/// <remarks>
/// This used to be unpaginated on the theory that the row count stays small. It does not:
/// the count is bounded by the number of unique receipt-item descriptions ever seen, and grocery
/// receipts generate thousands of them. The registry loaded all of them and filtered in the
/// browser, and every merge-dialog open paid for the same full list.
/// </remarks>
public record GetAllNormalizedDescriptionsQuery(
	NormalizedDescriptionStatus? StatusFilter,
	string? Q,
	int Offset,
	int Limit) : IQuery<PagedResult<NormalizedDescriptionDetail>>;
