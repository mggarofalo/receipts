export function itemsPhrase(count: number) {
  return count === 1 ? "1 receipt item" : `${count} receipt items`;
}
export /**
 * Per-action consequence copy (RECEIPTS-874).
 *
 * Each names what actually changes, and the count is folded in where an action moves data — the
 * difference between "Merge re-points the items" and "Merge re-points 47 items" is the difference
 * between a rule and a decision.
 */
function approveHint(linkedItemCount: number) {
  return `Accept this as a real entry. Nothing is re-linked or moved; its ${itemsPhrase(linkedItemCount)} stay where they are, and their spending stops being reported as unreviewed.`;
}
export function mergeHint(linkedItemCount: number) {
  return `Say this is the same thing as another entry. Its ${itemsPhrase(linkedItemCount)} are re-pointed at the entry you pick and this one is deleted. Cannot be undone.`;
}
export function rejectHint(linkedItemCount: number) {
  return `Say this text does not deserve an entry. Its ${itemsPhrase(linkedItemCount)} become unnormalized, and the resolver will not propose this text again.`;
}
export function linkTemplateHint(linkedItemCount: number) {
  return `Say this is an item you already have a template for. Its ${itemsPhrase(linkedItemCount)} are re-pointed at that template's entry and this one is deleted, unless it already is that entry.`;
}
