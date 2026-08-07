import type { components } from "@/generated/api";

type NormalizedDescriptionStatus =
  components["schemas"]["NormalizedDescriptionStatus"];

/**
 * Status predicates that survive the wire-format casing bug.
 *
 * The spec documents these values lowercase (`active`, `pendingReview`) and the generated
 * TypeScript union says the same, but the API serializes them PascalCase (`Active`,
 * `PendingReview`). NSwag decorates every generated enum property with a property-level
 * `[JsonConverter(typeof(JsonStringEnumConverter<T>))]` built from its parameterless
 * constructor — no naming policy — and a property-level converter outranks the
 * `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` registered globally in
 * ApplicationConfiguration. So the global camelCase policy never gets a say. That is
 * RECEIPTS-884; the same defensive comparison already guards `SimilarItemResponse.source`
 * in LineItemsSection.
 *
 * Comparing case-insensitively here means these predicates keep working either way, so
 * fixing RECEIPTS-884 will not silently invert any of them.
 */
function matches(
  value: NormalizedDescriptionStatus | null | undefined,
  expected: NormalizedDescriptionStatus,
): boolean {
  if (value == null) return false;
  return value.toLowerCase() === expected.toLowerCase();
}

/**
 * True when the resolver grouped these items on its own authority and no reviewer has
 * confirmed it. Callers render such data as provisional rather than settled.
 *
 * Absent status is deliberately not pending: the report's synthetic "(Not Normalized)"
 * bucket has no backing row, and marking it "unreviewed" would invite a reviewer to go
 * looking for a review-queue entry that does not exist.
 */
export function isPendingReview(
  status: NormalizedDescriptionStatus | null | undefined,
): boolean {
  return matches(status, "pendingReview");
}

export function isActive(
  status: NormalizedDescriptionStatus | null | undefined,
): boolean {
  return matches(status, "active");
}
