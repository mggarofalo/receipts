import { useMemo } from "react";
import { ChartCard, DonutChart } from "@/components/charts";
import { useDashboardSpendingByCategory } from "@/hooks/useDashboard";
import type { DateRange } from "@/hooks/useDashboard";

const MAX_SLICES = 5;

interface SpendingByCategoryWidgetProps {
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

export function SpendingByCategoryWidget({
  dateRange,
  className,
}: SpendingByCategoryWidgetProps) {
  const { data, isLoading } = useDashboardSpendingByCategory(dateRange);

  const chartData = useMemo(() => {
    const items = data?.items ?? [];
    if (items.length <= MAX_SLICES) {
      return items.map((item) => ({
        name: item.categoryName,
        value: Number(item.amount ?? 0),
      }));
    }
    const top = items.slice(0, MAX_SLICES - 1);
    const rest = items.slice(MAX_SLICES - 1);
    const otherTotal = rest.reduce(
      (sum, item) => sum + Number(item.amount ?? 0),
      0,
    );
    return [
      ...top.map((item) => ({
        name: item.categoryName,
        value: Number(item.amount ?? 0),
      })),
      { name: "Other", value: otherTotal },
    ];
  }, [data?.items]);

  const totalSpent = useMemo(
    () =>
      chartData.reduce((sum, item) => sum + item.value, 0),
    [chartData],
  );

  const top = chartData[0];
  const summary =
    chartData.length === 0
      ? null
      : `Total spending ${formatCurrency(totalSpent)} across ${chartData.length} ${chartData.length === 1 ? "category" : "categories"}. Top: ${top.name} at ${formatCurrency(top.value)}.`;

  return (
    <ChartCard
      title="Spending by Category"
      loading={isLoading}
      empty={chartData.length === 0 && !isLoading}
      className={className}
    >
      {(titleId) => (
        <>
          {summary && <p className="sr-only">{summary}</p>}
          <DonutChart
            data={chartData}
            centerLabel={formatCurrency(totalSpent)}
            formatValue={formatCurrency}
            aria-labelledby={titleId}
          />
        </>
      )}
    </ChartCard>
  );
}
