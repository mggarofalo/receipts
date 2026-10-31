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
/** Removes explicitly previewed pending catalog records; purchases return to review. */
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

  const { pendingDescriptionCount, pendingFingerprint, linkedItemCount } =
    preview.data;
  const nothingToDo = pendingDescriptionCount === 0;

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle>Requeue Pending Descriptions</CardTitle>
          <CardDescription>
            Delete the previewed pending entries and clear their purchase links.
            Purchases return to review without automatic similarity rematching.
            Active entries stay unchanged.
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
                className="grid grid-cols-1 gap-4 sm:grid-cols-2"
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
                    returned to review
                  </p>
                </div>
              </div>
              <p className="text-sm text-muted-foreground">
                Review these purchases through source groups or receipt entry.
                Only an approved exact scoped mapping can be reused
                automatically.
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
            discarded, and the items need review. No automatic similarity
            rematching is scheduled. Active entries are not affected. This
            cannot be undone — take a backup first if you are running against
            production.
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
