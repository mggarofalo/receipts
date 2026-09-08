import type { components } from "@/generated/api";

type NormalizedDescriptionStatus =
  components["schemas"]["NormalizedDescriptionStatus"];

/**
 * Status predicates that retain compatibility with historical response casing.
 *
 * The response contract uses camelCase. Before RECEIPTS-884, some generated response
 * properties overrode that policy and emitted PascalCase names such as `PendingReview`.
 * Comparing case-insensitively keeps historical responses and cached data readable.
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
