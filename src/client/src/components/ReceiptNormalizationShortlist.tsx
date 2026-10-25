import { useState } from "react";
import { Button } from "@/components/ui/button";
import { RequestFailure } from "@/components/RequestFailure";
import {
  useReceiptNormalizationCandidates,
  type ReceiptNormalizationSuggestion,
} from "@/hooks/useReceiptNormalizationCandidates";
import type { ReceiptNormalizationSource } from "@/lib/receipt-normalization";

export interface ReceiptNormalizationShortlistProps {
  source: ReceiptNormalizationSource;
  disabled?: boolean;
  onChoose: (candidate: ReceiptNormalizationSuggestion) => void;
}

const evidenceLabels: Record<string, string> = {
  exact: "Exact name",
  approved_example: "Approved description",
  lexical: "Shared words",
};

/** Optional retrieval. Selecting a candidate still requires the entry preview and save checks. */
export function ReceiptNormalizationShortlist({
  source,
  disabled,
  onChoose,
}: ReceiptNormalizationShortlistProps) {
  const suggestions = useReceiptNormalizationCandidates(source);
  const [showAll, setShowAll] = useState(false);
  // Semantic retrieval alone is too weak to offer as an inline product match.
  // Keep the server's ranking for candidates backed by specific matching evidence.
  const candidates =
    suggestions.data?.filter((candidate) =>
      candidate.channels.some((channel) =>
        ["exact", "approved_example", "lexical"].includes(channel),
      ),
    ) ?? [];
  if (!suggestions.isPending && !suggestions.isError && !candidates.length)
    return null;
  return (
    <section aria-label="Suggested products" className="space-y-2">
      <h3 className="text-sm font-medium">Suggested products</h3>
      {suggestions.isPending ? (
        <p role="status" className="text-sm">
          Loading suggested products…
        </p>
      ) : suggestions.isError ? (
        <RequestFailure
          message="Product suggestions unavailable. You can search or continue entering items."
          retry={() => void suggestions.refetch()}
          isRetrying={suggestions.isFetching}
        />
      ) : !candidates.length ? (
        <p className="text-sm">
          No suggested products. Search the full catalog or name a new product
          below.
        </p>
      ) : (
        <ul className="flex max-h-64 flex-wrap gap-2 overflow-y-auto">
          {(showAll ? candidates : candidates.slice(0, 5)).map((candidate) => (
            <li key={candidate.id} className="space-y-1">
              <Button
                type="button"
                variant="outline"
                className="h-auto max-w-full whitespace-normal text-left [overflow-wrap:anywhere]"
                disabled={disabled || suggestions.isFetching}
                onClick={() => onChoose(candidate)}
              >
                Choose suggested{" "}
                {candidate.displayLabel ?? candidate.canonicalName}
              </Button>
              <p className="text-xs text-muted-foreground">
                {candidate.channels
                  .filter((channel) => channel !== "semantic")
                  .map(
                    (channel) =>
                      evidenceLabels[channel] ?? "Other matching evidence",
                  )
                  .join(" · ")}
              </p>
            </li>
          ))}
        </ul>
      )}
      {candidates.length > 5 && (
        <Button
          type="button"
          size="sm"
          variant="ghost"
          onClick={() => setShowAll(!showAll)}
        >
          {showAll
            ? "Fewer products"
            : `More products (${candidates.length - 5})`}
        </Button>
      )}
    </section>
  );
}
