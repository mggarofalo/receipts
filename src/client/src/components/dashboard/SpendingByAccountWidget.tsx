import { useMemo } from "react";
import { ChartCard, BarChart } from "@/components/charts";
import { useDashboardSpendingByAccount } from "@/hooks/useDashboard";
import type { DateRange } from "@/hooks/useDashboard";

interface SpendingByAccountWidgetProps {
  dateRange: DateRange;
  className?: string;
}

function formatCurrency(value: number): string {
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
    minimumFractionDigits: 0,
    maximumFractionDigits: 0,
  }).format(value);
}

export function SpendingByAccountWidget({
  dateRange,
  className,
}: SpendingByAccountWidgetProps) {
  const { data, isLoading } = useDashboardSpendingByAccount(dateRange);

  const chartData = useMemo(
    () =>
      (data?.items ?? [])
        .map((item) => ({
          name: item.accountName,
          value: Number(item.amount ?? 0),
        }))
        .sort((a, b) => b.value - a.value),
    [data?.items],
  );

  const top = chartData[0];
  const summary =
    chartData.length === 0
      ? null
      : `Top account ${top.name} at ${formatCurrency(top.value)} across ${chartData.length} ${chartData.length === 1 ? "account" : "accounts"}.`;

  return (
    <ChartCard
      title="Spending by Account"
      loading={isLoading}
      empty={chartData.length === 0 && !isLoading}
      className={className}
    >
      {(titleId) => (
        <>
          {summary && (
            <p className="sr-only">{summary}</p>
          )}
          <BarChart
            data={chartData}
            layout="horizontal"
            formatValue={formatCurrency}
            aria-labelledby={titleId}
          />
        </>
      )}
    </ChartCard>
  );
}
