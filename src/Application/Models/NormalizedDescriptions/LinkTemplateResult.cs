namespace Application.Models.NormalizedDescriptions;

// What happened when a reviewer said "this row is template X's item" (RECEIPTS-930).
//
// Survivor is the template's canonical entry — the row named after the template — which is not
// necessarily the row the caller pointed at. That is the whole reason Merged exists: the caller
// asked about one row and may get another one back, and a client that assumed otherwise would
// show the wrong name and leave a deleted row on screen.
//
// ItemsRelinkedCount carries the same meaning as MergeAsync's return value: live receipt items
// moved. Zero when Merged is false, because nothing moved, and legitimately zero when Merged is
// true and the consolidated row held nothing.
public record LinkTemplateResult(
	NormalizedDescriptionDetail Survivor,
	int ItemsRelinkedCount,
	bool Merged);
