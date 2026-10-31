import { useAiActions, type AiRun } from "@/hooks/useNormalizationAi";
import { Button } from "@/components/ui/button";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { AiRunStatus } from "./AiRunStatus";
import { visibleAiProposals } from "./ai-run-state";
import { extractErrorMessage } from "@/lib/problem-details";

/** Read-only view of a run from the retired per-purchase workflow. */
export function AiRunReview({ run }: { run: AiRun }) {
  const { cancel } = useAiActions();
  const proposals = visibleAiProposals(run);
  const discardable = run.state === "ready" && proposals.length > 0;
  return (
    <section
      className="space-y-3 rounded-lg border p-4"
      aria-label="Review ChatGPT suggestions"
    >
      <h2 className="card-title">
        {proposals.length === 0 ? "Suggestion request" : "Suggestion outcomes"}
      </h2>
      <AiRunStatus run={run} />
      {["queued", "running", "unknown", "failed"].includes(run.state) && (
        <Button
          variant="outline"
          onClick={() => cancel.mutate(run.id)}
          disabled={cancel.isPending}
        >
          Cancel request
        </Button>
      )}
      {discardable && (
        <>
          <p>
            Per-purchase suggestions are retired and can no longer be applied or
            retried. Discard them so these items can be requested from Catalog.
          </p>
          <Button
            variant="outline"
            disabled={cancel.isPending}
            onClick={() => cancel.mutate(run.id)}
          >
            Discard suggestions
          </Button>
        </>
      )}
      {proposals.length > 0 && (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Original item</TableHead>
              <TableHead>Suggestion</TableHead>
              <TableHead>Reason</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {proposals.map((proposal) => (
              <TableRow key={proposal.id}>
                <TableCell>{proposal.description}</TableCell>
                <TableCell>
                  {proposal.action === "abstain"
                    ? "No suggestion"
                    : (proposal.displayLabel ?? proposal.canonicalName)}
                  <span className="block text-xs text-muted-foreground">
                    {proposal.action === "reuse"
                      ? "Link to existing name"
                      : proposal.action === "create"
                        ? "Create reviewed name"
                        : "Needs manual review"}{" "}
                    ... {proposal.state}
                  </span>
                </TableCell>
                <TableCell>{proposal.reason}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {run.state === "ready" && proposals.length === 0 && (
        <p>No reviewable suggestions were returned.</p>
      )}
      {run.state === "applied" && (
        <p>
          Accepted suggestions were applied. Unselected suggestions were not
          applied.
        </p>
      )}
      {cancel.error && (
        <p role="alert" className="text-destructive">
          {extractErrorMessage(cancel.error)}
        </p>
      )}
    </section>
  );
}
