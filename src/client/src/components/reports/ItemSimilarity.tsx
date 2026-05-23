import { useCallback, useMemo, useRef, useState } from "react";
import { formatDistanceToNow } from "date-fns";
import {
  useItemSimilarityReport,
  useRefreshItemSimilarity,
  useRenameItemSimilarityGroup,
  type ItemSimilarityParams,
} from "@/hooks/useItemSimilarityReport";
import { usePermission } from "@/hooks/usePermission";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { formatDecimal } from "@/lib/format";

type SortColumn = "canonicalName" | "occurrences" | "maxSimilarity";
type SortDirection = "asc" | "desc";

interface RenameTarget {
  canonicalName: string;
  itemIds: string[];
  variants: string[];
}

export default function ItemSimilarity() {
  const [sortBy, setSortBy] = useState<SortColumn>("occurrences");
  const [sortDirection, setSortDirection] = useState<SortDirection>("desc");
  const [page, setPage] = useState(1);
  const [threshold, setThreshold] = useState(0.7);
  const [renameTarget, setRenameTarget] = useState<RenameTarget | null>(null);
  const [renameValue, setRenameValue] = useState("");
  const pageSize = 50;

  // Debounce threshold changes
  const [debouncedThreshold, setDebouncedThreshold] = useState(0.7);
  const debounceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const handleThresholdChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const value = parseFloat(e.target.value);
      setThreshold(value);

      if (debounceTimerRef.current) {
        clearTimeout(debounceTimerRef.current);
      }
      debounceTimerRef.current = setTimeout(() => {
        setDebouncedThreshold(value);
        setPage(1);
      }, 400);
    },
    [],
  );

  const params: ItemSimilarityParams = useMemo(
    () => ({
      threshold: debouncedThreshold,
      sortBy,
      sortDirection,
      page,
      pageSize,
    }),
    [debouncedThreshold, sortBy, sortDirection, page, pageSize],
  );

  const { data, isLoading, isError } = useItemSimilarityReport(params);
  const renameMutation = useRenameItemSimilarityGroup();
  const refreshMutation = useRefreshItemSimilarity();
  const { isAdmin } = usePermission();

  function handleSort(column: SortColumn) {
    if (sortBy === column) {
      setSortDirection((prev) => (prev === "asc" ? "desc" : "asc"));
    } else {
      setSortBy(column);
      setSortDirection(column === "canonicalName" ? "asc" : "desc");
    }
    setPage(1);
  }

  function sortIndicator(column: SortColumn) {
    if (sortBy !== column) return null;
    return sortDirection === "asc" ? " \u2191" : " \u2193";
  }

  function handleRenameClick(group: {
    canonicalName: string;
    itemIds: string[];
    variants: string[];
  }) {
    setRenameTarget({
      canonicalName: group.canonicalName,
      itemIds: group.itemIds,
      variants: group.variants,
    });
    setRenameValue(group.canonicalName);
  }

  function handleRenameConfirm() {
    if (!renameTarget || !renameValue.trim()) return;
    renameMutation.mutate(
      {
        itemIds: renameTarget.itemIds,
        newDescription: renameValue.trim(),
      },
      {
        onSuccess: () => {
          setRenameTarget(null);
          setRenameValue("");
        },
      },
    );
  }

  const totalPages = data ? Math.ceil(Number(data.totalCount ?? 0) / pageSize) : 0;

  if (isLoading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-20 w-full rounded-lg" />
        <Skeleton className="h-64 w-full rounded-lg" />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="rounded-lg border border-destructive p-6 text-center">
        <p className="text-destructive">
          Failed to load item similarity report.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-end gap-6 rounded-lg border p-4">
        <div className="flex-1">
          <Label htmlFor="threshold-slider">
            Similarity Threshold: {formatDecimal(threshold)}
          </Label>
          <input
            id="threshold-slider"
            type="range"
            min={0.3}
            max={0.95}
            step={0.05}
            value={threshold}
            onChange={handleThresholdChange}
            className="mt-2 w-full accent-primary"
          />
          <div className="mt-1 flex justify-between text-xs text-muted-foreground">
            <span>0.30 (loose)</span>
            <span>0.95 (strict)</span>
          </div>
        </div>
        <div className="text-right">
          <p className="card-sub">Groups Found</p>
          <p className="money-med">{data?.totalCount ?? 0}</p>
        </div>
      </div>

      <div className="flex items-center justify-between px-1">
        <p className="text-xs text-muted-foreground">
          {data?.computedAt
            ? `Last updated ${formatDistanceToNow(new Date(data.computedAt), { addSuffix: true })}`
            : "Report not yet computed — the background refresher will populate edges shortly."}
        </p>
        {isAdmin() && (
          <Button
            variant="outline"
            size="sm"
            onClick={() => refreshMutation.mutate()}
            disabled={refreshMutation.isPending}
          >
            {refreshMutation.isPending ? "Requesting…" : "Refresh now"}
          </Button>
        )}
      </div>

      {!data || data.totalCount === 0 ? (
        <div className="rounded-lg border p-6 text-center">
          <h2 className="card-title">No Similar Items</h2>
          <p className="mt-2 text-muted-foreground">
            No similar item descriptions found at this threshold. Try lowering
            the similarity threshold.
          </p>
        </div>
      ) : (
        <>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead
                  className="cursor-pointer select-none"
                  onClick={() => handleSort("canonicalName")}
                >
                  Canonical Name{sortIndicator("canonicalName")}
                </TableHead>
                <TableHead>Variants</TableHead>
                <TableHead
                  className="cursor-pointer select-none text-right"
                  onClick={() => handleSort("occurrences")}
                >
                  Occurrences{sortIndicator("occurrences")}
                </TableHead>
                <TableHead
                  className="cursor-pointer select-none text-right"
                  onClick={() => handleSort("maxSimilarity")}
                >
                  Max Similarity{sortIndicator("maxSimilarity")}
                </TableHead>
                <TableHead className="text-right">Action</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.groups.map((group) => (
                <TableRow key={group.canonicalName}>
                  <TableCell className="font-medium">
                    {group.canonicalName}
                  </TableCell>
                  <TableCell>
                    <div className="flex flex-wrap gap-1">
                      {group.variants.map((variant) => (
                        <Badge
                          key={variant}
                          variant={
                            variant === group.canonicalName
                              ? "default"
                              : "secondary"
                          }
                        >
                          {variant}
                        </Badge>
                      ))}
                    </div>
                  </TableCell>
                  <TableCell className="text-right money">
                    {group.occurrences}
                  </TableCell>
                  <TableCell className="text-right money">
                    {formatDecimal(Number(group.maxSimilarity ?? 0))}
                  </TableCell>
                  <TableCell className="text-right">
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() =>
                        handleRenameClick({
                          canonicalName: group.canonicalName,
                          itemIds: group.itemIds,
                          variants: group.variants,
                        })
                      }
                    >
                      Rename All
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>

          {totalPages > 1 && (
            <div className="flex items-center justify-between">
              <p className="text-sm text-muted-foreground">
                Page {page} of {totalPages}
              </p>
              <div className="flex gap-2">
                <Button
                  variant="outline"
                  size="sm"
                  disabled={page <= 1}
                  onClick={() => setPage((p) => p - 1)}
                >
                  Previous
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  disabled={page >= totalPages}
                  onClick={() => setPage((p) => p + 1)}
                >
                  Next
                </Button>
              </div>
            </div>
          )}
        </>
      )}

      <Dialog
        open={renameTarget !== null}
        onOpenChange={(open) => {
          if (!open) {
            setRenameTarget(null);
            setRenameValue("");
          }
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Rename Items</DialogTitle>
            <DialogDescription>
              Update the description for {renameTarget?.itemIds.length ?? 0}{" "}
              items in this group.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-4">
            <div>
              <Label htmlFor="rename-input">New Description</Label>
              <Input
                id="rename-input"
                value={renameValue}
                onChange={(e) => setRenameValue(e.target.value)}
                placeholder="Enter new description"
                className="mt-1"
              />
            </div>
            {renameTarget && (
              <div>
                <p className="text-sm text-muted-foreground">
                  Current variants:
                </p>
                <div className="mt-1 flex flex-wrap gap-1">
                  {renameTarget.variants.map((v) => (
                    <Badge key={v} variant="secondary">
                      {v}
                    </Badge>
                  ))}
                </div>
              </div>
            )}
          </div>
          <DialogFooter>
            <Button
              variant="outline"
              onClick={() => {
                setRenameTarget(null);
                setRenameValue("");
              }}
            >
              Cancel
            </Button>
            <Button
              onClick={handleRenameConfirm}
              disabled={
                !renameValue.trim() || renameMutation.isPending
              }
            >
              {renameMutation.isPending ? "Renaming..." : "Rename All"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
