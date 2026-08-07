import { useId, useMemo, useState } from "react";
import {
  useNormalizedDescriptions,
  NORMALIZED_DESCRIPTION_PAGE_SIZE,
} from "@/hooks/useNormalizedDescriptions";
import {
  useMergeMutation,
  useRenameMutation,
  useSplitMutation,
  useUpdateStatusMutation,
} from "@/hooks/useNormalizedDescriptionActions";
import {
  useSettings,
  useUpdateSettingsMutation,
  useTestMatchMutation,
  usePreviewImpactMutation,
} from "@/hooks/useNormalizedDescriptionSettings";
import {
  useRequeuePendingPreview,
  useRequeuePendingMutation,
} from "@/hooks/useNormalizedDescriptionMaintenance";
import { useLinkedReceiptItems } from "@/hooks/useReceiptItems";
import { usePermission } from "@/hooks/usePermission";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useServerPagination } from "@/hooks/useServerPagination";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { Pagination } from "@/components/Pagination";
import { formatDecimal } from "@/lib/format";
import { isPendingReview } from "@/lib/normalized-description-status";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { PageHead } from "@/components/primitives";
import { Tabs, TabsList, TabsTrigger, TabsContent } from "@/components/ui/tabs";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
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
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import type { components } from "@/generated/api";

type NormalizedDescription =
  components["schemas"]["NormalizedDescriptionResponse"];

type ReceiptItem = {
  id: string;
  description: string;
  normalizedDescriptionId?: string | null;
  normalizedDescriptionName?: string | null;
};

type TabKey = "review" | "registry" | "settings" | "maintenance";

/** Page size for the split dialog's linked-item list. */
const SPLIT_PAGE_SIZE = 50;

export default function NormalizedDescriptions() {
  usePageTitle("Normalized Descriptions");
  const { isAdmin } = usePermission();
  const [tab, setTab] = useState<TabKey>("review");

  return (
    <>
      <PageHead
        title="Normalized Descriptions"
        sub="Review, merge, and configure canonical item descriptions"
      />
      <Tabs
        value={tab}
        onValueChange={(v) => setTab(v as TabKey)}
        className="space-y-4"
      >
        <TabsList>
          <TabsTrigger value="review">Review Queue</TabsTrigger>
          <TabsTrigger value="registry">Registry</TabsTrigger>
          {isAdmin() && <TabsTrigger value="settings">Settings</TabsTrigger>}
          {isAdmin() && (
            <TabsTrigger value="maintenance">Maintenance</TabsTrigger>
          )}
        </TabsList>
        <TabsContent value="review">
          <ReviewQueueTab />
        </TabsContent>
        <TabsContent value="registry">
          <RegistryTab />
        </TabsContent>
        {isAdmin() && (
          <TabsContent value="settings">
            <SettingsTab />
          </TabsContent>
        )}
        {isAdmin() && (
          <TabsContent value="maintenance">
            <MaintenanceTab />
          </TabsContent>
        )}
      </Tabs>
    </>
  );
}

function ReviewQueueTab() {
  const pagination = useServerPagination({
    defaultPageSize: NORMALIZED_DESCRIPTION_PAGE_SIZE,
  });
  const {
    items: pending,
    total,
    isLoading,
    isError,
  } = useNormalizedDescriptions({
    status: "PendingReview",
    offset: pagination.offset,
    limit: pagination.limit,
  });
  const updateStatus = useUpdateStatusMutation();
  const [mergeTarget, setMergeTarget] = useState<NormalizedDescription | null>(
    null,
  );
  const [splitTarget, setSplitTarget] = useState<NormalizedDescription | null>(
    null,
  );
  const [rejectTarget, setRejectTarget] = useState<NormalizedDescription | null>(
    null,
  );

  // The queue used to re-sort each fetch newest-first in the browser. Once the list is paged that
  // is no longer a sort — it only reorders the rows in hand, so "newest" would mean "newest on
  // this page" while reading as a global ordering. The server's ordering (display name, then id)
  // is used as-is: it is stable across pages, and it puts near-duplicates next to each other,
  // which is exactly what a reviewer hunting merge candidates wants.

  if (isLoading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-16 w-full rounded-lg" />
        <Skeleton className="h-64 w-full rounded-lg" />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="rounded-lg border border-destructive p-6 text-center">
        <p className="text-destructive">Failed to load review queue.</p>
      </div>
    );
  }

  if (pending.length === 0) {
    return (
      <div className="space-y-4">
        <ReviewQueueExplainer />
        <div className="rounded-lg border p-6 text-center">
          <h2 className="card-title">Review Queue Empty</h2>
          <p className="mt-2 text-muted-foreground">
            No descriptions are awaiting review right now.
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <ReviewQueueExplainer />

      <div className="flex gap-6 rounded-lg border p-4">
        <div>
          <p className="card-sub">Pending Review</p>
          <p className="money-med">{total}</p>
        </div>
      </div>

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Canonical Name</TableHead>
            <TableHead>Nearest Match</TableHead>
            <TableHead className="text-right">Linked Items</TableHead>
            <TableHead>Created</TableHead>
            <TableHead className="text-right">Actions</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {pending.map((row) => (
            <TableRow key={row.id}>
              <TableCell className="font-medium">
                <EditableName row={row} />
                <SampleRawDescriptions samples={row.sampleRawDescriptions} />
              </TableCell>
              <TableCell>
                <NearestMatch
                  name={row.nearestNeighbourName}
                  similarity={row.nearestNeighbourSimilarity}
                />
              </TableCell>
              <TableCell className="text-right tabular-nums">
                {row.linkedItemCount}
              </TableCell>
              <TableCell>
                <span className="text-sm text-muted-foreground">
                  {new Date(row.createdAt).toLocaleDateString()}
                </span>
              </TableCell>
              {/* Every action carries a tooltip naming its consequence (RECEIPTS-874). Three
                  bare outline buttons used to sit here with no explanation anywhere except
                  inside the dialog you got *after* clicking — and Approve has no dialog at all,
                  so its consequences were never stated. */}
              <TableCell className="text-right space-x-2">
                <ActionButton
                  label="Approve"
                  hint={approveHint(row.linkedItemCount)}
                  disabled={updateStatus.isPending}
                  onClick={() =>
                    updateStatus.mutate({ id: row.id, status: "active" })
                  }
                />
                {/* "Merge into…" rather than "Merge": the ellipsis says a picker follows, and
                    "into" says the direction, which decides which of the two rows survives. */}
                <ActionButton
                  label="Merge into…"
                  hint={mergeHint(row.linkedItemCount)}
                  onClick={() => setMergeTarget(row)}
                />
                <ActionButton
                  label="Split"
                  hint="Move some of the linked items into a new entry of their own. Everything you leave unselected stays here."
                  onClick={() => setSplitTarget(row)}
                />
                {/* Reject sits apart from Merge deliberately. Merge says "this is the same as
                    X" and re-points the items; Reject says "this text is not worth a canonical
                    entry at all" and unlinks them. Before this existed the only way to dispose
                    of a bad entry was to merge it into an unrelated row (RECEIPTS-876). */}
                <ActionButton
                  label="Reject"
                  hint={rejectHint(row.linkedItemCount)}
                  onClick={() => setRejectTarget(row)}
                />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>

      <Pagination
        currentPage={pagination.currentPage}
        totalItems={total}
        pageSize={pagination.pageSize}
        totalPages={pagination.totalPages(total)}
        onPageChange={(page) => pagination.setPage(page, total)}
        onPageSizeChange={pagination.setPageSize}
      />

      <MergeDialog
        source={mergeTarget}
        onClose={() => setMergeTarget(null)}
      />
      <SplitDialog
        source={splitTarget}
        onClose={() => setSplitTarget(null)}
      />
      <RejectDialog
        source={rejectTarget}
        onClose={() => setRejectTarget(null)}
      />
    </div>
  );
}

function itemsPhrase(count: number) {
  return count === 1 ? "1 receipt item" : `${count} receipt items`;
}

/**
 * Per-action consequence copy (RECEIPTS-874).
 *
 * Each names what actually changes, and the count is folded in where an action moves data — the
 * difference between "Merge re-points the items" and "Merge re-points 47 items" is the difference
 * between a rule and a decision.
 */
function approveHint(linkedItemCount: number) {
  return `Accept this as a real entry. Nothing is re-linked or moved; its ${itemsPhrase(linkedItemCount)} stay where they are, and their spending stops being reported as unreviewed.`;
}

function mergeHint(linkedItemCount: number) {
  return `Say this is the same thing as another entry. Its ${itemsPhrase(linkedItemCount)} are re-pointed at the entry you pick and this one is deleted. Cannot be undone.`;
}

function rejectHint(linkedItemCount: number) {
  return `Say this text does not deserve an entry. Its ${itemsPhrase(linkedItemCount)} become unnormalized, and the resolver will not propose this text again.`;
}

interface ActionButtonProps {
  label: string;
  hint: string;
  disabled?: boolean;
  onClick: () => void;
}

/**
 * A row action whose consequence is readable before you commit to it.
 *
 * The hint is both a tooltip and the button's accessible description, so it reaches a keyboard or
 * screen-reader user as well as one who happens to hover. `aria-describedby` rather than
 * `aria-label`, because the label is the action and the hint is what it does — collapsing them
 * would make the button announce a paragraph where a verb belongs.
 */
function ActionButton({ label, hint, disabled, onClick }: ActionButtonProps) {
  const hintId = useId();

  return (
    <>
      <Tooltip>
        <TooltipTrigger asChild>
          <Button
            variant="outline"
            size="sm"
            disabled={disabled}
            onClick={onClick}
            aria-describedby={hintId}
          >
            {label}
          </Button>
        </TooltipTrigger>
        <TooltipContent className="max-w-xs">{hint}</TooltipContent>
      </Tooltip>
      <span id={hintId} className="sr-only">
        {hint}
      </span>
    </>
  );
}

/**
 * What this queue is and what the four actions do (RECEIPTS-874).
 *
 * Sits above the table rather than behind a help icon: the actions are destructive and mostly
 * irreversible, and a reviewer meeting the queue for the first time has no way to know that
 * Merge deletes a row. Collapsed by default after the first read would be nice, but a
 * remembered-dismissal is state we would have to persist and get wrong; a short block that is
 * cheap to skip is the better trade.
 */
function ReviewQueueExplainer() {
  return (
    <div
      className="rounded-lg border bg-muted/30 p-4 text-sm"
      data-testid="review-queue-explainer"
    >
      <h2 className="card-title">What is in this queue</h2>
      <p className="mt-1 text-muted-foreground">
        When a receipt item's text nearly matches an entry the registry already has — close enough
        to be suspicious, not close enough to be certain — the resolver creates a new entry and
        parks it here instead of guessing. The near-match that caused it is shown on each row.
        Its receipt items are already linked, and its spending already appears in reports, marked
        as unreviewed until you decide.
      </p>
      <dl className="mt-3 grid gap-2 sm:grid-cols-2">
        <div>
          <dt className="font-medium">Approve</dt>
          <dd className="text-muted-foreground">
            This is its own thing. Nothing moves; the “unreviewed” marking clears.
          </dd>
        </div>
        <div>
          <dt className="font-medium">Merge into…</dt>
          <dd className="text-muted-foreground">
            This is the same as an entry you already have. Its items are re-pointed there and{" "}
            <strong>this entry is deleted</strong>. Cannot be undone.
          </dd>
        </div>
        <div>
          <dt className="font-medium">Split</dt>
          <dd className="text-muted-foreground">
            Some of these items belong together under a different name. You pick which, and the
            rest stay here.
          </dd>
        </div>
        <div>
          <dt className="font-medium">Reject</dt>
          <dd className="text-muted-foreground">
            This text does not deserve an entry. Its items become unnormalized and the resolver
            stops proposing it.
          </dd>
        </div>
      </dl>
      <p className="mt-3 text-muted-foreground">
        You can also rename an entry in place. That changes the label only — never the receipt
        text it matches on — so renaming can never change what resolves here later.
      </p>
    </div>
  );
}

interface EditableNameProps {
  row: NormalizedDescription;
}

/**
 * The row's display name, editable in place (RECEIPTS-876).
 *
 * Editing writes `displayLabel` only. The matched text underneath is what the embedding is
 * anchored to, so it is shown read-only whenever a label diverges from it — a reviewer renaming
 * "MILK 2% GAL" to "Milk" still needs to see which receipt text the entry actually covers.
 */
function EditableName({ row }: EditableNameProps) {
  const rename = useRenameMutation();
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");

  function startEditing() {
    setDraft(row.displayLabel ?? row.canonicalName);
    setEditing(true);
  }

  function commit() {
    const trimmed = draft.trim();
    if (!trimmed) return;
    // Renaming a row back to its matched text is a clear, not a label that happens to match.
    const next = trimmed === row.canonicalName ? null : trimmed;
    if (next === (row.displayLabel ?? null)) {
      setEditing(false);
      return;
    }

    rename.mutate(
      { id: row.id, displayLabel: next },
      { onSuccess: () => setEditing(false) },
    );
  }

  if (editing) {
    return (
      <div className="flex items-center gap-2">
        <Input
          value={draft}
          // Focus follows the user's own click on "Rename", so it is focus management rather
          // than the page-load focus stealing that jsx-a11y/no-autofocus guards against. A
          // callback ref does the same job without tripping the rule.
          ref={(el) => el?.focus()}
          aria-label={`Display name for ${row.displayName}`}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") commit();
            if (e.key === "Escape") setEditing(false);
          }}
          className="h-8 max-w-[16rem]"
        />
        <Button
          size="sm"
          onClick={commit}
          disabled={!draft.trim() || rename.isPending}
        >
          {rename.isPending ? "Saving…" : "Save"}
        </Button>
        <Button
          variant="ghost"
          size="sm"
          onClick={() => setEditing(false)}
          disabled={rename.isPending}
        >
          Cancel
        </Button>
      </div>
    );
  }

  return (
    <div className="flex items-baseline gap-2">
      <span>{row.displayName}</span>
      <Button
        variant="ghost"
        size="sm"
        className="h-6 px-2 text-xs font-normal text-muted-foreground"
        onClick={startEditing}
        aria-label={`Rename ${row.displayName}`}
      >
        Rename
      </Button>
      {row.displayLabel && (
        <span className="text-xs font-normal text-muted-foreground">
          matches “{row.canonicalName}”
        </span>
      )}
    </div>
  );
}

interface RejectDialogProps {
  source: NormalizedDescription | null;
  onClose: () => void;
}

/**
 * Confirmation for the one action that both destroys links and is remembered (RECEIPTS-876).
 *
 * Spelled out rather than fired inline like Approve, because two consequences are non-obvious:
 * the linked items become unnormalized, and the resolver will not offer this text again.
 */
function RejectDialog({ source, onClose }: RejectDialogProps) {
  const updateStatus = useUpdateStatusMutation();
  const count = source?.linkedItemCount ?? 0;

  function handleConfirm() {
    if (!source) return;
    updateStatus.mutate(
      { id: source.id, status: "rejected" },
      { onSuccess: () => onClose() },
    );
  }

  return (
    <Dialog
      open={source !== null}
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Reject “{source?.displayName}”?</DialogTitle>
          <DialogDescription>
            {count === 0
              ? "No receipt items are linked to this entry."
              : `${count} receipt ${count === 1 ? "item" : "items"} will become unnormalized and report under "(Not Normalized)".`}{" "}
            The entry is kept as a record of your decision, so the resolver will not create it
            again the next time this text appears on a receipt. Use Merge instead if this is
            really the same item as an existing entry.
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

interface NearestMatchProps {
  name: string | null | undefined;
  similarity: number | null | undefined;
}

/**
 * The evidence that explains why a row is sitting in the review queue at all (RECEIPTS-873).
 *
 * The absent case is deliberately distinguished from a zero score: a row with no recorded
 * neighbour was never compared against anything, and rendering that as "0.00" would assert a
 * near-miss that never happened. Guards are nullish, not falsy, so a genuine similarity of 0
 * still prints as 0.00.
 */
function NearestMatch({ name, similarity }: NearestMatchProps) {
  if (similarity == null) {
    return (
      <span className="text-sm text-muted-foreground">
        No comparison recorded
      </span>
    );
  }

  // Merging the neighbour away nulls the FK (ON DELETE SET NULL) but leaves the score behind.
  // The score is still a true record of what happened, so we show it rather than pretending
  // no comparison was ever made.
  if (name == null) {
    return (
      <span className="text-sm">
        Nearly matched a since-removed entry at{" "}
        <span className="tabular-nums">{formatDecimal(similarity, 2)}</span>
      </span>
    );
  }

  return (
    <span className="text-sm">
      Nearly matched <span className="font-medium">{name}</span> at{" "}
      <span className="tabular-nums">{formatDecimal(similarity, 2)}</span>
    </span>
  );
}

/**
 * The raw receipt text this canonical row actually covers — the difference between approving a
 * name and approving a grouping.
 */
function SampleRawDescriptions({ samples }: { samples: string[] | undefined }) {
  if (!samples || samples.length === 0) return null;

  return (
    <p className="mt-1 text-xs font-normal text-muted-foreground">
      Seen as: {samples.join(", ")}
    </p>
  );
}

interface MergeDialogProps {
  source: NormalizedDescription | null;
  onClose: () => void;
}

/** Candidate window. Search runs server-side, so this bounds one page, not the searchable set. */
const MERGE_CANDIDATE_PAGE_SIZE = 50;

function MergeDialog({ source, onClose }: MergeDialogProps) {
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
  const { items: candidates, total } = useNormalizedDescriptions({
    status: ["Active", "PendingReview"],
    q: debouncedSearch,
    limit: MERGE_CANDIDATE_PAGE_SIZE,
    enabled: source !== null,
  });

  const filtered = useMemo(
    () => candidates.filter((c) => c.id !== source?.id),
    [candidates, source?.id],
  );

  const sourceCount = source?.linkedItemCount ?? 0;

  function handleClose() {
    setTargetId(undefined);
    setSearch("");
    onClose();
  }

  function handleConfirm() {
    if (!source || !targetId) return;
    merge.mutate(
      { id: targetId, discardId: source.id },
      { onSuccess: () => handleClose() },
    );
  }

  return (
    <Dialog
      open={source !== null}
      onOpenChange={(open) => {
        if (!open) handleClose();
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
              : `${sourceCount} receipt ${sourceCount === 1 ? "item" : "items"} linked to “${source?.displayName}” will be re-pointed at the chosen row.`}{" "}
            “{source?.displayName}” is then deleted. This cannot be undone.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          <div>
            <Label htmlFor="merge-search">Search active entries</Label>
            <Input
              id="merge-search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Start typing a canonical name…"
              className="mt-1"
            />
          </div>
          <div className="max-h-64 overflow-y-auto rounded border">
            {filtered.length === 0 ? (
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
          <Button variant="outline" onClick={handleClose}>
            Cancel
          </Button>
          <Button
            onClick={handleConfirm}
            disabled={!targetId || merge.isPending}
          >
            {merge.isPending ? "Merging…" : "Merge"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

interface SplitDialogProps {
  source: NormalizedDescription | null;
  onClose: () => void;
}

function SplitDialog({ source, onClose }: SplitDialogProps) {
  const split = useSplitMutation();
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [name, setName] = useState("");
  const [nameTouched, setNameTouched] = useState(false);
  const [page, setPage] = useState(0);

  // Server-side, filtered to this entry. Every linked item is reachable by paging, rather than
  // only those that happened to fall in a fixed page of the unfiltered list (RECEIPTS-877).
  const { data: items, total, isLoading } = useLinkedReceiptItems(
    source?.id ?? null,
    page * SPLIT_PAGE_SIZE,
    SPLIT_PAGE_SIZE,
  );

  const linked = useMemo(() => (items ?? []) as ReceiptItem[], [items]);
  const totalPages = Math.ceil(total / SPLIT_PAGE_SIZE);

  function handleClose() {
    setSelectedIds([]);
    setName("");
    setNameTouched(false);
    setPage(0);
    onClose();
  }

  function toggle(itemId: string, description: string) {
    setSelectedIds((prev) => {
      const next = prev.includes(itemId)
        ? prev.filter((existing) => existing !== itemId)
        : [...prev, itemId];

      // Pre-fill from the first selection, but never overwrite what the reviewer typed.
      if (!nameTouched && next.length === 1 && !prev.includes(itemId)) {
        setName(description);
      }
      if (!nameTouched && next.length === 0) {
        setName("");
      }
      return next;
    });
  }

  const trimmedName = name.trim();
  const canSubmit = selectedIds.length > 0 && trimmedName.length > 0 && !split.isPending;

  function handleConfirm() {
    if (!source || !canSubmit) return;
    split.mutate(
      { id: source.id, receiptItemIds: selectedIds, canonicalName: trimmedName },
      { onSuccess: () => handleClose() },
    );
  }

  return (
    <Dialog
      open={source !== null}
      onOpenChange={(open) => {
        if (!open) handleClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Split Out Receipt Items</DialogTitle>
          <DialogDescription>
            Select the receipt items to detach from &quot;{source?.displayName}&quot; and give
            the group a name. They move together into one entry; anything you leave unselected
            stays where it is.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          {isLoading ? (
            <Skeleton className="h-24 w-full rounded" />
          ) : linked.length === 0 ? (
            // Says what is true — this entry has nothing linked — rather than the old
            // "we could not find them in the most recent 200 items", which was a statement
            // about the query rather than the data.
            <p className="text-sm text-muted-foreground" data-testid="split-empty">
              No receipt items are linked to this entry, so there is nothing to split out.
            </p>
          ) : (
            <>
              <ul className="max-h-64 overflow-y-auto divide-y rounded border">
                {linked.map((item) => (
                  <li key={item.id}>
                    <label className="flex cursor-pointer items-center gap-2 p-2 text-sm hover:bg-muted/50">
                      <input
                        type="checkbox"
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
                      onClick={() => setPage((p) => p - 1)}
                    >
                      Previous
                    </Button>
                    <Button
                      variant="outline"
                      size="sm"
                      disabled={page + 1 >= totalPages}
                      onClick={() => setPage((p) => p + 1)}
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
        <DialogFooter>
          <Button variant="outline" onClick={handleClose}>
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

/**
 * The Active registry: everything the resolver is currently allowed to match against.
 *
 * Two things changed here in RECEIPTS-879. Search and paging moved to the server — the tab used
 * to load every Active row and filter the array in the browser, which does not survive a registry
 * of a few thousand descriptions. And the tab stopped being read-only: it is the only place an
 * already-approved entry can be corrected, so without actions here every approval was permanent.
 */
function RegistryTab() {
  const pagination = useServerPagination({
    defaultPageSize: NORMALIZED_DESCRIPTION_PAGE_SIZE,
  });
  const [search, setSearch] = useState("");
  const debouncedSearch = useDebouncedValue(search);

  const {
    items,
    total,
    isLoading,
    isError,
  } = useNormalizedDescriptions({
    status: "Active",
    q: debouncedSearch,
    offset: pagination.offset,
    limit: pagination.limit,
  });

  const [mergeTarget, setMergeTarget] = useState<NormalizedDescription | null>(
    null,
  );
  const [splitTarget, setSplitTarget] = useState<NormalizedDescription | null>(
    null,
  );
  const [retireTarget, setRetireTarget] = useState<NormalizedDescription | null>(
    null,
  );

  function handleSearchChange(value: string) {
    setSearch(value);
    // A new search re-filters the whole set on the server, so the page the admin was on no
    // longer refers to anything. Staying on page 4 of the old result would show an empty table
    // for a search that has matches.
    pagination.resetPage();
  }

  if (isLoading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-16 w-full rounded-lg" />
        <Skeleton className="h-64 w-full rounded-lg" />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="rounded-lg border border-destructive p-6 text-center">
        <p className="text-destructive">Failed to load registry.</p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-end gap-4">
        <div className="flex-1 max-w-md">
          <Label htmlFor="registry-search">Search</Label>
          <Input
            id="registry-search"
            value={search}
            onChange={(e) => handleSearchChange(e.target.value)}
            placeholder="Search by name or matched receipt text…"
            className="mt-1"
          />
        </div>
        <div className="text-right">
          <p className="card-sub">
            {debouncedSearch ? "Matching Entries" : "Active Entries"}
          </p>
          <p className="money-med">{total}</p>
        </div>
      </div>
      {items.length === 0 ? (
        <div className="rounded-lg border p-6 text-center">
          <p className="text-muted-foreground">
            {debouncedSearch
              ? "No active entries match your search."
              : "No active entries yet."}
          </p>
        </div>
      ) : (
        <>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Canonical Name</TableHead>
                <TableHead className="text-right">Linked Items</TableHead>
                <TableHead>Created</TableHead>
                <TableHead className="text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((row) => (
                <TableRow key={row.id}>
                  <TableCell className="font-medium">
                    <EditableName row={row} />
                    <SampleRawDescriptions samples={row.sampleRawDescriptions} />
                  </TableCell>
                  {/* Makes runaway and near-empty entries visible: a row holding thousands of
                      items is probably over-matching, and one holding none is dead weight. */}
                  <TableCell className="text-right tabular-nums">
                    {row.linkedItemCount}
                  </TableCell>
                  <TableCell>
                    <span className="text-sm text-muted-foreground">
                      {new Date(row.createdAt).toLocaleDateString()}
                    </span>
                  </TableCell>
                  <TableCell className="text-right space-x-2">
                    <ActionButton
                      label="Merge into…"
                      hint={mergeHint(row.linkedItemCount)}
                      onClick={() => setMergeTarget(row)}
                    />
                    <ActionButton
                      label="Split"
                      hint="Move some of the linked items into a new entry of their own. Everything you leave unselected stays here."
                      onClick={() => setSplitTarget(row)}
                    />
                    {/* Send back to review rather than reject: an entry that looks wrong on
                        second thought is a judgement to redo, not a decision to record. Reject
                        stays in the review queue, where it tombstones the text for good. */}
                    <ActionButton
                      label="Send back to review"
                      hint={`Put this back in the review queue. Nothing is unlinked — its ${itemsPhrase(row.linkedItemCount)} stay attached — but its spending is reported as unreviewed again until you approve it.`}
                      onClick={() => setRetireTarget(row)}
                    />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>

          <Pagination
            currentPage={pagination.currentPage}
            totalItems={total}
            pageSize={pagination.pageSize}
            totalPages={pagination.totalPages(total)}
            onPageChange={(page) => pagination.setPage(page, total)}
            onPageSizeChange={pagination.setPageSize}
          />
        </>
      )}

      <MergeDialog source={mergeTarget} onClose={() => setMergeTarget(null)} />
      <SplitDialog source={splitTarget} onClose={() => setSplitTarget(null)} />
      <RetireDialog
        source={retireTarget}
        onClose={() => setRetireTarget(null)}
      />
    </div>
  );
}

interface RetireDialogProps {
  source: NormalizedDescription | null;
  onClose: () => void;
}

/**
 * Sends an Active entry back to the review queue (RECEIPTS-879).
 *
 * Confirmed rather than fired inline because the consequence is not local to this row: until it
 * is approved again, its spend renders as provisional in the spending report (RECEIPTS-875).
 * Nothing is unlinked — this is the reversible action, which is what separates it from Reject.
 */
function RetireDialog({ source, onClose }: RetireDialogProps) {
  const updateStatus = useUpdateStatusMutation();
  const count = source?.linkedItemCount ?? 0;

  function handleConfirm() {
    if (!source) return;
    updateStatus.mutate(
      { id: source.id, status: "pendingReview" },
      { onSuccess: () => onClose() },
    );
  }

  return (
    <Dialog
      open={source !== null}
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Send “{source?.displayName}” back to review?</DialogTitle>
          <DialogDescription>
            The entry returns to the review queue and its{" "}
            {count === 1 ? "one linked item stays" : `${count} linked items stay`}{" "}
            attached — nothing is unlinked and no receipt data changes. Until it is
            approved again its spending is reported as unreviewed. Approve it from
            the review queue to undo this.
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

function SettingsTab() {
  const settings = useSettings();

  if (settings.isLoading) {
    return <Skeleton className="h-48 w-full rounded-lg" />;
  }

  if (settings.isError || !settings.data) {
    return (
      <div className="rounded-lg border border-destructive p-6 text-center">
        <p className="text-destructive">Failed to load settings.</p>
      </div>
    );
  }

  return (
    <SettingsForm
      initialAutoAccept={settings.data.autoAcceptThreshold}
      initialPendingReview={settings.data.pendingReviewThreshold}
    />
  );
}

/**
 * Operational actions for the normalized-description registry (RECEIPTS-883).
 *
 * The only action today is the requeue: existing PendingReview rows predate near-miss capture
 * (RECEIPTS-873), so they render "No comparison recorded" forever. Deleting them lets the
 * background resolver rebuild each one with real evidence. It is not backfilled, because a
 * neighbour computed now would be measured against today's registry and could name an entry
 * that did not exist when the row was created.
 */
function MaintenanceTab() {
  const preview = useRequeuePendingPreview();
  const [confirming, setConfirming] = useState(false);

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
            recorded. Requeueing deletes them so the background resolver rebuilds
            each one from scratch, this time with the evidence attached. Active
            entries are never touched.
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
            onClick={() => setConfirming(true)}
          >
            Requeue {pendingDescriptionCount} pending{" "}
            {pendingDescriptionCount === 1 ? "description" : "descriptions"}
          </Button>
        </CardContent>
      </Card>

      <RequeueConfirmDialog
        open={confirming}
        pendingDescriptionCount={pendingDescriptionCount}
        pendingFingerprint={pendingFingerprint}
        linkedItemCount={linkedItemCount}
        onClose={() => setConfirming(false)}
      />
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
            {linkedItemCount} receipt {linkedItemCount === 1 ? "item" : "items"}.
            Any review judgement already applied to those entries is discarded,
            and the items stay unnormalized until the resolver rebuilds them.
            Active entries are not affected. This cannot be undone — take a
            backup first if you are running against production.
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

interface SettingsFormProps {
  initialAutoAccept: number;
  initialPendingReview: number;
}

function SettingsForm({
  initialAutoAccept,
  initialPendingReview,
}: SettingsFormProps) {
  const updateSettings = useUpdateSettingsMutation();
  const previewImpact = usePreviewImpactMutation();
  const testMatch = useTestMatchMutation();

  const [autoAccept, setAutoAccept] = useState(() => String(initialAutoAccept));
  const [pendingReview, setPendingReview] = useState(() =>
    String(initialPendingReview),
  );
  const [testDescription, setTestDescription] = useState("");
  const [topN, setTopN] = useState(5);

  const autoVal = parseFloat(autoAccept);
  const pendingVal = parseFloat(pendingReview);
  const autoValid = Number.isFinite(autoVal) && autoVal >= 0 && autoVal <= 1;
  const pendingValid =
    Number.isFinite(pendingVal) && pendingVal >= 0 && pendingVal <= 1;
  const orderValid = autoValid && pendingValid && pendingVal < autoVal;
  const canSubmit = autoValid && pendingValid && orderValid;

  function validationMessage() {
    if (!autoValid || !pendingValid) {
      return "Thresholds must be numbers between 0 and 1.";
    }
    if (!orderValid) {
      return "Pending-review threshold must be strictly less than the auto-accept threshold.";
    }
    return null;
  }

  function handleSave() {
    if (!canSubmit) return;
    updateSettings.mutate({
      autoAcceptThreshold: autoVal,
      pendingReviewThreshold: pendingVal,
    });
  }

  function handlePreview() {
    if (!canSubmit) return;
    previewImpact.mutate({
      autoAcceptThreshold: autoVal,
      pendingReviewThreshold: pendingVal,
    });
  }

  function handleTest() {
    const trimmed = testDescription.trim();
    if (!trimmed) return;
    testMatch.mutate({
      description: trimmed,
      topN,
      autoAcceptThresholdOverride: autoValid ? autoVal : undefined,
      pendingReviewThresholdOverride: pendingValid ? pendingVal : undefined,
    });
  }

  const validationError = validationMessage();

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle>Thresholds</CardTitle>
          <CardDescription>
            Auto-accept is the similarity score at which the resolver re-uses an
            existing canonical entry. Pending-review is the floor for flagging a
            new description for admin review. Both are between 0 and 1, and
            pending-review must be strictly less than auto-accept.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div>
              <Label htmlFor="auto-accept-input">Auto-Accept Threshold</Label>
              <Input
                id="auto-accept-input"
                type="number"
                min={0}
                max={1}
                step={0.01}
                value={autoAccept}
                onChange={(e) => setAutoAccept(e.target.value)}
                className="mt-1"
                aria-invalid={!autoValid}
              />
            </div>
            <div>
              <Label htmlFor="pending-review-input">
                Pending-Review Threshold
              </Label>
              <Input
                id="pending-review-input"
                type="number"
                min={0}
                max={1}
                step={0.01}
                value={pendingReview}
                onChange={(e) => setPendingReview(e.target.value)}
                className="mt-1"
                aria-invalid={!pendingValid || !orderValid}
              />
            </div>
          </div>
          {validationError && (
            <p
              role="alert"
              className="text-sm text-destructive"
              data-testid="threshold-validation-error"
            >
              {validationError}
            </p>
          )}
          <div className="flex gap-2">
            <Button
              onClick={handleSave}
              disabled={!canSubmit || updateSettings.isPending}
            >
              {updateSettings.isPending ? "Saving…" : "Save"}
            </Button>
            <Button
              variant="outline"
              onClick={handlePreview}
              disabled={!canSubmit || previewImpact.isPending}
            >
              {previewImpact.isPending ? "Computing…" : "Preview impact"}
            </Button>
          </div>
          {previewImpact.data && (
            <div
              className="rounded border p-3 text-sm"
              data-testid="preview-impact-panel"
            >
              <h3 className="card-title">Projected impact</h3>
              <div className="mt-2 grid grid-cols-3 gap-4">
                <div>
                  <p className="text-xs text-muted-foreground">Auto-accepted</p>
                  <p>
                    <span>{previewImpact.data.current.autoAccepted}</span>
                    {" \u2192 "}
                    <strong>{previewImpact.data.proposed.autoAccepted}</strong>
                  </p>
                </div>
                <div>
                  <p className="text-xs text-muted-foreground">
                    Pending review
                  </p>
                  <p>
                    <span>{previewImpact.data.current.pendingReview}</span>
                    {" \u2192 "}
                    <strong>{previewImpact.data.proposed.pendingReview}</strong>
                  </p>
                </div>
                <div>
                  <p className="text-xs text-muted-foreground">Unresolved</p>
                  <p>
                    <span>{previewImpact.data.current.unresolved}</span>
                    {" \u2192 "}
                    <strong>{previewImpact.data.proposed.unresolved}</strong>
                  </p>
                </div>
              </div>
              <p className="mt-2 text-xs text-muted-foreground">
                Auto-to-pending: {previewImpact.data.deltas.autoToPending} |
                Pending-to-auto: {previewImpact.data.deltas.pendingToAuto} |
                Unresolved-to-auto: {previewImpact.data.deltas.unresolvedToAuto}{" "}
                | Unresolved-to-pending:{" "}
                {previewImpact.data.deltas.unresolvedToPending}
              </p>
            </div>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Test a Description</CardTitle>
          <CardDescription>
            Run any description through the classifier with the currently-edited
            thresholds (or the live values when the inputs are blank) to see
            which canonical rows would match.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="flex flex-wrap items-end gap-3">
            <div className="flex-1 min-w-[220px]">
              <Label htmlFor="test-description-input">Description</Label>
              <Input
                id="test-description-input"
                value={testDescription}
                onChange={(e) => setTestDescription(e.target.value)}
                placeholder="e.g. banana"
                className="mt-1"
              />
            </div>
            <div className="w-28">
              <Label htmlFor="test-topn-input">Top N</Label>
              <Select
                value={String(topN)}
                onValueChange={(v) => setTopN(Number(v))}
              >
                <SelectTrigger
                  id="test-topn-input"
                  className="mt-1 w-full"
                  aria-label="Top N candidates"
                >
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {[3, 5, 10, 20].map((n) => (
                    <SelectItem key={n} value={String(n)}>
                      {n}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <Button
              onClick={handleTest}
              disabled={!testDescription.trim() || testMatch.isPending}
            >
              {testMatch.isPending ? "Testing…" : "Test"}
            </Button>
          </div>
          {testMatch.data && (
            <div
              className="rounded border p-3 text-sm"
              data-testid="test-match-panel"
            >
              <div className="flex items-center gap-2">
                <span className="font-semibold">Simulated outcome:</span>
                <Badge variant="secondary">
                  {testMatch.data.simulatedOutcome}
                </Badge>
              </div>
              {testMatch.data.candidates.length === 0 ? (
                <p className="mt-2 text-muted-foreground">
                  No candidates returned.
                </p>
              ) : (
                <Table className="mt-2">
                  <TableHeader>
                    <TableRow>
                      <TableHead>Canonical Name</TableHead>
                      <TableHead>Status</TableHead>
                      <TableHead className="text-right">Similarity</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {testMatch.data.candidates.map((c) => (
                      <TableRow key={c.normalizedDescriptionId}>
                        <TableCell className="font-medium">
                          {c.canonicalName}
                        </TableCell>
                        <TableCell>
                          <Badge
                            variant={
                              c.status === "Active" ? "default" : "secondary"
                            }
                          >
                            {c.status}
                          </Badge>
                        </TableCell>
                        <TableCell className="text-right money">
                          {formatDecimal(c.cosineSimilarity, 4)}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
