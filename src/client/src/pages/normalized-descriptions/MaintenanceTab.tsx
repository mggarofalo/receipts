import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import {
  useRequeuePendingMutation,
  useRequeuePendingPreview,
} from "@/hooks/useNormalizedDescriptionMaintenance";
import { useState } from "react";
/**
 * Operational actions for the normalized-description registry (RECEIPTS-883).
 *
 * The only action today is the requeue: existing PendingReview rows predate near-miss capture
 * (RECEIPTS-873), so they render "No comparison recorded" forever. Deleting them lets the
 * background resolver rebuild each one with real evidence. It is not backfilled, because a
 * neighbour computed now would be measured against today's registry and could name an entry
 * that did not exist when the row was created.
 */
export function MaintenanceTab() {
  const preview = useRequeuePendingPreview();
  const [confirmation, setConfirmation] = useState<null | {
    pendingDescriptionCount: number;
    pendingFingerprint: string;
    linkedItemCount: number;
  }>(null);

  if (preview.isLoading) {
    return <Skeleton className="h-48 w-full rounded-lg" />;
  }

  if (preview.isError || !preview.data) {
    return (
      <div className="rounded-lg border border-destructive p-6 text-center">
        <p className="text-destructive">Failed to load maintenance status.</p>
      </div>
    );
  }

  const {
    pendingDescriptionCount,
    pendingFingerprint,
    linkedItemCount,
    staleMatchScoreCount,
    estimatedCatchUpSeconds,
  } = preview.data;
  const nothingToDo = pendingDescriptionCount === 0;

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle>Requeue Pending Descriptions</CardTitle>
          <CardDescription>
            Descriptions created before near-miss evidence was captured have
            nothing to show in the review queue — their nearest match was never
            recorded. Requeueing deletes them so the background resolver
            rebuilds each one from scratch, this time with the evidence
            attached. Active entries are never touched.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {nothingToDo ? (
            <p className="text-muted-foreground" data-testid="requeue-empty">
              No pending descriptions to requeue.
            </p>
          ) : (
            <>
              <div
                className="grid grid-cols-1 gap-4 sm:grid-cols-3"
                data-testid="requeue-preview-panel"
              >
                <div>
                  <p className="card-sub">Pending descriptions</p>
                  <p className="money-med tabular-nums">
                    {pendingDescriptionCount}
                  </p>
                  <p className="text-xs text-muted-foreground">
                    will be deleted
                  </p>
                </div>
                <div>
                  <p className="card-sub">Receipt items</p>
                  <p className="money-med tabular-nums">{linkedItemCount}</p>
                  <p className="text-xs text-muted-foreground">
                    unnormalized until the resolver catches up
                  </p>
                </div>
                <div>
                  <p className="card-sub">Match scores</p>
                  <p className="money-med tabular-nums">
                    {staleMatchScoreCount}
                  </p>
                  <p className="text-xs text-muted-foreground">
                    cleared in the same transaction
                  </p>
                </div>
              </div>
              <p className="text-sm text-muted-foreground">
                Estimated catch-up:{" "}
                <span className="font-medium">
                  {formatCatchUp(estimatedCatchUpSeconds)}
                </span>{" "}
                at 50 items per 30-second resolver cycle. Approximate — the
                resolver shares each batch with any items that were already
                unresolved.
              </p>
            </>
          )}
          <Button
            variant="destructive"
            disabled={nothingToDo}
            onClick={() =>
              setConfirmation({
                pendingDescriptionCount,
                pendingFingerprint,
                linkedItemCount,
              })
            }
          >
            Requeue {pendingDescriptionCount} pending{" "}
            {pendingDescriptionCount === 1 ? "description" : "descriptions"}
          </Button>
        </CardContent>
      </Card>

      {confirmation && (
        <RequeueConfirmDialog
          open
          {...confirmation}
          onClose={() => setConfirmation(null)}
        />
      )}
    </div>
  );
}
/** "150" -> "2m 30s". Seconds only below a minute, so a tiny backlog doesn't read as "0m". */
function formatCatchUp(seconds: number) {
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  const remainder = seconds % 60;
  return remainder === 0 ? `${minutes}m` : `${minutes}m ${remainder}s`;
}

interface RequeueConfirmDialogProps {
  open: boolean;
  pendingDescriptionCount: number;
  pendingFingerprint: string;
  linkedItemCount: number;
  onClose: () => void;
}
function RequeueConfirmDialog({
  open,
  pendingDescriptionCount,
  pendingFingerprint,
  linkedItemCount,
  onClose,
}: RequeueConfirmDialogProps) {
  const requeue = useRequeuePendingMutation();

  function handleConfirm() {
    // The fingerprint identifies the exact rows this dialog described. If the queue has shifted
    // since — even to the same total — the server rejects rather than destroying a row the
    // operator never saw.
    requeue.mutate(
      { expectedFingerprint: pendingFingerprint },
      // Close on failure too: a 409 means the counts on screen are stale, and the hook has
      // already refetched them. Leaving the dialog open would invite a confirm against
      // numbers that no longer hold.
      { onSettled: () => onClose() },
    );
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Requeue {pendingDescriptionCount} pending</DialogTitle>
          <DialogDescription>
            This deletes {pendingDescriptionCount} pending-review{" "}
            {pendingDescriptionCount === 1 ? "entry" : "entries"} and unlinks{" "}
            {linkedItemCount} receipt {linkedItemCount === 1 ? "item" : "items"}
            . Any review judgement already applied to those entries is
            discarded, and the items stay unnormalized until the resolver
            rebuilds them. Active entries are not affected. This cannot be
            undone — take a backup first if you are running against production.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="destructive"
            onClick={handleConfirm}
            disabled={requeue.isPending}
          >
            {requeue.isPending ? "Requeueing…" : "Requeue"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
