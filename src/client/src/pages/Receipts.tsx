import { useState, useMemo, useEffect, useCallback } from "react";
import { generateId } from "@/lib/id";
import { Link } from "react-router";
import {
  useReceipts,
  useCreateReceipt,
  useUpdateReceipt,
  useDeleteReceipts,
} from "@/hooks/useReceipts";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useFuzzySearch } from "@/hooks/useFuzzySearch";
import { useSavedFilters } from "@/hooks/useSavedFilters";
import { useServerPagination } from "@/hooks/useServerPagination";
import { useServerSort } from "@/hooks/useServerSort";
import { useListKeyboardNav } from "@/hooks/useListKeyboardNav";
import { useEntityLinkParams } from "@/hooks/useEntityLinkParams";
import type { FuseSearchConfig, FilterDefinition } from "@/lib/search";
import { applyFilters } from "@/lib/search";
import type { FilterValues } from "@/components/FilterPanel";
import { ReceiptForm } from "@/components/ReceiptForm";
import { FuzzySearchInput } from "@/components/FuzzySearchInput";
import { FilterPanel } from "@/components/FilterPanel";
import type { FilterField } from "@/components/FilterPanel";
import { SearchHighlight } from "@/components/SearchHighlight";
import { getMatchIndices } from "@/lib/search-highlight";
import { SortableTableHead } from "@/components/SortableTableHead";
import { NoResults } from "@/components/NoResults";
import { Pagination } from "@/components/Pagination";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { TableSkeleton } from "@/components/ui/table-skeleton";
import { Spinner } from "@/components/ui/spinner";
import { formatCurrency } from "@/lib/format";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Info, Pencil } from "lucide-react";
import { useReceiptYnabSyncStatuses } from "@/hooks/useYnab";
import { YnabSyncBadge } from "@/components/YnabSyncBadge";

interface ReceiptResponse {
  id: string;
  location: string;
  date: string;
  taxAmount: number;
}

const SEARCH_CONFIG: FuseSearchConfig<ReceiptResponse> = {
  keys: [
    { name: "location", weight: 1.5 },
  ],
};

const FILTER_FIELDS: FilterField[] = [
  { type: "dateRange", key: "date", label: "Date" },
  { type: "numberRange", key: "taxAmount", label: "Tax Amount" },
];

const FILTER_DEFS: FilterDefinition[] = [
  { key: "date", type: "dateRange", field: "date" },
  { key: "taxAmount", type: "numberRange", field: "taxAmount" },
];

const HIGHLIGHT_PARAMS = ["highlight"] as const;

function Receipts() {
  usePageTitle("Receipts");
  const { params: linkParams } = useEntityLinkParams(HIGHLIGHT_PARAMS);
  const { sortBy, sortDirection, toggleSort } = useServerSort({ defaultSortBy: "date", defaultSortDirection: "desc" });
  const { offset, limit, currentPage, pageSize, totalPages, setPage, setPageSize, resetPage } = useServerPagination({ sortBy, sortDirection });
  const { data: receiptsData, total: serverTotal, isLoading } = useReceipts(offset, limit, sortBy, sortDirection);
  const createReceipt = useCreateReceipt();
  const updateReceipt = useUpdateReceipt();
  const deleteReceipts = useDeleteReceipts();

  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [createOpen, setCreateOpen] = useState(false);
  const [editReceipt, setEditReceipt] = useState<ReceiptResponse | null>(null);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [filterValues, setFilterValues] = useState<FilterValues>({});

  const anyDialogOpen = createOpen || editReceipt !== null || deleteOpen;

  useEffect(() => {
    function onNewItem() {
      setCreateOpen(true);
    }
    window.addEventListener("shortcut:new-item", onNewItem);
    return () => window.removeEventListener("shortcut:new-item", onNewItem);
  }, []);

  const handleSort = useCallback((column: string) => {
    toggleSort(column);
    resetPage();
  }, [toggleSort, resetPage]);

  const data = (receiptsData as ReceiptResponse[] | undefined) ?? [];

  const receiptIds = useMemo(() => data.map((r) => r.id), [data]);
  const { statusMap: syncStatusMap } = useReceiptYnabSyncStatuses(receiptIds);

  const {
    filters: savedFilters,
    save: saveFilter,
    remove: removeFilter,
  } = useSavedFilters("receipts");

  const { search, setSearch, results, totalCount, clearSearch } =
    useFuzzySearch({ data, config: SEARCH_CONFIG });

  const filteredResults = useMemo(() => {
    const items = search.trim()
      ? results.map((r) => r.item)
      : results.map((r) => r.item);
    return applyFilters(items, FILTER_DEFS, filterValues);
  }, [results, filterValues, search]);

  const matchMap = useMemo(() => {
    const map = new Map<string, (typeof results)[number]>();
    for (const r of results) {
      map.set(r.item.id, r);
    }
    return map;
  }, [results]);

  const highlightMissing =
    linkParams.highlight && data.length > 0 && !data.some((r) => r.id === linkParams.highlight);

  function toggleSelect(id: string) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  function toggleAll() {
    if (selected.size === filteredResults.length) {
      setSelected(new Set());
    } else {
      setSelected(new Set(filteredResults.map((r) => r.id)));
    }
  }

  const { focusedId, setFocusedIndex, tableRef } = useListKeyboardNav({
    items: filteredResults,
    getId: (r) => r.id,
    enabled: !anyDialogOpen,
    onOpen: (r) => setEditReceipt(r),
    onDelete: () => setDeleteOpen(true),
    onSelectAll: () =>
      setSelected(new Set(filteredResults.map((r) => r.id))),
    onDeselectAll: () => setSelected(new Set()),
    onToggleSelect: (r) => toggleSelect(r.id),
    selected,
  });

  if (isLoading) {
    return <TableSkeleton columns={6} />;
  }

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-semibold tracking-tight">Receipts</h1>
      <div className="flex items-center justify-between">
        <FuzzySearchInput
          aria-label="Search receipts"
          value={search}
          onChange={setSearch}
          placeholder="Search receipts..."
          resultCount={filteredResults.length}
          totalCount={totalCount}
          className="max-w-sm"
        />
        <div className="flex gap-2">
          {selected.size > 0 && (
            <Button variant="destructive" onClick={() => setDeleteOpen(true)}>
              Delete ({selected.size})
            </Button>
          )}
          <Button variant="outline" asChild>
            <Link to="/receipts/scan">Scan Receipt</Link>
          </Button>
          <Button variant="outline" asChild>
            <Link to="/receipts/new">New Receipt</Link>
          </Button>
          <Button onClick={() => setCreateOpen(true)}>Quick Add</Button>
        </div>
      </div>

      <FilterPanel
        fields={FILTER_FIELDS}
        values={filterValues}
        onChange={setFilterValues}
        savedFilters={savedFilters}
        onSaveFilter={(name) =>
          saveFilter({
            id: generateId(),
            name,
            entityType: "receipts",
            values: filterValues,
            createdAt: new Date().toISOString(),
          })
        }
        onDeleteFilter={removeFilter}
        onLoadFilter={(preset) =>
          setFilterValues(preset.values as FilterValues)
        }
      />

      {highlightMissing && (
        <Alert>
          <Info className="h-4 w-4" />
          <AlertDescription>The highlighted item is not on this page.</AlertDescription>
        </Alert>
      )}

      {filteredResults.length === 0 ? (
        search ? (
          <NoResults
            searchTerm={search}
            onClearSearch={clearSearch}
            onSelectSuggestion={setSearch}
            entityName="receipts"
          />
        ) : (
          <div className="py-12 text-center text-muted-foreground">
            No receipts yet. Create one to get started.
          </div>
        )
      ) : (
        <>
          <Pagination
            currentPage={currentPage}
            totalItems={serverTotal}
            pageSize={pageSize}
            totalPages={totalPages(serverTotal)}
            onPageChange={(page) => setPage(page, serverTotal)}
            onPageSizeChange={setPageSize}
          />
          <div className="rounded-md border" ref={tableRef}>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="w-12">
                    <input
                      type="checkbox"
                      aria-label="Select all rows"
                      checked={
                        selected.size === filteredResults.length &&
                        filteredResults.length > 0
                      }
                      onChange={toggleAll}
                      className="h-4 w-4 rounded border-gray-300"
                    />
                  </TableHead>
                  <SortableTableHead column="location" label="Location" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
                  <SortableTableHead column="date" label="Date" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
                  <SortableTableHead column="taxAmount" label="Tax Amount" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} className="text-right" />
                  <TableHead>YNAB</TableHead>
                  <TableHead>Detail</TableHead>
                  <TableHead className="w-24">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {filteredResults.map((receipt, index) => {
                  const result = matchMap.get(receipt.id);
                  const matches = result?.matches;
                  return (
                    <TableRow
                      key={receipt.id}
                      className={`cursor-pointer ${focusedId === receipt.id ? "bg-accent" : ""} ${linkParams.highlight === receipt.id ? "ring-2 ring-primary" : ""}`}
                      onClick={(e) => {
                        if ((e.target as HTMLElement).closest("button, input, a, [role='button']")) return;
                        setFocusedIndex(index);
                      }}
                    >
                      <TableCell>
                        <input
                          type="checkbox"
                          aria-label={`Select ${receipt.location}`}
                          checked={selected.has(receipt.id)}
                          onChange={() => toggleSelect(receipt.id)}
                          className="h-4 w-4 rounded border-gray-300"
                        />
                      </TableCell>
                      <TableCell>
                        <SearchHighlight
                          text={receipt.location}
                          indices={getMatchIndices(matches, "location")}
                        />
                      </TableCell>
                      <TableCell>{receipt.date}</TableCell>
                      <TableCell className="text-right">
                        {formatCurrency(receipt.taxAmount)}
                      </TableCell>
                      <TableCell>
                        <YnabSyncBadge status={syncStatusMap.get(receipt.id)} />
                      </TableCell>
                      <TableCell>
                        <Link to={`/receipts/${receipt.id}`} className="text-sm text-primary hover:underline">
                          View
                        </Link>
                      </TableCell>
                      <TableCell>
                        <Button
                          variant="ghost"
                          size="icon"
                          aria-label="Edit"
                          onClick={() => setEditReceipt(receipt)}
                        >
                          <Pencil className="h-4 w-4" />
                        </Button>
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>
          <Pagination
            currentPage={currentPage}
            totalItems={serverTotal}
            pageSize={pageSize}
            totalPages={totalPages(serverTotal)}
            onPageChange={(page) => setPage(page, serverTotal)}
            onPageSizeChange={setPageSize}
          />
        </>
      )}

      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Create Receipt</DialogTitle>
          </DialogHeader>
          <ReceiptForm
            mode="create"
            isSubmitting={createReceipt.isPending}
            onCancel={() => setCreateOpen(false)}
            onSubmit={(values) => {
              createReceipt.mutate(
                values,
                { onSuccess: () => setCreateOpen(false) },
              );
            }}
          />
        </DialogContent>
      </Dialog>

      <Dialog
        open={editReceipt !== null}
        onOpenChange={(open) => !open && setEditReceipt(null)}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Edit Receipt</DialogTitle>
          </DialogHeader>
          {editReceipt && (
            <ReceiptForm
              mode="edit"
              defaultValues={{
                location: editReceipt.location,
                date: editReceipt.date,
                taxAmount: editReceipt.taxAmount,
              }}
              isSubmitting={updateReceipt.isPending}
              onCancel={() => setEditReceipt(null)}
              onSubmit={(values) => {
                updateReceipt.mutate(
                  {
                    id: editReceipt.id,
                    ...values,
                  },
                  { onSuccess: () => setEditReceipt(null) },
                );
              }}
            />
          )}
        </DialogContent>
      </Dialog>

      <Dialog open={deleteOpen} onOpenChange={setDeleteOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete Receipts</DialogTitle>
          </DialogHeader>
          <p className="text-sm text-muted-foreground">
            Are you sure you want to delete {selected.size} receipt(s)? This
            action can be undone by restoring.
          </p>
          <div className="flex justify-end gap-2 pt-4">
            <Button variant="outline" onClick={() => setDeleteOpen(false)}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              disabled={deleteReceipts.isPending}
              onClick={() => {
                const ids = [...selected];
                setSelected(new Set());
                setDeleteOpen(false);
                deleteReceipts.mutate(ids);
              }}
            >
              {deleteReceipts.isPending && <Spinner size="sm" />}
              {deleteReceipts.isPending ? "Deleting..." : "Delete"}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}

export default Receipts;
