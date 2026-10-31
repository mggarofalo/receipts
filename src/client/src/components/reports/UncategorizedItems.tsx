import { Pagination } from "@/components/Pagination";
import { usePersistedTablePageSize } from "@/hooks/usePersistedTablePageSize";
import { isPageSize } from "@/lib/page-size";
import { RequestFailure } from "@/components/RequestFailure";
import { useState } from "react";
import { useNavigate } from "react-router";
import { useCategorizeReceiptItems } from "@/hooks/useCategorizeReceiptItems";
import {
  useUncategorizedItemsReport,
  type UncategorizedItemsParams,
} from "@/hooks/useUncategorizedItemsReport";
import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import { CategoryPathPicker } from "@/components/categories/CategoryPathPicker";
import { ItemTypeCorrection } from "@/components/reports/ItemTypeCorrection";
import type { CategoryChoice } from "@/lib/category-paths";
import { useCsvExport } from "@/hooks/useCsvExport";
import { useReportSearchParams } from "@/hooks/useReportSearchParams";
import { csvFilename } from "@/lib/export-csv";
import { fetchAllReportPages } from "@/lib/fetch-all-report-pages";
import { formatCurrency } from "@/lib/format";
import { parseEnumParam, parsePositiveIntParam } from "@/lib/report-params";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Skeleton } from "@/components/ui/skeleton";
import { SortableTableHead } from "@/components/SortableTableHead";
import client from "@/lib/api-client";
import { toast } from "sonner";

type SortColumn = "description" | "total" | "itemCode";
type SortDirection = "asc" | "desc";
type SuggestionFilter = "any" | "with" | "without";

const SORT_COLUMNS = ["description", "total", "itemCode"] as const;
const SORT_DIRECTIONS = ["asc", "desc"] as const;
const SUGGESTION_FILTERS = ["any", "with", "without"] as const;
const SUGGESTION_FILTER_LABELS: Record<SuggestionFilter, string> = {
  any: "All",
  with: "Jev answered",
  without: "Not asked",
};

interface UncategorizedItemsUrlParams {
  sortBy: SortColumn;
  sortDirection: SortDirection;
  page: number;
  pageSize: number;
  suggestion: SuggestionFilter;
}

function parseUncategorizedItemsParams(
  searchParams: URLSearchParams,
): UncategorizedItemsUrlParams {
  return {
    sortBy: parseEnumParam(
      searchParams.get("sortBy"),
      SORT_COLUMNS,
      "description",
    ),
    sortDirection: parseEnumParam(
      searchParams.get("sortDirection"),
      SORT_DIRECTIONS,
      "asc",
    ),
    page: parsePositiveIntParam(searchParams.get("page"), 1),
    pageSize: parsePositiveIntParam(searchParams.get("pageSize"), 0),
    suggestion: parseEnumParam(
      searchParams.get("suggestion"),
      SUGGESTION_FILTERS,
      "any",
    ),
  };
}

export default function UncategorizedItems() {
  const navigate = useNavigate();
  const [urlParams, updateParams] = useReportSearchParams(
    parseUncategorizedItemsParams,
  );
  const { sortBy, sortDirection, page, suggestion } = urlParams;
  const { pageSize: preferredPageSize, setPageSize } =
    usePersistedTablePageSize("report-uncategorized", 50);
  const pageSize = isPageSize(urlParams.pageSize)
    ? urlParams.pageSize
    : preferredPageSize;
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [choice, setChoice] = useState<CategoryChoice>();

  const params: UncategorizedItemsParams = {
    sortBy,
    sortDirection,
    page,
    pageSize,
    suggestion,
  };

  const { data, isLoading, isError } = useUncategorizedItemsReport(params);
  const { exportCsv, isExporting } = useCsvExport();
  const taxonomy = useCategoryTaxonomy();

  function handleExport() {
    exportCsv({
      filename: csvFilename("uncategorized-items"),
      headers: [
        "Description",
        "Item Code",
        "Quantity",
        "Unit Price",
        "Total",
        "Classification state",
        "Currency",
        "Receipt ID",
      ],
      rows: async () => {
        const items = await fetchAllReportPages(
          async (exportPage, exportPageSize) => {
            const { data: pageData, error } = await client.GET(
              "/api/reports/uncategorized-items",
              {
                params: {
                  query: {
                    sortBy,
                    sortDirection,
                    page: exportPage,
                    pageSize: exportPageSize,
                    suggestion,
                  },
                },
              },
            );
            if (error) throw error;
            return {
              items: pageData?.items ?? [],
              totalCount: Number(pageData?.totalCount ?? 0),
            };
          },
        );
        return items.map((item) => [
          item.description,
          item.receiptItemCode,
          item.quantity,
          item.unitPrice,
          item.totalAmount,
          item.classificationState,
          item.totalAmountCurrency,
          item.receiptId,
        ]);
      },
    });
  }

  const bulkUpdateMutation = useCategorizeReceiptItems({
    onSuccess: () => {
      setSelectedIds(new Set());
      setChoice(undefined);
      toast.success("Items categorized successfully");
    },
  });
  const rowUpdateMutation = useCategorizeReceiptItems({
    onSuccess: () => toast.success("Item categorized"),
  });
  const rowBusy = rowUpdateMutation.isPending || bulkUpdateMutation.isPending;

  function handleSort(column: string) {
    const nextColumn = column as SortColumn;
    if (sortBy === nextColumn) {
      updateParams({
        sortDirection: sortDirection === "asc" ? "desc" : "asc",
        page: 1,
      });
    } else {
      updateParams({ sortBy: nextColumn, sortDirection: "asc", page: 1 });
    }
  }

  function handleReceiptClick(e: React.MouseEvent, receiptId: string) {
    e.stopPropagation();
    navigate(`/receipts/${receiptId}`);
  }

  function handleToggleItem(id: string) {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }

  function handleToggleAll() {
    if (!data?.items) return;
    setSelectedIds((prev) => {
      const allOnPage = data.items.map((item) => item.id);
      const allSelected = allOnPage.every((id) => prev.has(id));
      if (allSelected) {
        const next = new Set(prev);
        for (const id of allOnPage) next.delete(id);
        return next;
      } else {
        const next = new Set(prev);
        for (const id of allOnPage) next.add(id);
        return next;
      }
    });
  }

  function handleApply() {
    if (
      !data?.items ||
      selectedIds.size === 0 ||
      !choice ||
      !taxonomy.data ||
      choice.taxonomyRevisionId !== taxonomy.data.id ||
      choice.categoryId === null
    )
      return;

    const itemsToUpdate = data.items.filter((item) => selectedIds.has(item.id));

    bulkUpdateMutation.mutate({
      items: itemsToUpdate,
      choice,
    });
  }

  const totalPages = data
    ? Math.ceil(Number(data.totalCount ?? 0) / pageSize)
    : 0;
  const allOnPageSelected =
    data?.items &&
    data.items.length > 0 &&
    data.items.every((item) => selectedIds.has(item.id));

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
          Failed to load uncategorized items report.
        </p>
      </div>
    );
  }

  // Without category assistance there are no answers to filter by.
  const filterBar = data?.suggestionsAvailable && (
    <div
      role="group"
      aria-label="Jev suggestion filter"
      className="flex flex-wrap gap-1"
    >
      {SUGGESTION_FILTERS.map((value) => (
        <Button
          key={value}
          type="button"
          size="sm"
          variant={suggestion === value ? "secondary" : "ghost"}
          aria-pressed={suggestion === value}
          onClick={() => {
            setSelectedIds(new Set());
            updateParams({ suggestion: value, page: 1 });
          }}
        >
          {SUGGESTION_FILTER_LABELS[value]}
        </Button>
      ))}
    </div>
  );

  if (
    data &&
    data.totalCount === 0 &&
    data.suggestionsAvailable &&
    suggestion !== "any"
  ) {
    return (
      <div className="space-y-4">
        {filterBar}
        <div className="rounded-lg border p-6 text-center">
          <p className="text-muted-foreground">
            No uncategorized items match this filter.
          </p>
        </div>
      </div>
    );
  }

  if (!data || data.totalCount === 0) {
    return (
      <div className="rounded-lg border p-6 text-center">
        <h2 className="card-title">All Categorized</h2>
        <p className="mt-2 text-muted-foreground">
          All receipt items have been categorized. No uncategorized items found.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-6 rounded-lg border p-4">
        <div>
          <p className="card-sub">Uncategorized Items</p>
          <p className="money-med">{data.totalCount}</p>
        </div>
        <Button
          variant="outline"
          size="sm"
          className="ml-auto"
          disabled={isExporting}
          onClick={handleExport}
        >
          {isExporting ? "Exporting..." : "Export CSV"}
        </Button>
      </div>

      {filterBar}

      {taxonomy.isError && (
        <RequestFailure
          message="Category choices unavailable. Your selection is retained."
          retry={() => {
            void taxonomy.refetch();
          }}
          isRetrying={taxonomy.isFetching}
        />
      )}

      {data.items.some((item) => selectedIds.has(item.id)) && (
        <div className="flex flex-wrap items-center gap-3 rounded-lg border bg-muted/50 p-3">
          <span className="text-sm font-medium">
            {data.items.filter((item) => selectedIds.has(item.id)).length}{" "}
            selected on this page
          </span>
          <CategoryPathPicker
            aria-label="Category for selected items"
            revision={taxonomy.data}
            loading={taxonomy.isLoading}
            error={taxonomy.isError}
            value={choice}
            onValueChange={setChoice}
            className="w-full sm:w-48"
          />
          <Button
            size="sm"
            disabled={
              !choice?.categoryId ||
              choice.taxonomyRevisionId !== taxonomy.data?.id ||
              !data.items.some((item) => selectedIds.has(item.id)) ||
              bulkUpdateMutation.isPending
            }
            onClick={handleApply}
          >
            {bulkUpdateMutation.isPending ? "Applying..." : "Apply to Selected"}
          </Button>
        </div>
      )}

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead className="w-10">
              <Checkbox
                checked={!!allOnPageSelected}
                onCheckedChange={handleToggleAll}
                aria-label="Select all items on this page"
              />
            </TableHead>
            <SortableTableHead
              column="description"
              label="Description"
              currentSortBy={sortBy}
              currentSortDirection={sortDirection}
              onToggleSort={handleSort}
            />
            <SortableTableHead
              column="itemCode"
              label="Item Code"
              currentSortBy={sortBy}
              currentSortDirection={sortDirection}
              onToggleSort={handleSort}
            />
            <TableHead>Receipt</TableHead>
            <SortableTableHead
              column="total"
              label="Total"
              currentSortBy={sortBy}
              currentSortDirection={sortDirection}
              onToggleSort={handleSort}
              className="text-right"
              align="right"
            />
            <TableHead>Item type</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {data.items.map((item) => (
            <TableRow key={item.id}>
              <TableCell>
                <Checkbox
                  checked={selectedIds.has(item.id)}
                  onCheckedChange={() => handleToggleItem(item.id)}
                  aria-label={`Select ${item.description}`}
                />
              </TableCell>
              <TableCell>
                <div>{item.description}</div>
                <div className="text-xs text-muted-foreground">
                  {item.location}
                </div>
              </TableCell>
              <TableCell>{item.receiptItemCode ?? "-"}</TableCell>
              <TableCell>
                <button
                  type="button"
                  className="text-primary underline-offset-4 hover:underline"
                  onClick={(e) => handleReceiptClick(e, item.receiptId)}
                >
                  View
                </button>
              </TableCell>
              <TableCell className="text-right money">
                {formatCurrency(
                  Number(item.totalAmount ?? 0),
                  item.totalAmountCurrency,
                )}
              </TableCell>
              <TableCell>
                <ItemTypeCorrection
                  description={item.description}
                  suggestion={item.suggestion}
                  revision={taxonomy.data}
                  loading={taxonomy.isLoading}
                  error={taxonomy.isError}
                  busy={rowBusy}
                  onChoose={(next, fromSuggestion) =>
                    rowUpdateMutation.mutate({
                      items: [item],
                      choice: next,
                      reason: fromSuggestion
                        ? "Jev's suggestion accepted from unclassified items report"
                        : undefined,
                    })
                  }
                />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>

      <Pagination
        currentPage={page}
        totalItems={Number(data.totalCount ?? 0)}
        pageSize={pageSize}
        totalPages={totalPages}
        onPageChange={(nextPage) => updateParams({ page: nextPage })}
        onPageSizeChange={(size) => {
          setPageSize(size);
          updateParams({ page: 1, pageSize: size });
        }}
      />
    </div>
  );
}
