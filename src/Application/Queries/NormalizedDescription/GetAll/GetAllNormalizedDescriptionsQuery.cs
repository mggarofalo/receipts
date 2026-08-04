using Application.Interfaces;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;

namespace Application.Queries.NormalizedDescription.GetAll;

// Lists canonical normalized-description rows, optionally filtered to a single status.
// Unlike the Core entity list queries, this isn't paginated — the row count is bounded by
// the number of unique receipt-item descriptions ever seen, which stays small enough that
// the admin screen can render the full set without server-side paging for the foreseeable
// future. When pressure grows we can add offset/limit/sort in a follow-up without changing
// the existing URL shape.
public record GetAllNormalizedDescriptionsQuery(NormalizedDescriptionStatus? StatusFilter) : IQuery<List<NormalizedDescriptionDetail>>;
