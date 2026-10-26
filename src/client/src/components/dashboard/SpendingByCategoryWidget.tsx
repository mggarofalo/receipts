import { useMemo } from "react";
import { ChartCard, DonutChart } from "@/components/charts";
import { useDashboardSpendingByCategory } from "@/hooks/useDashboard";
import type { DateRange, CategoryReportOptions } from "@/hooks/useDashboard";
import type { components } from "@/generated/api";

type Item = components["schemas"]["SpendingCategoryItem"];
const MAX_SLICES = 5;
const stateLabels: Record<string, string> = {
  unclassified: "Unclassified",
  missingAssignment: "Missing assignment",
  unresolvedLegacy: "Unresolved legacy category",
  unresolvedPath: "Unresolved path",
  beforeMigrationHistory: "Before recorded history",
};

function bucketId(item: Item) {
  return JSON.stringify([
    item.currency,
    item.state,
    item.pathRevisionId,
    item.categoryId,
    item.path.map((node) => node.categoryId),
  ]);
}
function pathLabel(item: Item) {
  return item.path.length
    ? item.path
        .map((node) => `${node.name}${node.archived ? " (archived)" : ""}`)
        .join(" / ")
    : (stateLabels[item.state] ?? item.categoryName);
}

export function SpendingByCategoryWidget({
  dateRange,
  className,
  options = {},
}: {
  dateRange: DateRange;
  className?: string;
  options?: CategoryReportOptions;
}) {
  const { data, isLoading, isError, refetch } = useDashboardSpendingByCategory(
    dateRange,
    100,
    options,
  );
  const groups = useMemo(() => {
    const currencies = new Map<string, Item[]>();
    for (const item of data?.items ?? []) {
      const items = currencies.get(item.currency) ?? [];
      items.push(item);
      currencies.set(item.currency, items);
    }
    return [...currencies].map(([currency, items]) => {
      const labels = items.map(pathLabel);
      const slices = items.map((item, index) => ({
        id: bucketId(item),
        name:
          labels.filter((label) => label === labels[index]).length > 1
            ? `${labels[index]} (${item.pathRevisionId ?? item.categoryId})`
            : labels[index],
        value: item.amount,
        percentage: item.percentage,
      }));
      const chartData =
        slices.length <= MAX_SLICES
          ? slices
          : [
              ...slices.slice(0, MAX_SLICES - 1),
              {
                id: `other:${currency}`,
                name: "Other",
                value: slices
                  .slice(MAX_SLICES - 1)
                  .reduce((sum, item) => sum + item.value, 0),
                percentage: slices
                  .slice(MAX_SLICES - 1)
                  .reduce((sum, item) => sum + item.percentage, 0),
              },
            ];
      const total = items.reduce((sum, item) => sum + item.amount, 0);
      const percentage = items.reduce((sum, item) => sum + item.percentage, 0);
      const formatMoney = (value: number) =>
        new Intl.NumberFormat("en-US", { style: "currency", currency }).format(
          value,
        );
      const formatValue = (value: number) =>
        `${formatMoney(value)} (${total ? ((value / total) * percentage).toFixed(1) : "0.0"}% of ${currency})`;
      return { currency, items, chartData, total, formatMoney, formatValue };
    });
  }, [data?.items]);
  const partial = data?.items.some(
    (item) => !["classified", "unclassified"].includes(item.state),
  );
  return (
    <ChartCard
      title="Spending by Category"
      loading={isLoading}
      empty={!isError && !groups.length && !isLoading}
      className={className}
    >
      {(titleId) =>
        isError ? (
          <div role="alert">
            Category spending could not be loaded.{" "}
            <button className="btn xs ghost" onClick={() => void refetch()}>
              Retry
            </button>
          </div>
        ) : (
          <>
            {partial && (
              <p role="status" className="text-sm text-muted-foreground">
                Partial category history · unresolved amounts included
              </p>
            )}
            {groups.map((group) => (
              <section
                key={group.currency}
                aria-label={`${group.currency} category spending`}
              >
                <p className="text-sm font-medium">
                  {group.currency} · {group.formatMoney(group.total)} shown
                </p>
                <DonutChart
                  data={group.chartData}
                  centerLabel={group.formatMoney(group.total)}
                  formatValue={group.formatValue}
                  aria-labelledby={titleId}
                />
                <ul className="space-y-2 text-sm">
                  {group.items.map((item) => (
                    <li
                      key={bucketId(item)}
                      className="flex justify-between gap-3"
                      style={{ overflowWrap: "anywhere" }}
                    >
                      <span>{pathLabel(item)}</span>
                      <span
                        className="shrink-0"
                        title={group.formatValue(item.amount)}
                      >
                        {group.formatMoney(item.amount)} ·{" "}
                        {item.percentage.toFixed(1)}%
                      </span>
                    </li>
                  ))}
                </ul>
              </section>
            ))}
          </>
        )
      }
    </ChartCard>
  );
}
