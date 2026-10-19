import { Pagination } from "@/components/Pagination";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import {
  NORMALIZED_DESCRIPTION_PAGE_SIZE,
  useNormalizedDescriptions,
} from "@/hooks/useNormalizedDescriptions";
import { useServerPagination } from "@/hooks/useServerPagination";
import { useState } from "react";
import { MergeDialog } from "./MergeDialog";
import {
  ActionButton,
  EditableName,
  LastSeen,
  SampleRawDescriptions,
  TemplateEvidence,
} from "./ReviewPresentation";
import { itemsPhrase, mergeHint } from "./action-copy";
import { SplitDialog } from "./SplitDialog";
import { RetireDialog } from "./StatusDialogs";
import type { NormalizedDescription } from "./types";
/**
 * The Active registry: everything the resolver is currently allowed to match against.
 *
 * Two things changed here in RECEIPTS-879. Search and paging moved to the server — the tab used
 * to load every Active row and filter the array in the browser, which does not survive a registry
 * of a few thousand descriptions. And the tab stopped being read-only: it is the only place an
 * already-approved entry can be corrected, so without actions here every approval was permanent.
 */
export function RegistryTab() {
  const pagination = useServerPagination({
    tableId: "normalized-registry",
    defaultPageSize: NORMALIZED_DESCRIPTION_PAGE_SIZE,
  });
  const [search, setSearch] = useState("");
  const debouncedSearch = useDebouncedValue(search);

  const { items, total, isLoading, isError } = useNormalizedDescriptions({
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
  const [retireTarget, setRetireTarget] =
    useState<NormalizedDescription | null>(null);

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
                <TableHead>Last Seen</TableHead>
                <TableHead>Created</TableHead>
                <TableHead className="text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((row) => (
                <TableRow key={row.id}>
                  <TableCell className="font-medium">
                    <EditableName row={row} />
                    <SampleRawDescriptions
                      samples={row.sampleRawDescriptions}
                    />
                    {/* Shown here as well as in the queue, for the opposite reason: in the
                        registry it is a warning. An entry a user curated by hand should not be
                        merged away or sent back to review on the same impulse as resolver
                        output (RECEIPTS-930). */}
                    <TemplateEvidence row={row} />
                  </TableCell>
                  {/* Makes runaway and near-empty entries visible: a row holding thousands of
                      items is probably over-matching, and one holding none is dead weight. */}
                  <TableCell className="text-right tabular-nums">
                    {row.linkedItemCount}
                  </TableCell>
                  <TableCell>
                    <LastSeen value={row.lastSeen} />
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
      {retireTarget && (
        <RetireDialog
          key={retireTarget.id}
          source={retireTarget}
          onClose={() => setRetireTarget(null)}
        />
      )}
    </div>
  );
}
