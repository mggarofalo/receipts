import { Link } from "react-router";
import type { CatalogSource } from "@/hooks/useNormalizationCatalog";
import { pendingReviewHref } from "./catalog-review-navigation";

export function PendingIdentityRecovery({
  error,
  source,
}: {
  error: unknown;
  source?: CatalogSource;
}) {
  if (!error || typeof error !== "object") return null;
  const problem = error as Record<string, unknown>;
  if (
    problem.normalizedDescriptionStatus !== "pendingReview" ||
    typeof problem.normalizedDescriptionId !== "string"
  )
    return null;
  return (
    <p>
      <Link
        className="underline"
        to={pendingReviewHref(problem.normalizedDescriptionId, source)}
      >
        Review this pending identity
      </Link>
      . Approval is a separate, explicit action.
    </p>
  );
}
