import { useCallback, useMemo } from "react";
import {
  useSpendingByLocationReport,
  type SpendingByLocationParams,
} from "@/hooks/useSpendingByLocationReport";
import { useCsvExport } from "@/hooks/useCsvExport";
import { useReportSearchParams } from "@/hooks/useReportSearchParams";
import client from "@/lib/api-client";
import { csvFilename } from "@/lib/export-csv";
import { fetchAllReportPages } from "@/lib/fetch-all-report-pages";
import { formatCurrency } from "@/lib/format";
import {
  parseDateRangeParam,
  parseEnumParam,
  parsePositiveIntParam,
  serializeDateRangeParam,
} from "@/lib/report-params";
import { ChartCard, BarChart } from "@/components/charts";
import { DateRangeSelector } from "@/components/dashboard/DateRangeSelector";
import type { DateRange } from "@/hooks/useDashboard";
import {
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { SortableTableHead } from "@/components/SortableTableHead";

type SortColumn = "location" | "visits" | "total" | "averagePerVisit";
type SortDirection = "asc" | "desc";

const SORT_COLUMNS = ["location", "visits", "total", "averagePerVisit"] as const;
const SORT_DIRECTIONS = ["asc", "desc"] as const;

interface SpendingByLocationUrlParams {
  dateRange: DateRange;
  sortBy: SortColumn;
  sortDirection: SortDirection;
  page: number;
}

function parseSpendingByLocationParams(
  searchParams: URLSearchParams,
): SpendingByLocationUrlParams {
  return {
    dateRange: parseDateRangeParam(searchParams),
    sortBy: parseEnumParam(searchParams.get("sortBy"), SORT_COLUMNS, "total"),
    sortDirection: parseEnumParam(
      searchParams.get("sortDirection"),
      SORT_DIRECTIONS,
      "desc",
    ),
    page: parsePositiveIntParam(searchParams.get("page"), 1),
  };
}

export default function SpendingByLocation() {
  const [urlParams, updateParams] = useReportSearchParams(
    parseSpendingByLocationParams,
  );
  const { dateRange, sortBy, sortDirection, page } = urlParams;
  const pageSize = 50;

  const params: SpendingByLocationParams = {
    startDate: dateRange.startDate,
    endDate: dateRange.endDate,
    sortBy,
    sortDirection,
    page,
    pageSize,
  };

  const { data, isLoading, isError } = useSpendingByLocationReport(params);
  const { exportCsv, isExporting } = useCsvExport();

  const handleDateRangeChange = useCallback(
    (range: DateRange) => {
      updateParams({ ...serializeDateRangeParam(range), page: 1 });
    },
    [updateParams],
  );

  function handleExport() {
    exportCsv({
      filename: csvFilename("spending-by-location", dateRange),
      headers: ["Location", "Visits", "Total", "Average Per Visit"],
      rows: async () => {
        const items = await fetchAllReportPages(
          async (exportPage, exportPageSize) => {
            const { data: pageData, error } = await client.GET(
              "/api/reports/spending-by-location",
              {
                params: {
                  query: {
                    startDate: dateRange.startDate,
                    endDate: dateRange.endDate,
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
        return items.map((item) => [
          item.location,
          item.visits,
          item.total,
          item.averagePerVisit,
        ]);
      },
    });
  }

  function handleSort(column: string) {
    const nextColumn = column as SortColumn;
    if (sortBy === nextColumn) {
      updateParams({
        sortDirection: sortDirection === "asc" ? "desc" : "asc",
        page: 1,
      });
    } else {
      updateParams({
        sortBy: nextColumn,
        sortDirection: nextColumn === "location" ? "asc" : "desc",
        page: 1,
      });
    }
  }

  const chartData = useMemo(
    () =>
      (data?.items ?? []).slice(0, 10).map((item) => ({
        name: item.location,
        value: Number(item.total ?? 0),
      })),
    [data?.items],
  );

  const totalPages = data ? Math.ceil(Number(data.totalCount ?? 0) / pageSize) : 0;

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
          Failed to load spending by location report.
        </p>
      </div>
    );
  }

  if (!data || data.totalCount === 0) {
    return (
      <div className="space-y-4">
        <div className="flex justify-end">
          <DateRangeSelector
            value={dateRange}
            onChange={handleDateRangeChange}
            initialPreset="12M"
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
            <p className="card-sub">Locations</p>
            <p className="money-med">{data.totalCount}</p>
          </div>
          <div>
            <p className="card-sub">Total Spending</p>
            <p className="money-med">
              {formatCurrency(Number(data.grandTotal ?? 0))}
            </p>
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
            initialPreset="12M"
          />
        </div>
      </div>

      <ChartCard
        title="Top Locations by Spending"
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
            <SortableTableHead column="location" label="Location" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
            <SortableTableHead column="visits" label="Visits" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} className="text-right" align="right" />
            <SortableTableHead column="total" label="Total" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} className="text-right" align="right" />
            <SortableTableHead column="averagePerVisit" label="Avg/Visit" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} className="text-right" align="right" />
          </TableRow>
        </TableHeader>
        <TableBody>
          {data.items.map((item) => (
            <TableRow key={item.location}>
              <TableCell>{item.location}</TableCell>
              <TableCell className="text-right money">{item.visits}</TableCell>
              <TableCell className="text-right money">
                {formatCurrency(Number(item.total ?? 0))}
              </TableCell>
              <TableCell className="text-right money">
                {formatCurrency(Number(item.averagePerVisit ?? 0))}
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
