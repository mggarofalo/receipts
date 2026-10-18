import { RequestFailure } from "@/components/RequestFailure";
import { Badge } from "@/components/ui/badge";
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
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { useMergeMutation } from "@/hooks/useNormalizedDescriptionActions";
import { useNormalizedDescriptions } from "@/hooks/useNormalizedDescriptions";
import { isPendingReview } from "@/lib/normalized-description-status";
import { useMemo, useState } from "react";
import type { NormalizedDescription } from "./types";

interface MergeDialogProps {
  source: NormalizedDescription;
  onClose: () => void;
}

/** Candidate window. Search runs server-side, so this bounds one page, not the searchable set. */
const MERGE_CANDIDATE_PAGE_SIZE = 50;
export function MergeDialog({ source, onClose }: MergeDialogProps) {
  const merge = useMergeMutation();
  const [targetId, setTargetId] = useState<string | undefined>();
  const [search, setSearch] = useState("");
  const debouncedSearch = useDebouncedValue(search);

  // Searched on the server (RECEIPTS-879). It used to filter whatever the "Active" list had
  // already loaded, which meant the dialog could only ever find a target that happened to be in
  // that array — and once the list endpoint became paged, that array is one page.
  //
  // Both statuses, not just Active (RECEIPTS-878). Two near-duplicate pending entries out of the
  // same resolver batch are exactly the pair you want to merge, and requiring one to be approved
  // first forced a judgement ("this is a real item") that the reviewer had not made yet. Rejected
  // is excluded: merging items into a tombstone would resurrect text someone retired on purpose.
  const {
    items: candidates,
    total,
    isSuccess,
    isLoading,
    isError,
    isFetching,
    isPlaceholderData,
    refetch,
  } = useNormalizedDescriptions({
    status: ["Active", "PendingReview"],
    q: debouncedSearch,
    limit: MERGE_CANDIDATE_PAGE_SIZE,
    enabled: true,
  });

  const filtered = useMemo(
    () => candidates.filter((c) => c.id !== source.id),
    [candidates, source.id],
  );

  const isDebouncing = search.trim() !== debouncedSearch.trim();
  const canChoose =
    isSuccess &&
    !isFetching &&
    !isPlaceholderData &&
    !isDebouncing &&
    !merge.isPending;
  const canConfirm =
    canChoose && filtered.some((candidate) => candidate.id === targetId);

  const sourceCount = source.linkedItemCount ?? 0;

  function handleConfirm() {
    if (!targetId || !canConfirm) return;
    merge.mutate(
      { id: targetId, discardId: source.id },
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
          <DialogTitle>Merge Into Another Entry</DialogTitle>
          {/* States the blast radius up front: how many items move, and that the entry being
              merged away is deleted rather than filed somewhere. The old copy said "this
              pending-review entry", which became wrong once the registry could merge two Active
              entries (RECEIPTS-879). */}
          <DialogDescription>
            Pick the canonical row to keep.{" "}
            {sourceCount === 0
              ? "No receipt items are linked to this entry, so nothing will be re-pointed."
              : `${sourceCount} receipt ${sourceCount === 1 ? "item" : "items"} linked to “${source.displayName}” will be re-pointed at the chosen row.`}{" "}
            “{source.displayName}” is then deleted. This cannot be undone.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          <div>
            <Label htmlFor="merge-search">Search active entries</Label>
            <Input
              id="merge-search"
              value={search}
              onChange={(e) => {
                setSearch(e.target.value);
                setTargetId(undefined);
              }}
              placeholder="Start typing a canonical name…"
              className="mt-1"
            />
          </div>
          <div className="max-h-64 overflow-y-auto rounded border">
            {isLoading || isDebouncing ? (
              <Skeleton className="h-24 w-full" />
            ) : isError ? (
              <RequestFailure
                message="Merge candidates unavailable. Retry to verify the target."
                retry={() => {
                  void refetch();
                }}
                isRetrying={isFetching}
              />
            ) : filtered.length === 0 ? (
              <p className="p-4 text-sm text-muted-foreground">
                No matching entries.
              </p>
            ) : (
              <ul className="divide-y">
                {filtered.map((c) => (
                  <li key={c.id}>
                    <label className="flex cursor-pointer items-center gap-2 p-2 text-sm hover:bg-muted/50">
                      <input
                        type="radio"
                        name="merge-target"
                        disabled={!canChoose}
                        value={c.id}
                        checked={targetId === c.id}
                        onChange={() => setTargetId(c.id)}
                      />
                      <span className="font-medium">{c.displayName}</span>
                      {/* Pending targets are legitimate but consequential: the survivor stays
                          pending and still needs review, so the reviewer should know they are
                          not merging into something already approved. */}
                      {isPendingReview(c.status) && (
                        <Badge variant="secondary" className="text-xs">
                          Pending review
                        </Badge>
                      )}
                      {/* Merging is direction-sensitive and irreversible. Without both counts
                          side by side there is nothing on screen to say which way round moves
                          the fewest items. */}
                      <span className="ml-auto shrink-0 text-xs tabular-nums text-muted-foreground">
                        {c.linkedItemCount}{" "}
                        {c.linkedItemCount === 1 ? "item" : "items"}
                      </span>
                    </label>
                  </li>
                ))}
              </ul>
            )}
          </div>
          {/* Says so when there are more matches than are shown. The list was capped at 50 before
              this with nothing on screen to say so, which reads as "your target does not exist"
              (RECEIPTS-878). */}
          {total > candidates.length && (
            <p
              className="text-xs text-muted-foreground"
              data-testid="merge-truncation-notice"
            >
              Showing {candidates.length} of {total} matching entries — refine
              your search to narrow it down.
            </p>
          )}
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={handleConfirm} disabled={!canConfirm}>
            {merge.isPending ? "Merging…" : "Merge"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
