import { useRef, useState } from "react";
import { useAiActions, type AiRun } from "@/hooks/useNormalizationAi";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
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

export function AiRunReview({
  run,
  onRetried,
}: {
  run: AiRun;
  onRetried: (id: string) => void;
}) {
  const [selected, setSelected] = useState<string[]>([]);
  const [confirmed, setConfirmed] = useState(false);
  const retryId = useRef(crypto.randomUUID());
  const { apply, cancel, retry } = useAiActions((run) => onRetried(run.id));
  const pending = apply.isPending || cancel.isPending || retry.isPending;
  const error = apply.error ?? cancel.error ?? retry.error;
  const proposals = visibleAiProposals(run);
  const applicable = run.state === "ready" && proposals.length > 0;
  return (
    <section
      className="space-y-3 rounded-lg border p-4"
      aria-label="Review ChatGPT suggestions"
    >
      <h2 className="card-title">
        {proposals.length === 0
          ? "Suggestion request"
          : applicable
            ? "Review suggestions"
            : "Suggestion outcomes"}
      </h2>
      <AiRunStatus run={run} />
      {["queued", "running", "unknown", "failed"].includes(run.state) && (
        <>
          <Button
            variant="outline"
            onClick={() => cancel.mutate(run.id)}
            disabled={pending}
          >
            Cancel request
          </Button>
        </>
      )}
      {["failed", "unknown", "interrupted"].includes(run.state) && (
        <>
          <Button
            variant="outline"
            disabled={pending}
            onClick={() =>
              retry.mutate({ id: run.id, clientRequestId: retryId.current })
            }
          >
            Retry as a new request
          </Button>
        </>
      )}
      {applicable && (
        <>
          <Button
            variant="outline"
            disabled={pending}
            onClick={() => cancel.mutate(run.id)}
          >
            Discard suggestions
          </Button>
          <p>
            Only selected suggestions will be applied. Items with the same
            accepted name may be grouped. Prices, quantities and original
            descriptions stay unchanged. If any selected item or target changed,
            the entire apply request is rejected.
          </p>
          <label className="flex items-center gap-2">
            <Checkbox
              checked={confirmed}
              onCheckedChange={(value) => setConfirmed(value === true)}
              disabled={pending || !selected.length}
            />
            I reviewed these {selected.length} suggestions
          </label>
          <Button
            disabled={pending || !selected.length || !confirmed}
            onClick={() =>
              apply.mutate({
                id: run.id,
                proposalIds: selected,
                expectedRevision: run.revision,
              })
            }
          >
            {apply.isPending ? "Applying..." : "Apply reviewed suggestions"}
          </Button>
        </>
      )}
      {proposals.length > 0 && (
        <Table>
          <TableHeader>
            <TableRow>
              {applicable && <TableHead>Accept</TableHead>}
              <TableHead>Original item</TableHead>
              <TableHead>Suggestion</TableHead>
              <TableHead>Reason</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {proposals.map((proposal) => (
              <TableRow key={proposal.id}>
                {applicable && (
                  <TableCell>
                    <Checkbox
                      aria-label={`Accept suggestion for ${proposal.description}`}
                      checked={selected.includes(proposal.id)}
                      disabled={
                        !applicable ||
                        proposal.action === "abstain" ||
                        proposal.state !== "pending" ||
                        pending
                      }
                      onCheckedChange={(checked) => {
                        setConfirmed(false);
                        setSelected((current) =>
                          checked === true
                            ? [...current, proposal.id]
                            : current.filter((id) => id !== proposal.id),
                        );
                      }}
                    />
                  </TableCell>
                )}
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
      )}{" "}
      {run.state === "ready" && proposals.length === 0 && (
        <p>No reviewable suggestions were returned.</p>
      )}
      {run.state === "applied" && (
        <p>
          Accepted suggestions were applied. Unselected suggestions were not
          applied.
        </p>
      )}
      {error && (
        <p role="alert" className="text-destructive">
          {extractErrorMessage(error)}
        </p>
      )}
    </section>
  );
}
