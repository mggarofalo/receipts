import { Fragment, useState, useMemo, useEffect, useCallback } from "react";
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
import { usePersistedFilters } from "@/hooks/usePersistedFilters";
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
import { ErrorState } from "@/components/ErrorState";
import { Pagination } from "@/components/Pagination";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { TableSkeleton } from "@/components/ui/table-skeleton";
import { Spinner } from "@/components/ui/spinner";
import { Skeleton } from "@/components/ui/skeleton";
import { formatCurrency, formatDate } from "@/lib/format";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Info } from "lucide-react";
import {
  useReceiptYnabSyncStatuses,
  useBulkPushYnabTransactions,
} from "@/hooks/useYnab";
import { BulkActionsBar } from "@/components/BulkActionsBar";
import { useTripByReceiptId } from "@/hooks/useTrips";
import {
  Checkbox,
  Icon,
  PageHead,
  YnabChip,
  type YnabStatus,
} from "@/components/primitives";

interface ReceiptListItem {
  id: string;
  location: string;
  date: string;
  taxAmount: number;
  itemSubtotal: number;
  adjustmentTotal: number;
  expectedTotal: number;
  transactionTotal: number;
  balanceState: "noTransactions" | "balanced" | "outOfBalance";
  itemCount: number;
  categorySummary: string;
  paymentSummary: string;
}

const SEARCH_CONFIG: FuseSearchConfig<ReceiptListItem> = {
  keys: [{ name: "location", weight: 1.5 }],
};

const FILTER_FIELDS: FilterField[] = [
  { type: "dateRange", key: "date", label: "Date" },
  { type: "numberRange", key: "taxAmount", label: "Tax amount" },
];

const FILTER_DEFS: FilterDefinition[] = [
  { key: "date", type: "dateRange", field: "date" },
  { key: "taxAmount", type: "numberRange", field: "taxAmount" },
];

// `location` is the drill-down target from the Spending by Location report
// (RECEIPTS-841): an exact-match server-side filter, not the fuzzy client-side
// search box.
const HIGHLIGHT_PARAMS = [
  "highlight",
  "accountId",
  "cardId",
  "location",
] as const;

function syncStatusToChip(status: string | undefined | null): YnabStatus {
  switch (status) {
    case "Synced":
    case "AlreadySynced":
      return "synced";
    case "Pending":
      return "pending";
    case "Failed":
    case "Error":
      return "error";
    default:
      return "none";
  }
}

function ReceiptInlineDetails({
  receipt,
  ynabStatus,
}: {
  receipt: ReceiptListItem;
  ynabStatus: string | undefined;
}) {
  const {
    data: trip,
    isLoading,
    isError,
    refetch,
    isRefetching,
  } = useTripByReceiptId(receipt.id);

  if (isLoading) {
    return (
      <div className="receipt-inline-loading" role="status" aria-live="polite">
        <span className="sr-only">Loading receipt details…</span>
        <Skeleton className="h-5 w-2/3" />
        <Skeleton className="h-5 w-1/2" />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="receipt-inline-error" role="alert">
        <span>Couldn&apos;t load receipt details.</span>
        <button
          type="button"
          className="btn xs"
          disabled={isRefetching}
          onClick={() => void refetch()}
        >
          {isRefetching ? "Retrying…" : "Retry"}
        </button>
      </div>
    );
  }

  const items = trip?.receipt?.items ?? [];
  const subtotal = Number(trip?.receipt?.subtotal ?? receipt.itemSubtotal);
  const adjustmentTotal = Number(
    trip?.receipt?.adjustmentTotal ?? receipt.adjustmentTotal,
  );
  const taxAmount = Number(receipt.taxAmount);
  const expectedTotal = Number(
    trip?.receipt?.expectedTotal ?? receipt.expectedTotal,
  );
  const transactionTotal =
    trip?.transactions?.reduce(
      (sum, transaction) => sum + Number(transaction.transaction.amount ?? 0),
      0,
    ) ?? receipt.transactionTotal;
  const equationTerms = [
    { label: "Subtotal", amount: subtotal, operator: "" },
    ...(Math.abs(taxAmount) >= 0.005
      ? [
          {
            label: "Tax",
            amount: Math.abs(taxAmount),
            operator: taxAmount < 0 ? "−" : "+",
          },
        ]
      : []),
    ...(Math.abs(adjustmentTotal) >= 0.005
      ? [
          {
            label: "Adjustments",
            amount: Math.abs(adjustmentTotal),
            operator: adjustmentTotal < 0 ? "−" : "+",
          },
        ]
      : []),
  ];
  const balanceLabel =
    receipt.balanceState === "noTransactions"
      ? "No transactions"
      : receipt.balanceState === "outOfBalance"
        ? "Out of balance"
        : "Balanced";
  const ynabChipStatus = syncStatusToChip(ynabStatus);
  const equationAccessibleLabel = `${equationTerms
    .map((term) =>
      `${term.operator === "−" ? "minus " : term.operator === "+" ? "plus " : ""}${term.label} ${formatCurrency(term.amount)}`,
    )
    .join(", ")}; equals Expected ${formatCurrency(expectedTotal)}`;

  return (
    <div className="receipt-inline-detail">
      <div className="receipt-inline-heading">
        <span className="receipt-inline-label">Receipt breakdown</span>
        <Link to={`/receipts/${receipt.id}`} className="btn xs">
          Open receipt
        </Link>
      </div>
      <div className="receipt-inline-summary">
        <section className="receipt-inline-panel receipt-inline-balance">
          <span className="receipt-inline-label">Balance equation</span>
          <div
            className="receipt-equation"
            role="math"
            aria-label={equationAccessibleLabel}
          >
            {equationTerms.map((term) => (
              <Fragment key={term.label}>
                {term.operator && (
                  <span
                    className="receipt-equation-operator"
                    aria-hidden="true"
                  >
                    {term.operator}
                  </span>
                )}
                <span className="receipt-equation-term">
                  <span>{term.label}</span>
                  <strong>{formatCurrency(term.amount)}</strong>
                </span>
              </Fragment>
            ))}
            <span className="receipt-equation-operator" aria-hidden="true">
              =
            </span>
            <span className="receipt-equation-term receipt-equation-total">
              <span>Expected</span>
              <strong>{formatCurrency(expectedTotal)}</strong>
            </span>
          </div>
        </section>
        <section className="receipt-inline-panel">
          <span className="receipt-inline-label">Payments</span>
          <strong className="receipt-inline-primary">
            {formatCurrency(transactionTotal)}
          </strong>
          <div className="receipt-inline-pills">
            <span className="receipt-detail-pill">
              {receipt.paymentSummary || "No transactions"}
            </span>
            <span
              className={`chip ${receipt.balanceState === "balanced" ? "pos" : receipt.balanceState === "outOfBalance" ? "neg" : ""}`}
            >
              Reconciliation: {balanceLabel}
            </span>
          </div>
        </section>
        <section className="receipt-inline-panel">
          <span className="receipt-inline-label">Classification</span>
          <strong className="receipt-inline-primary">
            {receipt.categorySummary || "Uncategorized"}
          </strong>
          <div className="receipt-inline-pills">
            <YnabChip status={ynabChipStatus} />
            <span className="sr-only">
              YNAB: {ynabChipStatus === "none" ? "not synced" : ynabChipStatus}
            </span>
          </div>
        </section>
      </div>
      <section className="receipt-inline-items">
        <span className="receipt-inline-label">Items</span>
        {items.length === 0 ? (
          <span>No items</span>
        ) : (
          <ul>
            {items.slice(0, 5).map((item) => (
              <li key={item.id}>
                <span>
                  {Number(item.quantity ?? 0)} × {item.description}
                </span>
                <strong>
                  {formatCurrency(
                    Number(item.quantity ?? 0) * Number(item.unitPrice ?? 0),
                  )}
                </strong>
              </li>
            ))}
          </ul>
        )}
        {items.length > 5 && <span>+{items.length - 5} more</span>}
      </section>
    </div>
  );
}

function Receipts() {
  usePageTitle("Receipts");
  const { params: linkParams, clearParams: clearLinkParams } =
    useEntityLinkParams(HIGHLIGHT_PARAMS);
  const { sortBy, sortDirection, toggleSort } = useServerSort({
    defaultSortBy: "date",
    defaultSortDirection: "desc",
  });
  const {
    offset,
    limit,
    currentPage,
    pageSize,
    totalPages,
    setPage,
    setPageSize,
    resetPage,
  } = useServerPagination({ sortBy, sortDirection });
  const {
    data: receiptsData,
    total: serverTotal,
    isLoading,
    isError,
    isRefetching,
    refetch,
  } = useReceipts(
    offset,
    limit,
    sortBy,
    sortDirection,
    linkParams.accountId,
    linkParams.cardId,
    null,
    { location: linkParams.location },
  );
  const createReceipt = useCreateReceipt();
  const updateReceipt = useUpdateReceipt();
  const deleteReceipts = useDeleteReceipts();
  const bulkPushYnab = useBulkPushYnabTransactions();

  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [createOpen, setCreateOpen] = useState(false);
  const [editReceipt, setEditReceipt] = useState<ReceiptListItem | null>(null);
  const [deleteOpen, setDeleteOpen] = useState(false);
  // Filter state persists to localStorage so reload / fresh-tab returns the
  // user to their last-applied filter set (RECEIPTS-736).
  const [filterValues, setFilterValues] = usePersistedFilters("receipts");

  const anyDialogOpen = createOpen || editReceipt !== null || deleteOpen;

  useEffect(() => {
    function onNewItem() {
      setCreateOpen(true);
    }
    window.addEventListener("shortcut:new-item", onNewItem);
    return () => window.removeEventListener("shortcut:new-item", onNewItem);
  }, []);

  const handleSort = useCallback(
    (column: string) => {
      toggleSort(column);
      resetPage();
    },
    [toggleSort, resetPage],
  );

  const data = useMemo(
    () => (receiptsData as ReceiptListItem[] | undefined) ?? [],
    [receiptsData],
  );

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
    const items = results.map((r) => r.item);
    return applyFilters(items, FILTER_DEFS, filterValues);
  }, [results, filterValues]);

  // Expansion belongs to the current result set. Clear it as soon as paging,
  // filtering, or searching removes the row so returning later cannot silently
  // remount a detail query without a fresh user expansion.
  if (
    expandedId !== null &&
    !filteredResults.some((receipt) => receipt.id === expandedId)
  ) {
    setExpandedId(null);
  }

  const matchMap = useMemo(() => {
    const map = new Map<string, (typeof results)[number]>();
    for (const r of results) {
      map.set(r.item.id, r);
    }
    return map;
  }, [results]);

  const highlightMissing =
    linkParams.highlight &&
    data.length > 0 &&
    !data.some((r) => r.id === linkParams.highlight);

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

  const { focusedId, setFocusedIndex, tableRef, containerProps, getRowProps } =
    useListKeyboardNav({
      items: filteredResults,
      getId: (r) => r.id,
      listId: "receipts",
      enabled: !anyDialogOpen,
      onDelete: () => setDeleteOpen(true),
      onSelectAll: () => setSelected(new Set(filteredResults.map((r) => r.id))),
      onDeselectAll: () => setSelected(new Set()),
      onToggleSelect: (r) => toggleSelect(r.id),
      onFocusSearch: () => {
        // Search lives outside useListKeyboardNav's scope — focus it by id.
        // Using getElementById (vs. a ref) keeps the hook agnostic about
        // the input component and lets it work with any input on the page.
        const input = document.getElementById("receipts-search");
        if (input instanceof HTMLInputElement) {
          input.focus();
          input.select();
        }
      },
      selected,
    });

  return (
    <>
      <PageHead
        title="Receipts"
        sub={`${serverTotal} total · ${filteredResults.length} shown`}
        actions={
          <>
            <button
              type="button"
              className="btn"
              onClick={() => setCreateOpen(true)}
            >
              <Icon.Plus /> Quick add
            </button>
            <Link to="/receipts/new" className="btn primary">
              <Icon.Plus /> New receipt
            </Link>
          </>
        }
      />

      <div className="filter-strip">
        <div style={{ flex: 1, minWidth: 240 }}>
          <FuzzySearchInput
            id="receipts-search"
            aria-label="Search receipts"
            value={search}
            onChange={setSearch}
            placeholder="Search receipts…"
            resultCount={filteredResults.length}
            totalCount={totalCount}
          />
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
      </div>

      {(linkParams.accountId || linkParams.cardId || linkParams.location) && (
        <Alert style={{ marginBottom: 14 }}>
          <Info className="h-4 w-4" />
          <AlertDescription className="flex items-center justify-between gap-4">
            <span>
              {linkParams.location ? (
                <>Filtered to receipts at “{linkParams.location}”.</>
              ) : (
                <>
                  Filtered to receipts with transactions on the selected{" "}
                  {linkParams.cardId ? "card" : "account"}.
                </>
              )}
            </span>
            <button
              type="button"
              className="btn xs ghost"
              onClick={() => {
                clearLinkParams();
                resetPage();
              }}
            >
              Clear filter
            </button>
          </AlertDescription>
        </Alert>
      )}

      {highlightMissing && (
        <Alert style={{ marginBottom: 14 }}>
          <Info className="h-4 w-4" />
          <AlertDescription>
            The highlighted item is not on this page.
          </AlertDescription>
        </Alert>
      )}

      {isError && data.length === 0 ? (
        // A *failed initial load* (error with no cached rows) must not
        // masquerade as an empty list (RECEIPTS-784). A background-refetch
        // error while rows are already on screen keeps the data visible —
        // the global handler still toasts the failure.
        <ErrorState
          title="Couldn't load receipts"
          message="Something went wrong loading your receipts. Check your connection and try again."
          onRetry={() => void refetch()}
          isRetrying={isRefetching}
        />
      ) : isLoading ? (
        <TableSkeleton columns={6} />
      ) : filteredResults.length === 0 ? (
        // Three-way split: search vs. filters vs. genuinely empty. The
        // first two surface "you can clear what you applied"; the last is
        // the first-run "create your first receipt" CTA (RECEIPTS-742).
        search ? (
          <NoResults
            searchTerm={search}
            onClearSearch={clearSearch}
            onSelectSuggestion={setSearch}
            entityName="receipts"
          />
        ) : Object.keys(filterValues).length > 0 ? (
          <div className="empty">
            <div className="icon-frame">
              <Icon.Inbox />
            </div>
            <h3>No receipts match your filters</h3>
            <p>Try widening the date range or clearing filters.</p>
            <div className="actions">
              <button
                type="button"
                className="btn"
                onClick={() => setFilterValues({})}
              >
                Clear filters
              </button>
            </div>
          </div>
        ) : (
          <div className="empty">
            <div className="icon-frame">
              <Icon.Inbox />
            </div>
            <h3>No receipts yet</h3>
            <p>Add your first receipt to start tracking spend.</p>
            <div className="actions">
              <Link to="/receipts/new" className="btn primary">
                <Icon.Plus /> New receipt
              </Link>
            </div>
          </div>
        )
      ) : (
        <>
          <div className="receipts-pagination receipts-pagination-top">
            <Pagination
              currentPage={currentPage}
              totalItems={serverTotal}
              pageSize={pageSize}
              totalPages={totalPages(serverTotal)}
              onPageChange={(page) => setPage(page, serverTotal)}
              onPageSizeChange={setPageSize}
            />
          </div>
          {/* Keyboard events bubble from the focusable table rows; the card itself
              deliberately stays out of the tab order and accessibility tree. */}
          {/* eslint-disable-next-line jsx-a11y/no-static-element-interactions */}
          <div
            className="card receipts-table-card"
            style={{ padding: 0 }}
            ref={tableRef}
            {...containerProps}
            onKeyDown={(e) => {
              if (
                (e.target as HTMLElement).closest(
                  "button, input, a, [role='button'], [role='checkbox']",
                )
              )
                return;
              if (!focusedId) return;
              if (e.key === "Enter") {
                e.preventDefault();
                setExpandedId((current) =>
                  current === focusedId ? null : focusedId,
                );
              } else if (e.key === "ArrowRight") {
                e.preventDefault();
                setExpandedId(focusedId);
              } else if (e.key === "ArrowLeft") {
                e.preventDefault();
                setExpandedId((current) =>
                  current === focusedId ? null : current,
                );
              }
            }}
          >
            <table className="tbl receipts-table">
              <thead>
                <tr>
                  <th className="receipt-select">
                    <Checkbox
                      aria-label="Select all rows"
                      on={
                        selected.size === filteredResults.length &&
                        filteredResults.length > 0
                      }
                      onClick={toggleAll}
                    />
                  </th>
                  <SortableTableHead
                    column="date"
                    label="Date"
                    currentSortBy={sortBy}
                    currentSortDirection={sortDirection}
                    onToggleSort={handleSort}
                  />
                  <SortableTableHead
                    column="location"
                    label="Merchant"
                    currentSortBy={sortBy}
                    currentSortDirection={sortDirection}
                    onToggleSort={handleSort}
                  />
                  <SortableTableHead
                    column="expectedTotal"
                    label="Total"
                    currentSortBy={sortBy}
                    currentSortDirection={sortDirection}
                    onToggleSort={handleSort}
                    className="num-h"
                  />
                  <th className="receipt-col-secondary">Payment</th>
                  <th className="receipt-col-secondary receipt-col-contents">
                    Contents
                  </th>
                  <th style={{ width: 180 }}>Status</th>
                  <th className="receipt-actions">Actions</th>
                </tr>
              </thead>
              <tbody>
                {filteredResults.map((receipt, index) => {
                  const result = matchMap.get(receipt.id);
                  const matches = result?.matches;
                  const isFocused = focusedId === receipt.id;
                  const isSelected = selected.has(receipt.id);
                  const isHighlighted = linkParams.highlight === receipt.id;
                  const rowClass = [
                    isFocused ? "focused" : "",
                    isSelected ? "selected" : "",
                  ]
                    .filter(Boolean)
                    .join(" ");
                  const isExpanded = expandedId === receipt.id;
                  const detailId = `receipt-detail-${receipt.id}`;
                  const rowProps = getRowProps(receipt.id);
                  const toggleExpanded = () =>
                    setExpandedId((current) =>
                      current === receipt.id ? null : receipt.id,
                    );
                  return (
                    <Fragment key={receipt.id}>
                      <tr
                        {...rowProps}
                        className={rowClass}
                        aria-expanded={isExpanded}
                        aria-controls={isExpanded ? detailId : undefined}
                        style={
                          isHighlighted
                            ? {
                                boxShadow:
                                  "inset 0 0 0 2px var(--accent), inset 2px 0 0 var(--accent)",
                              }
                            : undefined
                        }
                        onClick={(e) => {
                          if (
                            (e.target as HTMLElement).closest(
                              "button, input, a, [role='button'], [role='checkbox']",
                            )
                          )
                            return;
                          setFocusedIndex(index);
                          toggleExpanded();
                        }}
                      >
                        <td
                          className="receipt-select"
                          onClick={(e) => e.stopPropagation()}
                        >
                          <Checkbox
                            aria-label={`Select ${receipt.location}`}
                            on={isSelected}
                            onClick={() => toggleSelect(receipt.id)}
                          />
                        </td>
                        <td
                          className="num receipt-date"
                          style={{ color: "var(--mute)", fontSize: 12 }}
                        >
                          {formatDate(receipt.date)}
                        </td>
                        <td className="receipt-merchant">
                          <span style={{ fontWeight: 500 }}>
                            <SearchHighlight
                              text={receipt.location}
                              indices={getMatchIndices(matches, "location")}
                            />
                          </span>
                        </td>
                        <td className="money receipt-total">
                          {formatCurrency(receipt.expectedTotal)}
                        </td>
                        <td className="receipt-col-secondary">
                          {receipt.paymentSummary || (
                            <span className="receipt-empty-summary">
                              Unpaid
                            </span>
                          )}
                        </td>
                        <td className="receipt-col-secondary receipt-col-contents">
                          <span>
                            {receipt.itemCount} item
                            {receipt.itemCount === 1 ? "" : "s"}
                          </span>
                          {receipt.categorySummary && (
                            <span className="receipt-summary-detail">
                              {" "}
                              · {receipt.categorySummary}
                            </span>
                          )}
                        </td>
                        <td className="receipt-status">
                          <div className="receipt-statuses">
                            <span
                              className={`chip ${
                                receipt.balanceState === "balanced"
                                  ? "pos"
                                  : receipt.balanceState === "outOfBalance"
                                    ? "neg"
                                    : ""
                              }`}
                              aria-label={`Balance: ${
                                receipt.balanceState === "noTransactions"
                                  ? "no transactions"
                                  : receipt.balanceState === "outOfBalance"
                                    ? "out of balance"
                                    : "balanced"
                              }`}
                            >
                              {receipt.balanceState === "noTransactions"
                                ? "No transactions"
                                : receipt.balanceState === "outOfBalance"
                                  ? "Out of balance"
                                  : "Balanced"}
                            </span>
                            <YnabChip
                              status={syncStatusToChip(
                                syncStatusMap.get(receipt.id),
                              )}
                            />
                          </div>
                        </td>
                        <td
                          className="receipt-actions"
                          onClick={(e) => e.stopPropagation()}
                        >
                          <div className="row-actions">
                            <Link
                              to={`/receipts/${receipt.id}`}
                              className="icon-btn"
                              aria-label="View"
                            >
                              <Icon.Arrow />
                            </Link>
                            <button
                              type="button"
                              className="icon-btn"
                              aria-label="Edit"
                              onClick={() => setEditReceipt(receipt)}
                            >
                              <Icon.Edit />
                            </button>
                          </div>
                        </td>
                      </tr>
                      {isExpanded && (
                        <tr id={detailId} className="receipt-detail-row">
                          <td colSpan={8}>
                            <ReceiptInlineDetails
                              receipt={receipt}
                              ynabStatus={syncStatusMap.get(receipt.id)}
                            />
                          </td>
                        </tr>
                      )}
                    </Fragment>
                  );
                })}
              </tbody>
            </table>
          </div>
          <div className="receipts-pagination receipts-pagination-bottom">
            <Pagination
              currentPage={currentPage}
              totalItems={serverTotal}
              pageSize={pageSize}
              totalPages={totalPages(serverTotal)}
              onPageChange={(page) => setPage(page, serverTotal)}
              onPageSizeChange={setPageSize}
            />
          </div>
          <BulkActionsBar
            selectedCount={selected.size}
            totalCount={filteredResults.length}
            itemLabel="receipt"
            onClearSelection={() => setSelected(new Set())}
            onDelete={() => setDeleteOpen(true)}
            onPushToYnab={() => {
              bulkPushYnab.mutate(Array.from(selected), {
                onSuccess: (data) => {
                  // Only clear the receipts that actually pushed; keep the
                  // failed ones selected so the user can retry. RECEIPTS-783.
                  const succeededIds = new Set(
                    (data?.results ?? [])
                      .filter((r) => r.result.success)
                      .map((r) => r.receiptId),
                  );
                  setSelected((prev) => {
                    const next = new Set(prev);
                    for (const id of succeededIds) next.delete(id);
                    return next;
                  });
                },
              });
            }}
            isPushingToYnab={bulkPushYnab.isPending}
            isDeleting={deleteReceipts.isPending}
          />
        </>
      )}

      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Create receipt</DialogTitle>
          </DialogHeader>
          <ReceiptForm
            mode="create"
            isSubmitting={createReceipt.isPending}
            onCancel={() => setCreateOpen(false)}
            onSubmit={(values) => {
              createReceipt.mutate(values, {
                onSuccess: () => setCreateOpen(false),
              });
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
            <DialogTitle>Edit receipt</DialogTitle>
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
                  { id: editReceipt.id, ...values },
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
            <DialogTitle>Delete receipts</DialogTitle>
          </DialogHeader>
          <p style={{ fontSize: 13, color: "var(--mute)" }}>
            Are you sure you want to delete {selected.size} receipt(s)? This
            action can be undone by restoring from the trash.
          </p>
          <div
            style={{
              display: "flex",
              justifyContent: "flex-end",
              gap: 8,
              paddingTop: 12,
            }}
          >
            <button
              type="button"
              className="btn"
              onClick={() => setDeleteOpen(false)}
            >
              Cancel
            </button>
            <button
              type="button"
              className="btn danger"
              disabled={deleteReceipts.isPending}
              onClick={() => {
                const ids = [...selected];
                setSelected(new Set());
                setDeleteOpen(false);
                deleteReceipts.mutate(ids);
              }}
            >
              {deleteReceipts.isPending && <Spinner size="sm" />}
              {deleteReceipts.isPending ? "Deleting…" : "Delete"}
            </button>
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}

export default Receipts;
