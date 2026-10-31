import type { CategorySuggestion } from "@/components/categories/CategoryPathPicker";
import type { components } from "@/generated/api";

type Candidates = Pick<
  components["schemas"]["CategoryAssistanceSuggestionRead"],
  "leafCandidates" | "rootCandidates"
>;

/**
 * Jev's ranked alternatives as picker suggestions: its stage-2 item types
 * first, being the more specific answers, then its stage-1 groups.
 */
export function assistanceSuggestions(
  candidates: Candidates,
): CategorySuggestion[] {
  return [...candidates.leafCandidates, ...candidates.rootCandidates].map(
    (candidate) => ({
      categoryId: candidate.categoryId,
      confidence: candidate.probability,
      reason: "Jev",
    }),
  );
}
