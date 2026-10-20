import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { useUpdateStatusMutation } from "@/hooks/useNormalizedDescriptionActions";
import type { NormalizedDescription } from "./types";

interface RejectDialogProps {
  source: NormalizedDescription;
  onClose: () => void;
}
/**
 * Confirmation for the one action that both destroys links and is remembered (RECEIPTS-876).
 *
 * Spelled out rather than fired inline like Approve, because two consequences are non-obvious:
 * the linked items become unnormalized, and the resolver will not offer this text again.
 */
export function RejectDialog({ source, onClose }: RejectDialogProps) {
  const updateStatus = useUpdateStatusMutation();
  const count = source.linkedItemCount ?? 0;

  function handleConfirm() {
    updateStatus.mutate(
      { id: source.id, status: "rejected" },
      { onSuccess: () => onClose() },
    );
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Reject “{source.displayName}”?</DialogTitle>
          <DialogDescription>
            {count === 0
              ? "No receipt items are linked to this entry."
              : `${count} receipt ${count === 1 ? "item" : "items"} will become unnormalized and report under "(Not Normalized)".`}{" "}
            The entry is kept as a record of your decision and is unavailable
            for selection until explicitly reinstated. Use Merge instead if this
            is really the same item as an existing entry.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="destructive"
            onClick={handleConfirm}
            disabled={updateStatus.isPending}
          >
            {updateStatus.isPending ? "Rejecting…" : "Reject"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

interface RetireDialogProps {
  source: NormalizedDescription;
  onClose: () => void;
}
/**
 * Sends an Active entry back to the review queue (RECEIPTS-879).
 *
 * Confirmed rather than fired inline because the consequence is not local to this row: until it
 * is approved again, its spend renders as provisional in the spending report (RECEIPTS-875).
 * Nothing is unlinked — this is the reversible action, which is what separates it from Reject.
 */
export function RetireDialog({ source, onClose }: RetireDialogProps) {
  const updateStatus = useUpdateStatusMutation();
  const count = source.linkedItemCount ?? 0;

  function handleConfirm() {
    updateStatus.mutate(
      { id: source.id, status: "pendingReview" },
      { onSuccess: () => onClose() },
    );
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Send “{source.displayName}” back to review?</DialogTitle>
          <DialogDescription>
            The entry returns to the review queue and its{" "}
            {count === 1
              ? "one linked item stays"
              : `${count} linked items stay`}{" "}
            attached — nothing is unlinked and no receipt data changes. Until it
            is approved again its spending is reported as unreviewed. Approve it
            from the review queue to undo this.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={handleConfirm} disabled={updateStatus.isPending}>
            {updateStatus.isPending ? "Sending…" : "Send back to review"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
