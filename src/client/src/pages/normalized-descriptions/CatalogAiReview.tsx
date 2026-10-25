import { useRef, useState } from "react";
import { useAiActions, type AiRun } from "@/hooks/useNormalizationAi";
import type { CatalogSource } from "@/hooks/useNormalizationCatalog";
import type { CatalogSuggestion } from "./CatalogSourceReview";
import { CatalogLabelReview } from "./CatalogLabelReview";
import { Button } from "@/components/ui/button";
import { AiRunStatus } from "./AiRunStatus";
import { visibleAiProposals } from "./ai-run-state";
import { extractErrorMessage } from "@/lib/problem-details";

export function CatalogAiReview({
  run,
  onReview,
  onRetried,
}: {
  run: AiRun;
  onReview: (source: CatalogSource, suggestion: CatalogSuggestion) => void;
  onRetried: (id: string) => void;
}) {
  const [labelReviewId, setLabelReviewId] = useState<string | null>(null);
  const retryId = useRef(crypto.randomUUID());
  const { cancel, retry } = useAiActions((created) => onRetried(created.id));
  const pending = cancel.isPending || retry.isPending;
  const error = cancel.error ?? retry.error;
  return (
    <section
      className="space-y-3 rounded-lg border p-4"
      aria-label="Catalog suggestions"
    >
      <h2 className="card-title">ChatGPT suggestions</h2>
      <AiRunStatus run={run} />
      <p>
        Suggestions do not change your catalog. Review each source and preview
        its current historical scope before approving. A confident explanation
        can still be wrong.
      </p>
      {visibleAiProposals(run).map((proposal) => (
        <article key={proposal.id} className="space-y-2 rounded border p-3">
          <h3 className="font-medium">{proposal.description}</h3>
          {proposal.source && (
            <p>
              {proposal.source.location} · Item code:{" "}
              {proposal.source.itemCode || "not recorded"}
            </p>
          )}
          <p>
            {proposal.labelOnly && proposal.state === "no_change"
              ? "No display-name change proposed"
              : proposal.action === "abstain"
                ? "Needs manual review"
                : (proposal.displayLabel ?? proposal.canonicalName)}
          </p>
          <p>{proposal.reason}</p>
          {proposal.suggestedDisplayLabel &&
            proposal.targetId &&
            (!proposal.labelOnly ||
              (run.state === "ready" && proposal.state === "pending")) && (
              <div className="space-y-2">
                <p>Suggested readable name: {proposal.suggestedDisplayLabel}</p>
                <Button
                  variant="outline"
                  onClick={() =>
                    setLabelReviewId(
                      labelReviewId === proposal.id ? null : proposal.id,
                    )
                  }
                >
                  {labelReviewId === proposal.id
                    ? "Close name review"
                    : "Review global display name"}
                </Button>
                {labelReviewId === proposal.id && (
                  <CatalogLabelReview
                    key={proposal.id}
                    targetId={proposal.targetId}
                    suggestedLabel={proposal.suggestedDisplayLabel}
                    suggestion={
                      proposal.labelOnly
                        ? {
                            runId: run.id,
                            proposalId: proposal.id,
                            runRevision: run.revision,
                          }
                        : undefined
                    }
                  />
                )}
              </div>
            )}
          {proposal.labelOnly && (
            <p>
              Display name suggestion only. Existing purchase assignments are
              protected.
            </p>
          )}
          <p className="text-sm text-muted-foreground">
            {proposal.state === "no_change"
              ? "Completed without changing the display name."
              : proposal.state === "overridden"
                ? proposal.labelOnly
                  ? "Display name approved with edits; purchase assignments unchanged."
                  : "Reviewed with a different decision. See historical operations for the approved mapping."
                : proposal.state === "applied"
                  ? proposal.labelOnly
                    ? "Display name approved; purchase assignments unchanged."
                    : "Approved as suggested."
                  : proposal.state === "abstained"
                    ? "The model abstained; manual source review is available."
                    : `Review state: ${proposal.state}`}
          </p>
          {proposal.source && !proposal.labelOnly && (
            <Button
              variant="outline"
              disabled={
                proposal.state !== "abstained" &&
                (run.state !== "ready" || proposal.state !== "pending")
              }
              onClick={() =>
                onReview(proposal.source!, {
                  targetId:
                    proposal.action === "reuse" ? proposal.targetId : null,
                  targetRevision: proposal.targetRevision,
                  newCanonicalName:
                    proposal.action === "create"
                      ? proposal.canonicalName
                      : null,
                  includeLegacyUnknown: proposal.includeLegacyUnknown,
                  suggestionRunId:
                    proposal.state === "pending" ? run.id : undefined,
                  suggestionProposalId:
                    proposal.state === "pending" ? proposal.id : undefined,
                  suggestionRunRevision:
                    proposal.state === "pending" ? run.revision : undefined,
                })
              }
            >
              Review source and preview
            </Button>
          )}
        </article>
      ))}
      {["queued", "running", "unknown", "failed", "ready"].includes(
        run.state,
      ) && (
        <Button
          variant="outline"
          disabled={pending}
          onClick={() => cancel.mutate(run.id)}
        >
          {run.state === "ready" ? "Discard suggestions" : "Cancel request"}
        </Button>
      )}
      {["failed", "unknown", "interrupted"].includes(run.state) && (
        <>
          <Button
            disabled={pending}
            onClick={() =>
              retry.mutate({
                id: run.id,
                clientRequestId: retryId.current,
              })
            }
          >
            Retry as a new request
          </Button>
        </>
      )}
      {error && <p role="alert">{extractErrorMessage(error)}</p>}
    </section>
  );
}
