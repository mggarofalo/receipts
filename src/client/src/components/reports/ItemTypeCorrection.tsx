import { useMemo } from "react";
import { Button } from "@/components/ui/button";
import { CategoryPathPicker } from "@/components/categories/CategoryPathPicker";
import { assistanceSuggestions } from "@/lib/assistance-candidates";
import {
  categoryPaths,
  type CategoryChoice,
  type CategoryPath,
  type CategoryRevision,
} from "@/lib/category-paths";
import type { components } from "@/generated/api";

type Suggestion = components["schemas"]["UncategorizedItemSuggestion"];

// One path index per taxonomy revision, shared by every row of the report.
const pathIndexes = new WeakMap<
  CategoryRevision["nodes"],
  Map<string, CategoryPath>
>();

/** A choice of a category in the current tree, or undefined when it is not selectable there. */
function currentChoice(
  revision: CategoryRevision | undefined,
  categoryId: string | null | undefined,
): CategoryChoice | undefined {
  if (!revision || !categoryId) return undefined;
  let index = pathIndexes.get(revision.nodes);
  if (!index) {
    // categoryPaths leaves out archived categories and their descendants.
    index = new Map(
      categoryPaths(revision.nodes).map((row) => [row.node.id, row]),
    );
    pathIndexes.set(revision.nodes, index);
  }
  const row = index.get(categoryId);
  return row
    ? { categoryId, taxonomyRevisionId: revision.id, path: row.path }
    : undefined;
}

/**
 * Corrects one item in place: Jev's answer, when it still names a live item
 * type, is one tap away; the picker offers Jev's alternatives first.
 */
export function ItemTypeCorrection({
  description,
  suggestion,
  revision,
  loading,
  error,
  busy,
  onChoose,
}: {
  description: string;
  suggestion?: Suggestion | null;
  revision?: CategoryRevision;
  loading?: boolean;
  error?: boolean;
  busy?: boolean;
  /** `fromSuggestion` is true when the choice is Jev's own answer. */
  onChoose: (choice: CategoryChoice, fromSuggestion: boolean) => void;
}) {
  const proposed = currentChoice(revision, suggestion?.categoryId);
  const suggestions = useMemo(
    () => (suggestion ? assistanceSuggestions(suggestion) : undefined),
    [suggestion],
  );
  return (
    <div className="flex min-w-[14rem] flex-col gap-1">
      {proposed ? (
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <span>
            <span className="text-muted-foreground">Jev: </span>
            {proposed.path.map((node) => node.name).join(" / ")}
            {suggestion?.confidence != null && (
              <span className="text-muted-foreground">
                {" "}
                · {Math.round(suggestion.confidence * 100)}%
              </span>
            )}
            {suggestion?.state === "stale" && (
              <span className="text-muted-foreground"> · earlier answer</span>
            )}
          </span>
          <Button
            type="button"
            size="sm"
            variant="secondary"
            className="h-7"
            disabled={busy}
            aria-label={`Use Jev's choice for ${description}`}
            onClick={() => onChoose(proposed, true)}
          >
            Use
          </Button>
        </div>
      ) : suggestion ? (
        <span className="text-xs text-muted-foreground">Jev wasn't sure</span>
      ) : null}
      <CategoryPathPicker
        aria-label={`Category for ${description}`}
        revision={revision}
        loading={loading}
        error={error}
        disabled={busy}
        allowUnclassified={false}
        suggestions={suggestions}
        onValueChange={(choice) => {
          if (choice.categoryId) onChoose(choice, false);
        }}
        className="h-8 min-h-8 text-xs"
      />
    </div>
  );
}
