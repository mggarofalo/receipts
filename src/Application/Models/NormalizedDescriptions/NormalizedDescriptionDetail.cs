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
// LastSeen is the latest receipt date among the live items linked to this row, or null when
// nothing is linked (RECEIPTS-880). It answers a question CreatedAt cannot: an entry created two
// years ago and still appearing on this week's receipts, and one created two years ago that
// nothing has matched since, look identical without it. Same meaning as
// SpendingByNormalizedDescriptionItem.LastSeen, hence the same name.
//
// LinkedTemplate* names an item template that declares this row (RECEIPTS-930) — the strongest
// evidence a reviewer can be given, because a template is a user having already said by hand that
// this item exists and is called that. It is not derived from the name: it reads the FK
// ItemTemplate gained in RECEIPTS-881, so it says "somebody linked these", never "these look
// alike".
//
// The count exists because more than one template pointing at a row is not an anomaly to be
// papered over: merging two template-backed entries leaves both templates on the survivor, and
// MergeAsync re-points them deliberately. Showing one name and implying it is the only one would
// misreport exactly the case an admin most needs to see.
public record NormalizedDescriptionDetail(
	NormalizedDescription Description,
	int LinkedItemCount,
	string? NearestNeighbourName,
	IReadOnlyList<string> SampleRawDescriptions,
	DateTimeOffset? LastSeen = null,
	Guid? LinkedTemplateId = null,
	string? LinkedTemplateName = null,
	int LinkedTemplateCount = 0);
