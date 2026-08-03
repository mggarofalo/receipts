import { useCallback, useMemo } from "react";
import { Link } from "react-router";
import {
  useSpendingByNormalizedDescription,
  type SpendingByNormalizedDescriptionParams,
} from "@/hooks/useSpendingByNormalizedDescription";
import { useCsvExport } from "@/hooks/useCsvExport";
import { useReportSearchParams } from "@/hooks/useReportSearchParams";
import client from "@/lib/api-client";
import { csvFilename } from "@/lib/export-csv";
import { fetchAllReportPages } from "@/lib/fetch-all-report-pages";
import { formatCurrency } from "@/lib/format";
import {
  buildItemCostDrillDownHref,
  parseDateRangeParam,
  parseEnumParam,
  parsePositiveIntParam,
  serializeDateRangeParam,
} from "@/lib/report-params";
import { DateRangeSelector } from "@/components/dashboard/DateRangeSelector";
import type { DateRange } from "@/hooks/useDashboard";
import { ChartCard, BarChart } from "@/components/charts";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { SortableTableHead } from "@/components/SortableTableHead";

type SortColumn = "canonicalName" | "totalAmount" | "itemCount";
type SortDirection = "asc" | "desc";

const SORT_COLUMNS = ["canonicalName", "totalAmount", "itemCount"] as const;
const SORT_DIRECTIONS = ["asc", "desc"] as const;

const PAGE_SIZE = 50;

interface SpendingByNormalizedDescriptionUrlParams {
  dateRange: DateRange;
  sortBy: SortColumn;
  sortDirection: SortDirection;
  page: number;
}

function parseSpendingByNormalizedDescriptionParams(
  searchParams: URLSearchParams,
): SpendingByNormalizedDescriptionUrlParams {
  return {
    dateRange: parseDateRangeParam(searchParams),
    sortBy: parseEnumParam(
      searchParams.get("sortBy"),
      SORT_COLUMNS,
      "totalAmount",
    ),
    sortDirection: parseEnumParam(
      searchParams.get("sortDirection"),
      SORT_DIRECTIONS,
      "desc",
    ),
    page: parsePositiveIntParam(searchParams.get("page"), 1),
  };
}

/**
 * Share of the grand total, as a display string. The denominator spans every
 * bucket (not just the current page), so the column reads consistently while
 * paging. A zero/absent grand total yields an em dash rather than NaN%.
 */
function formatShare(amount: number, grandTotal: number): string {
  if (!Number.isFinite(grandTotal) || grandTotal === 0) return "—";
  return `${((amount / grandTotal) * 100).toFixed(1)}%`;
}

export default function SpendingByNormalizedDescription() {
  const [urlParams, updateParams] = useReportSearchParams(
    parseSpendingByNormalizedDescriptionParams,
  );
  const { dateRange, sortBy, sortDirection, page } = urlParams;

  const params: SpendingByNormalizedDescriptionParams = {
    from: dateRange.startDate,
    to: dateRange.endDate,
    sortBy,
    sortDirection,
    page,
    pageSize: PAGE_SIZE,
  };

  const { data, isLoading, isError } =
    useSpendingByNormalizedDescription(params);
  const { exportCsv, isExporting } = useCsvExport();

  const handleDateRangeChange = useCallback(
    (range: DateRange) => {
      updateParams({ ...serializeDateRangeParam(range), page: 1 });
    },
    [updateParams],
  );

  const handleSort = useCallback(
    (column: string) => {
      const nextColumn = column as SortColumn;
      if (sortBy === nextColumn) {
        updateParams({
          sortDirection: sortDirection === "asc" ? "desc" : "asc",
          page: 1,
        });
      } else {
        updateParams({
          sortBy: nextColumn,
          sortDirection: nextColumn === "canonicalName" ? "asc" : "desc",
          page: 1,
        });
      }
    },
    [sortBy, sortDirection, updateParams],
  );

  const items = useMemo(() => data?.items ?? [], [data?.items]);
  const grandTotal = Number(data?.grandTotal ?? 0);
  const totalCount = Number(data?.totalCount ?? 0);
  const totalPages = Math.ceil(totalCount / PAGE_SIZE);

  const chartData = useMemo(
    () =>
      items.slice(0, 10).map((item) => ({
        name: item.canonicalName,
        value: Number(item.totalAmount ?? 0),
      })),
    [items],
  );

  function handleExport() {
    exportCsv({
      filename: csvFilename("spending-by-normalized-description", dateRange),
      headers: [
        "Canonical Name",
        "Item Count",
        "Total Amount",
        "Share of Total",
        "Currency",
      ],
      rows: async () => {
        const allItems = await fetchAllReportPages(
          async (exportPage, exportPageSize) => {
            const { data: pageData, error } = await client.GET(
              "/api/reports/spending-by-normalized-description",
              {
                params: {
                  query: {
                    from: dateRange.startDate,
                    to: dateRange.endDate,
                    sortBy,
                    sortDirection,
                    page: exportPage,
                    pageSize: exportPageSize,
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
        return allItems.map((item) => [
          item.canonicalName,
          item.itemCount,
          item.totalAmount,
          formatShare(Number(item.totalAmount ?? 0), grandTotal),
          item.currency,
        ]);
      },
    });
  }

  if (isLoading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-10 w-full rounded-lg" />
        <Skeleton className="h-20 w-full rounded-lg" />
        <Skeleton className="h-64 w-full rounded-lg" />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="rounded-lg border border-destructive p-6 text-center">
        <p className="text-destructive">
          Failed to load spending by normalized description report.
        </p>
      </div>
    );
  }

  if (!data || totalCount === 0) {
    return (
      <div className="space-y-4">
        <div className="flex justify-end">
          <DateRangeSelector
            value={dateRange}
            onChange={handleDateRangeChange}
          />
        </div>
        <div className="rounded-lg border p-6 text-center">
          <h2 className="card-title">No Data</h2>
          <p className="mt-2 text-muted-foreground">
            No spending data found for the selected date range.
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div className="flex gap-6">
          <div>
            <p className="card-sub">Descriptions</p>
            <p className="money-med">{totalCount}</p>
          </div>
          <div>
            <p className="card-sub">Total Spending</p>
            <p className="money-med">{formatCurrency(grandTotal)}</p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            disabled={isExporting}
            onClick={handleExport}
          >
            {isExporting ? "Exporting..." : "Export CSV"}
          </Button>
          <DateRangeSelector
            value={dateRange}
            onChange={handleDateRangeChange}
          />
        </div>
      </div>

      <ChartCard
        title="Top Normalized Descriptions by Spending"
        empty={chartData.length === 0}
      >
        <BarChart
          data={chartData}
          layout="horizontal"
          height={Math.max(200, chartData.length * 40)}
          formatValue={formatCurrency}
        />
      </ChartCard>

      <Table>
        <TableHeader>
          <TableRow>
            <SortableTableHead
              column="canonicalName"
              label="Canonical Name"
              currentSortBy={sortBy}
              currentSortDirection={sortDirection}
              onToggleSort={handleSort}
            />
            <SortableTableHead
              column="itemCount"
              label="Items"
              currentSortBy={sortBy}
              currentSortDirection={sortDirection}
              onToggleSort={handleSort}
              className="text-right"
              align="right"
            />
            <SortableTableHead
              column="totalAmount"
              label="Total"
              currentSortBy={sortBy}
              currentSortDirection={sortDirection}
              onToggleSort={handleSort}
              className="text-right"
              align="right"
            />
            {/* Not sortable: share is a monotone function of Total, so it
                would duplicate the Total header's sort while showing a second
                active-sort indicator. */}
            <TableHead className="text-right">Share</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {items.map((item) => {
            const href = buildItemCostDrillDownHref(
              item.canonicalName,
              dateRange,
            );
            return (
              <TableRow key={item.canonicalName}>
                <TableCell className="font-medium">
                  {href ? (
                    <Link
                      to={href}
                      className="underline underline-offset-4 hover:text-primary"
                      title={`View cost over time for ${item.canonicalName}`}
                    >
                      {item.canonicalName}
                    </Link>
                  ) : (
                    item.canonicalName
                  )}
                </TableCell>
                <TableCell className="text-right money">
                  {item.itemCount}
                </TableCell>
                <TableCell className="text-right money">
                  {formatCurrency(Number(item.totalAmount ?? 0))}
                </TableCell>
                <TableCell className="text-right money">
                  {formatShare(Number(item.totalAmount ?? 0), grandTotal)}
                </TableCell>
              </TableRow>
            );
          })}
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
              onClick={() => updateParams({ page: page - 1 })}
            >
              Previous
            </Button>
            <Button
              variant="outline"
              size="sm"
              disabled={page >= totalPages}
              onClick={() => updateParams({ page: page + 1 })}
            >
              Next
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
