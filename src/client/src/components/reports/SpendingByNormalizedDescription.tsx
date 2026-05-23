import { useCallback, useMemo, useState } from "react";
import { format, subMonths } from "date-fns";
import { useSpendingByNormalizedDescription } from "@/hooks/useSpendingByNormalizedDescription";
import { formatCurrency } from "@/lib/format";
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
import { Skeleton } from "@/components/ui/skeleton";

function getDefaultRange(): DateRange {
  const now = new Date();
  return {
    startDate: format(subMonths(now, 1), "yyyy-MM-dd"),
    endDate: format(now, "yyyy-MM-dd"),
  };
}

export default function SpendingByNormalizedDescription() {
  const [dateRange, setDateRange] = useState<DateRange>(getDefaultRange);

  const handleDateRangeChange = useCallback((range: DateRange) => {
    setDateRange(range);
  }, []);

  const { data, isLoading, isError } = useSpendingByNormalizedDescription({
    from: dateRange.startDate,
    to: dateRange.endDate,
  });

  const sorted = useMemo(() => {
    const items = data?.items ?? [];
    return [...items].sort((a, b) => (b.totalAmount ?? 0) - (a.totalAmount ?? 0));
  }, [data?.items]);

  const grandTotal = useMemo(
    () => sorted.reduce((sum, item) => sum + (item.totalAmount ?? 0), 0),
    [sorted],
  );

  const chartData = useMemo(
    () =>
      sorted.slice(0, 10).map((item) => ({
        name: item.canonicalName,
        value: Number(item.totalAmount ?? 0),
      })),
    [sorted],
  );

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

  if (!data || sorted.length === 0) {
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
            <p className="money-med">{sorted.length}</p>
          </div>
          <div>
            <p className="card-sub">Total Spending</p>
            <p className="money-med">{formatCurrency(grandTotal)}</p>
          </div>
        </div>
        <DateRangeSelector
          value={dateRange}
          onChange={handleDateRangeChange}
        />
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
            <TableHead>Canonical Name</TableHead>
            <TableHead className="text-right">Items</TableHead>
            <TableHead className="text-right">Total</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {sorted.map((item) => (
            <TableRow key={item.canonicalName}>
              <TableCell className="font-medium">
                {item.canonicalName}
              </TableCell>
              <TableCell className="text-right money">{item.itemCount}</TableCell>
              <TableCell className="text-right money">
                {formatCurrency(Number(item.totalAmount ?? 0))}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
