import { useMemo } from "react";
import { ChartCard, BarChart } from "@/components/charts";
import { useDashboardSpendingByStore } from "@/hooks/useDashboard";
import type { DateRange } from "@/hooks/useDashboard";

interface SpendingByStoreWidgetProps {
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

export function SpendingByStoreWidget({
  dateRange,
  className,
}: SpendingByStoreWidgetProps) {
  const { data, isLoading } = useDashboardSpendingByStore(dateRange);

  const items = useMemo(() => data?.items ?? [], [data?.items]);

  const chartData = useMemo(
    () =>
      items
        .slice(0, 10)
        .map((item) => ({
          name: item.location,
          value: Number(item.totalAmount ?? 0),
        })),
    [items],
  );

  return (
    <ChartCard
      title="Spending by Store"
      loading={isLoading}
      empty={items.length === 0 && !isLoading}
      emptyMessage="No store data"
      className={className}
    >
      {(titleId) => (
      <div className="space-y-4">
        {/* The table below is a full text alternative; no sr-only
            summary needed. aria-labelledby ties the chart to the card
            title so AT users still get a label. */}
        <BarChart
          data={chartData}
          layout="horizontal"
          formatValue={formatCurrency}
          aria-labelledby={titleId}
        />
        {items.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-muted-foreground">
                  <th className="pb-2 pr-4 font-medium">Location</th>
                  <th className="pb-2 pr-4 font-medium text-right">Visits</th>
                  <th className="pb-2 pr-4 font-medium text-right">Total</th>
                  <th className="pb-2 font-medium text-right">Avg/Visit</th>
                </tr>
              </thead>
              <tbody>
                {items.map((item) => (
                  <tr
                    key={item.location}
                    className="border-b border-border/50 last:border-0"
                  >
                    <td className="py-2 pr-4 truncate max-w-[200px]">
                      {item.location}
                    </td>
                    <td className="py-2 pr-4 text-right tabular-nums">
                      {item.visitCount}
                    </td>
                    <td className="py-2 pr-4 text-right tabular-nums">
                      {formatCurrency(Number(item.totalAmount ?? 0))}
                    </td>
                    <td className="py-2 text-right tabular-nums">
                      {formatCurrency(Number(item.averagePerVisit ?? 0))}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      )}
    </ChartCard>
  );
}
