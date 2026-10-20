import { useId, useState } from "react";
import {
  useCatalogLabelActions,
  type CatalogLabelReview as LabelReview,
} from "@/hooks/useNormalizationCatalog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { extractErrorMessage } from "@/lib/problem-details";

export function CatalogLabelReview({
  targetId,
  suggestedLabel,
  suggestion,
}: {
  targetId: string;
  suggestedLabel: string;
  suggestion?: { runId: string; proposalId: string; runRevision: string };
}) {
  const controlId = useId();
  const [draft, setDraft] = useState(suggestedLabel);
  const [review, setReview] = useState<LabelReview | null>(null);
  const { preview, approve } = useCatalogLabelActions();
  const busy = preview.isPending || approve.isPending;
  const error = preview.error ?? approve.error;
  if (approve.isSuccess)
    return (
      <p role="status">
        Display name updated for this product across search and reports.
        Purchase assignments are unchanged.
      </p>
    );
  return (
    <section
      className="space-y-3 rounded border p-3"
      aria-label="Global display name review"
    >
      <h4 className="font-medium">Review a readable display name</h4>
      <p>
        This name applies everywhere this product is used, across all stores and
        purchases. Preserve known brand, size, flavor and dietary distinctions.
        The suggestion may be wrong.
      </p>
      <label htmlFor={controlId}>Display name</label>
      <Input
        id={controlId}
        value={draft}
        maxLength={200}
        disabled={busy}
        onChange={(event) => {
          setDraft(event.target.value);
          setReview(null);
          preview.reset();
          approve.reset();
        }}
      />
      <Button
        variant="outline"
        disabled={busy || !draft.trim()}
        onClick={() => {
          setReview(null);
          approve.reset();
          preview.mutate(
            { targetId, displayLabel: draft.trim() },
            { onSuccess: setReview },
          );
        }}
      >
        Preview global name change
      </Button>
      {review && (
        <div className="space-y-2">
          <p>
            Current name: {review.currentDisplayLabel || review.canonicalName}
          </p>
          <p>
            Proposed name: {review.proposedDisplayLabel || review.canonicalName}
          </p>
          <p>
            {review.affectedItemCount} current purchases use this product.
            Future purchases linked to it will also show this name. This does
            not merge products or approve source mappings.
          </p>
          <Button
            disabled={busy}
            onClick={() => approve.mutate({ review, suggestion })}
          >
            Approve global display name
          </Button>
        </div>
      )}
      {error && (
        <p role="alert">
          {extractErrorMessage(error)} Preview again if the product or its
          purchase count changed.
        </p>
      )}
    </section>
  );
}
