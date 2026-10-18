import { RequestFailure } from "@/components/RequestFailure";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { useSplitMutation } from "@/hooks/useNormalizedDescriptionActions";
import { useLinkedReceiptItems } from "@/hooks/useReceiptItems";
import { useMemo, useState } from "react";
import type { NormalizedDescription, ReceiptItem } from "./types";

/** Page size for the split dialog's linked-item list. */
const SPLIT_PAGE_SIZE = 50;

interface SplitDialogProps {
  source: NormalizedDescription;
  onClose: () => void;
}
export function SplitDialog({ source, onClose }: SplitDialogProps) {
  const split = useSplitMutation();
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [selectedPages, setSelectedPages] = useState<Record<string, number>>(
    {},
  );
  const [verification, setVerification] = useState({
    pageKey: "",
    disprovenIds: [] as string[],
  });
  const [name, setName] = useState("");
  const [nameTouched, setNameTouched] = useState(false);
  const [page, setPage] = useState(0);

  // Server-side, filtered to this entry. Every linked item is reachable by paging, rather than
  // only those that happened to fall in a fixed page of the unfiltered list (RECEIPTS-877).
  const {
    data: items,
    total,
    isLoading,
    isError,
    isFetching,
    refetch,
  } = useLinkedReceiptItems(
    source.id ?? null,
    page * SPLIT_PAGE_SIZE,
    SPLIT_PAGE_SIZE,
  );

  const linked = useMemo(() => (items ?? []) as ReceiptItem[], [items]);
  const totalPages = Math.ceil(total / SPLIT_PAGE_SIZE);

  const canChoose = !isLoading && !isError && !isFetching && !split.isPending;

  function toggle(itemId: string, description: string) {
    if (!canChoose) return;
    setVerification((previous) => ({
      ...previous,
      disprovenIds: previous.disprovenIds.filter((id) => id !== itemId),
    }));
    const next = selectedIds.includes(itemId)
      ? selectedIds.filter((existing) => existing !== itemId)
      : [...selectedIds, itemId];
    if (!nameTouched && next.length === 1 && !selectedIds.includes(itemId))
      setName(description);
    if (!nameTouched && next.length === 0) setName("");
    setSelectedIds(next);
    setSelectedPages((previous) => ({ ...previous, [itemId]: page }));
  }

  const trimmedName = name.trim();
  // Only a settled successful read establishes absence. Remember disproven
  // selections when leaving this page so paging cannot make them valid again.
  const missingOnPage =
    !isLoading && !isError && !isFetching
      ? selectedIds.filter(
          (id) =>
            selectedPages[id] === page &&
            !linked.some((item) => item.id === id),
        )
      : [];
  const pageKey = JSON.stringify([page, linked.map((item) => item.id)]);
  // Retain evidence at the successful read transition, before any later fetch
  // or navigation. A guarded render adjustment avoids a post-render window in
  // which an interaction could erase that evidence. Compare content rather than
  // query-result identity, which deliberately changes between renders.
  if (
    !isLoading &&
    !isError &&
    !isFetching &&
    verification.pageKey !== pageKey
  ) {
    setVerification({
      pageKey,
      disprovenIds: [
        ...new Set([...verification.disprovenIds, ...missingOnPage]),
      ],
    });
  }
  const missingSelection =
    missingOnPage.length > 0 ||
    selectedIds.some((id) => verification.disprovenIds.includes(id));
  const canSubmit =
    selectedIds.length > 0 &&
    trimmedName.length > 0 &&
    !split.isPending &&
    !isLoading &&
    !isError &&
    !isFetching &&
    !missingSelection;

  function handleConfirm() {
    if (!canSubmit) return;
    split.mutate(
      {
        id: source.id,
        receiptItemIds: selectedIds,
        canonicalName: trimmedName,
      },
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
          <DialogTitle>Split Out Receipt Items</DialogTitle>
          <DialogDescription>
            Select the receipt items to detach from &quot;{source.displayName}
            &quot; and give the group a name. They move together into one entry;
            anything you leave unselected stays where it is.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          {isError ? (
            <RequestFailure
              message="Linked items unavailable. Retry before splitting."
              retry={() => {
                void refetch();
              }}
              isRetrying={isFetching}
            />
          ) : isLoading ? (
            <Skeleton className="h-24 w-full rounded" />
          ) : linked.length === 0 ? (
            // Says what is true — this entry has nothing linked — rather than the old
            // "we could not find them in the most recent 200 items", which was a statement
            // about the query rather than the data.
            <p
              className="text-sm text-muted-foreground"
              data-testid="split-empty"
            >
              No receipt items are linked to this entry, so there is nothing to
              split out.
            </p>
          ) : (
            <>
              <ul className="max-h-64 overflow-y-auto divide-y rounded border">
                {linked.map((item) => (
                  <li key={item.id}>
                    <label className="flex cursor-pointer items-center gap-2 p-2 text-sm hover:bg-muted/50">
                      <input
                        type="checkbox"
                        disabled={!canChoose}
                        value={item.id}
                        checked={selectedIds.includes(item.id)}
                        onChange={() => toggle(item.id, item.description)}
                      />
                      <span>{item.description}</span>
                    </label>
                  </li>
                ))}
              </ul>

              {totalPages > 1 && (
                <div className="flex items-center justify-between text-sm">
                  <span className="text-muted-foreground">
                    Page {page + 1} of {totalPages} · {total} linked items
                  </span>
                  <div className="flex gap-2">
                    <Button
                      variant="outline"
                      size="sm"
                      disabled={page === 0}
                      onClick={() => setPage(page - 1)}
                    >
                      Previous
                    </Button>
                    <Button
                      variant="outline"
                      size="sm"
                      disabled={page + 1 >= totalPages}
                      onClick={() => setPage(page + 1)}
                    >
                      Next
                    </Button>
                  </div>
                </div>
              )}

              <div>
                <Label htmlFor="split-name">Name for the new entry</Label>
                <Input
                  id="split-name"
                  value={name}
                  onChange={(e) => {
                    setNameTouched(true);
                    setName(e.target.value);
                  }}
                  placeholder="e.g. Milk"
                  className="mt-1"
                />
                <p className="mt-1 text-xs text-muted-foreground">
                  {selectedIds.length === 0
                    ? "Select at least one item."
                    : `${selectedIds.length} selected. If an entry with this name already exists, the items are moved to it instead.`}
                </p>
              </div>
            </>
          )}
        </div>
        {missingSelection && (
          <p role="alert">
            A selected item is no longer on its verified page. Clear the
            selection and choose the current items.
          </p>
        )}
        {missingSelection && (
          <Button
            variant="outline"
            onClick={() => {
              setSelectedIds([]);
              setSelectedPages({});
              setVerification((previous) => ({
                ...previous,
                disprovenIds: [],
              }));
            }}
          >
            Clear selection
          </Button>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={handleConfirm} disabled={!canSubmit}>
            {split.isPending ? "Splitting…" : "Split"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
