import { Pagination } from "@/components/Pagination";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { useUpdateStatusMutation } from "@/hooks/useNormalizedDescriptionActions";
import {
  NORMALIZED_DESCRIPTION_PAGE_SIZE,
  useNormalizedDescriptions,
} from "@/hooks/useNormalizedDescriptions";
import { useServerPagination } from "@/hooks/useServerPagination";
import { useState } from "react";
import { LinkTemplateDialog } from "./LinkTemplateDialog";
import { MergeDialog } from "./MergeDialog";
import {
  ActionButton,
  EditableName,
  NearestMatch,
  ReviewQueueExplainer,
  SampleRawDescriptions,
  TemplateEvidence,
} from "./ReviewPresentation";
import {
  approveHint,
  linkTemplateHint,
  mergeHint,
  rejectHint,
} from "./action-copy";
import { SplitDialog } from "./SplitDialog";
import { RejectDialog } from "./StatusDialogs";
import type { NormalizedDescription } from "./types";
export function ReviewQueueTab() {
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
  const [rejectTarget, setRejectTarget] =
    useState<NormalizedDescription | null>(null);
  const [linkTarget, setLinkTarget] = useState<NormalizedDescription | null>(
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

      {/* Sized to its contents rather than stretched across the viewport (RECEIPTS-880). One
          number in a full-width bordered bar reads as a section header with nothing in it. */}
      <div className="inline-flex rounded-lg border px-4 py-3">
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
                <TemplateEvidence row={row} />
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
              {/* Wraps rather than overflowing. Five actions do not fit a narrow viewport on one
                  line, and a row that pushes the document wider is the bug the width sweep added
                  in RECEIPTS-880 exists to catch. */}
              <TableCell className="text-right">
                <div className="flex flex-wrap justify-end gap-2">
                  <ActionButton
                    label="Approve"
                    hint={approveHint(row.linkedItemCount)}
                    disabled={updateStatus.isPending}
                    onClick={() =>
                      updateStatus.mutate({ id: row.id, status: "active" })
                    }
                  />
                  {/* Next to Approve because it answers the same question — "is this a real
                      item?" — with better evidence: a template means the user already said yes
                      by hand (RECEIPTS-930). */}
                  <ActionButton
                    label="Link to template…"
                    hint={linkTemplateHint(row.linkedItemCount)}
                    onClick={() => setLinkTarget(row)}
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
                </div>
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

      {mergeTarget && (
        <MergeDialog
          key={mergeTarget.id}
          source={mergeTarget}
          onClose={() => setMergeTarget(null)}
        />
      )}
      {splitTarget && (
        <SplitDialog
          key={splitTarget.id}
          source={splitTarget}
          onClose={() => setSplitTarget(null)}
        />
      )}
      {rejectTarget && (
        <RejectDialog
          key={rejectTarget.id}
          source={rejectTarget}
          onClose={() => setRejectTarget(null)}
        />
      )}
      {linkTarget && (
        <LinkTemplateDialog
          key={linkTarget.id}
          source={linkTarget}
          onClose={() => setLinkTarget(null)}
        />
      )}
    </div>
  );
}
