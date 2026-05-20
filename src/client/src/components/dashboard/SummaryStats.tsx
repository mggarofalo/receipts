import { useDashboardSummary } from "@/hooks/useDashboard";
import type { DateRange } from "@/hooks/useDashboard";
import { StatCard } from "./StatCard";

interface SummaryStatsProps {
  dateRange: DateRange;
  className?: string;
}

function formatCurrency(value: number | string | undefined): string {
  const num = Number(value ?? 0);
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
  }).format(num);
}

export function SummaryStats({ dateRange, className }: SummaryStatsProps) {
  const { data, isLoading } = useDashboardSummary(dateRange);

  return (
    <div className={`kpis ${className ?? ""}`}>
      <StatCard
        title="Total receipts"
        value={String(Number(data?.totalReceipts ?? 0))}
        loading={isLoading}
      />
      <StatCard
        title="Total spent"
        value={formatCurrency(data?.totalSpent)}
        loading={isLoading}
      />
      <StatCard
        title="Avg trip"
        value={formatCurrency(data?.averageTripAmount)}
        loading={isLoading}
      />
      <StatCard
        title="Top category"
        value={data?.mostUsedCategory?.name ?? "—"}
        subtitle={
          data?.mostUsedCategory?.count
            ? `${Number(data.mostUsedCategory.count)} receipts`
            : undefined
        }
        loading={isLoading}
      />
    </div>
  );
}
