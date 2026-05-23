import { useState, useCallback, useMemo } from "react";
import { format, subMonths } from "date-fns";
import {
  useSpendingByLocationReport,
  type SpendingByLocationParams,
} from "@/hooks/useSpendingByLocationReport";
import { formatCurrency } from "@/lib/format";
import { ChartCard, BarChart } from "@/components/charts";
import { DateRangeSelector } from "@/components/dashboard/DateRangeSelector";
import type { DateRange } from "@/hooks/useDashboard";
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

type SortColumn = "location" | "visits" | "total" | "averagePerVisit";
type SortDirection = "asc" | "desc";

function getDefaultRange(): DateRange {
  const now = new Date();
  return {
    startDate: format(subMonths(now, 1), "yyyy-MM-dd"),
    endDate: format(now, "yyyy-MM-dd"),
  };
}

export default function SpendingByLocation() {
  const [dateRange, setDateRange] = useState<DateRange>(getDefaultRange);
  const [sortBy, setSortBy] = useState<SortColumn>("total");
  const [sortDirection, setSortDirection] = useState<SortDirection>("desc");
  const [page, setPage] = useState(1);
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

  const handleDateRangeChange = useCallback((range: DateRange) => {
    setDateRange(range);
    setPage(1);
  }, []);

  function handleSort(column: SortColumn) {
    if (sortBy === column) {
      setSortDirection((prev) => (prev === "asc" ? "desc" : "asc"));
    } else {
      setSortBy(column);
      setSortDirection(column === "location" ? "asc" : "desc");
    }
    setPage(1);
  }

  function sortIndicator(column: SortColumn) {
    if (sortBy !== column) return null;
    return sortDirection === "asc" ? " \u2191" : " \u2193";
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
        <DateRangeSelector
          value={dateRange}
          onChange={handleDateRangeChange}
        />
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
            <TableHead
              className="cursor-pointer select-none"
              onClick={() => handleSort("location")}
            >
              Location{sortIndicator("location")}
            </TableHead>
            <TableHead
              className="cursor-pointer select-none text-right"
              onClick={() => handleSort("visits")}
            >
              Visits{sortIndicator("visits")}
            </TableHead>
            <TableHead
              className="cursor-pointer select-none text-right"
              onClick={() => handleSort("total")}
            >
              Total{sortIndicator("total")}
            </TableHead>
            <TableHead
              className="cursor-pointer select-none text-right"
              onClick={() => handleSort("averagePerVisit")}
            >
              Avg/Visit{sortIndicator("averagePerVisit")}
            </TableHead>
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
    </div>
  );
}
