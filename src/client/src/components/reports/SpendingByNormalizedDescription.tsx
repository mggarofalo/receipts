import { useCallback, useMemo } from "react";
import { useSpendingByNormalizedDescription } from "@/hooks/useSpendingByNormalizedDescription";
import { useCsvExport } from "@/hooks/useCsvExport";
import { useReportSearchParams } from "@/hooks/useReportSearchParams";
import { csvFilename } from "@/lib/export-csv";
import { formatCurrency } from "@/lib/format";
import { parseDateRangeParam, serializeDateRangeParam } from "@/lib/report-params";
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

interface SpendingByNormalizedDescriptionUrlParams {
  dateRange: DateRange;
}

function parseSpendingByNormalizedDescriptionParams(
  searchParams: URLSearchParams,
): SpendingByNormalizedDescriptionUrlParams {
  return { dateRange: parseDateRangeParam(searchParams) };
}

export default function SpendingByNormalizedDescription() {
  const [urlParams, updateParams] = useReportSearchParams(
    parseSpendingByNormalizedDescriptionParams,
  );
  const { dateRange } = urlParams;

  const handleDateRangeChange = useCallback(
    (range: DateRange) => {
      updateParams(serializeDateRangeParam(range));
    },
    [updateParams],
  );

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

  const { exportCsv, isExporting } = useCsvExport();

  function handleExport() {
    exportCsv({
      filename: csvFilename("spending-by-normalized-description", dateRange),
      headers: ["Canonical Name", "Item Count", "Total Amount", "Currency"],
      rows: sorted.map((item) => [
        item.canonicalName,
        item.itemCount,
        item.totalAmount,
        item.currency,
      ]),
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
