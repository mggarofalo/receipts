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
  usePendingNamePromotionPreview,
  usePendingNamePromotions,
  usePromotePendingNamesMutation,
  useUndoPendingNamePromotionMutation,
} from "@/hooks/usePendingNamePromotion";
import { useState } from "react";

function plural(count: number, one: string, many: string) {
  return `${count} ${count === 1 ? one : many}`;
}

/**
 * Promotes consistent pending-review names to active in one undoable action (RECEIPTS-1107).
 * It approves names, not purchases.
 */
export function PendingPromotionCard() {
  const preview = usePendingNamePromotionPreview();
  const [confirming, setConfirming] = useState<null | {
    nameCount: number;
    purchaseCount: number;
    fingerprint: string;
  }>(null);

  if (preview.isLoading) {
    return <Skeleton className="h-48 w-full rounded-lg" />;
  }

  if (preview.isError || !preview.data) {
    return (
      <div className="rounded-lg border border-destructive p-6 text-center">
        <p className="text-destructive">Failed to load names to promote.</p>
      </div>
    );
  }

  const { minimumPurchases, nameCount, purchaseCount, fingerprint, names } =
    preview.data;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Promote Consistent Pending Names</CardTitle>
        <CardDescription>
          Make pending-review names active when they have at least{" "}
          {minimumPurchases} purchases, none of their receipt lines is linked to
          another name, and no other name has the same spelling. Active names
          appear in suggestions and can be chosen. Purchases keep their links
          and review state. You can undo a promotion.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {nameCount === 0 ? (
          <p className="text-muted-foreground" data-testid="promotion-empty">
            No pending names qualify.
          </p>
        ) : (
          <>
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <div>
                <p className="card-sub">Pending names</p>
                <p className="money-med tabular-nums">{nameCount}</p>
                <p className="text-xs text-muted-foreground">
                  will become active
                </p>
              </div>
              <div>
                <p className="card-sub">Purchases</p>
                <p className="money-med tabular-nums">{purchaseCount}</p>
                <p className="text-xs text-muted-foreground">
                  linked to those names
                </p>
              </div>
            </div>
            <details>
              <summary className="cursor-pointer text-sm">
                Show the names
              </summary>
              <ul
                className="mt-2 max-h-64 overflow-y-auto text-sm"
                data-testid="promotion-names"
              >
                {names.map((name) => (
                  <li key={name.id} className="flex justify-between gap-4 py-1">
                    <span className="min-w-0 break-words">
                      {name.canonicalName}
                    </span>
                    <span className="tabular-nums text-muted-foreground">
                      {plural(name.purchaseCount, "purchase", "purchases")}
                    </span>
                  </li>
                ))}
              </ul>
            </details>
          </>
        )}
        <Button
          disabled={nameCount === 0}
          onClick={() =>
            setConfirming({ nameCount, purchaseCount, fingerprint })
          }
        >
          Promote {plural(nameCount, "name", "names")}
        </Button>
        <PromotionHistory />
      </CardContent>
      {confirming && (
        <PromoteConfirmDialog
          {...confirming}
          onClose={() => setConfirming(null)}
        />
      )}
    </Card>
  );
}

function PromoteConfirmDialog({
  nameCount,
  purchaseCount,
  fingerprint,
  onClose,
}: {
  nameCount: number;
  purchaseCount: number;
  fingerprint: string;
  onClose: () => void;
}) {
  const promote = usePromotePendingNamesMutation();
  return (
    <Dialog
      open
      onOpenChange={(next) => {
        if (!next) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>
            Promote {plural(nameCount, "name", "names")}
          </DialogTitle>
          <DialogDescription>
            {plural(nameCount, "pending-review name", "pending-review names")}{" "}
            covering {plural(purchaseCount, "purchase", "purchases")} become
            active. No purchase is reviewed or relinked, and no mapping is
            created. If the eligible names changed since this preview, nothing
            is promoted and the preview reloads.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button
            disabled={promote.isPending}
            onClick={() =>
              // Close on failure too: after a 409 the counts on screen are stale.
              promote.mutate(
                { expectedFingerprint: fingerprint },
                { onSettled: () => onClose() },
              )
            }
          >
            {promote.isPending ? "Promoting…" : "Promote"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function PromotionHistory() {
  const history = usePendingNamePromotions();
  const undo = useUndoPendingNamePromotionMutation();
  const promotions = history.data ?? [];
  if (promotions.length === 0) return null;

  return (
    <div className="space-y-2" data-testid="promotion-history">
      <p className="card-sub">Recent promotions</p>
      <ul className="space-y-2 text-sm">
        {promotions.map((promotion) => (
          <li
            key={promotion.id}
            className="flex flex-wrap items-center justify-between gap-2"
          >
            <span>
              {new Date(promotion.createdAt).toLocaleString()}:{" "}
              {plural(promotion.promotedCount, "name", "names")}
            </span>
            {promotion.undoneAt ? (
              <span className="text-muted-foreground">
                Undone: {promotion.restoredCount ?? 0} returned to review,{" "}
                {promotion.keptCount ?? 0} kept
              </span>
            ) : (
              <Button
                variant="outline"
                size="sm"
                disabled={undo.isPending}
                onClick={() => undo.mutate({ id: promotion.id })}
              >
                Undo
              </Button>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}
