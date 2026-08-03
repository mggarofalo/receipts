using Domain.NormalizedDescriptions;

namespace Application.Models.NormalizedDescriptions;

// A canonical row plus the evidence an admin needs to act on it (RECEIPTS-873). The Review Queue
// asks for Approve / Merge / Split decisions; before this existed it offered only a name, a status
// and a date, which is no basis for any of the three.
//
// Everything here except the neighbour's name is either persisted on the row itself or aggregated
// from the ReceiptItems pointing at it — nothing is recomputed from embeddings at read time.
//
// NearestNeighbourName resolves the FK carried on Description.NearestNeighbourId. It is null when
// no near-miss was recorded (the row was auto-accepted, created outright, or predates RECEIPTS-873)
// or when the neighbour has since been merged away and the FK was nulled out. Callers must render
// that absence as "no comparison recorded" rather than a zero score — see
// Description.NearestNeighbourSimilarity for the matching rationale.
public record NormalizedDescriptionDetail(
	NormalizedDescription Description,
	int LinkedItemCount,
	string? NearestNeighbourName,
	IReadOnlyList<string> SampleRawDescriptions);
