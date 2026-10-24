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
  semantic: "Similar description",
};

/** Optional retrieval. Selecting a candidate still requires the entry preview and save checks. */
export function ReceiptNormalizationShortlist({
  source,
  disabled,
  onChoose,
}: ReceiptNormalizationShortlistProps) {
  const suggestions = useReceiptNormalizationCandidates(source);
  return (
    <section aria-label="Suggested products" className="space-y-2 rounded-md bg-muted/40 p-3">
      <h3 className="text-sm font-medium">Suggested products</h3>
      <p className="text-xs text-muted-foreground">
        These are possibilities, not verified matches. Check the brand, size and
        variety. Full catalog search is available below.
      </p>
      {suggestions.isPending ? (
        <p role="status" className="text-sm">Loading suggested products…</p>
      ) : suggestions.isError ? (
        <RequestFailure
          message="Suggested products are unavailable. Search all products below or save for review."
          retry={() => void suggestions.refetch()}
          isRetrying={suggestions.isFetching}
        />
      ) : !suggestions.data?.length ? (
        <p className="text-sm">No suggested products. Search the full catalog or name a new product below.</p>
      ) : (
        <ul className="space-y-2">
          {suggestions.data.slice(0, 5).map((candidate) => (
            <li key={candidate.id} className="space-y-1">
              <Button
                type="button"
                variant="outline"
                className="h-auto max-w-full whitespace-normal text-left [overflow-wrap:anywhere]"
                disabled={disabled || suggestions.isFetching}
                onClick={() => onChoose(candidate)}
              >
                Choose suggested {candidate.displayLabel ?? candidate.canonicalName}
              </Button>
              <p className="text-xs text-muted-foreground">
                {candidate.channels.map((channel) => evidenceLabels[channel] ?? "Other matching evidence").join(" · ")}
              </p>

            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
