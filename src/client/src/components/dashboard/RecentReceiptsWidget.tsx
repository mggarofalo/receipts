import { useMemo } from "react";
import { Link } from "react-router";
import { ChartCard } from "@/components/charts";
import { useReceipts } from "@/hooks/useReceipts";
import { formatDate } from "@/lib/format";

interface RecentReceiptsWidgetProps {
  className?: string;
}

function formatCurrency(value: number | string | undefined): string {
  const num = Number(value ?? 0);
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
  }).format(num);
}

export function RecentReceiptsWidget({ className }: RecentReceiptsWidgetProps) {
  const { data, isLoading } = useReceipts(0, 5, "date", "desc");

  const receipts = useMemo(() => data ?? [], [data]);

  return (
    <ChartCard
      title="Recent Receipts"
      loading={isLoading}
      empty={receipts.length === 0 && !isLoading}
      emptyMessage="No receipts yet"
      className={className}
      action={
        <Link
          to="/receipts"
          className="text-sm text-primary hover:underline"
        >
          View all
        </Link>
      }
    >
      <ul className="space-y-3">
        {receipts.map((receipt) => (
          <li key={receipt.id}>
            <Link
              to={`/receipts/${receipt.id}`}
              className="flex items-center justify-between rounded-md px-2 py-1.5 transition-colors hover:bg-accent"
            >
              <div className="min-w-0 flex-1">
                <p className="text-sm font-medium truncate">
                  {receipt.location}
                </p>
                <p className="text-xs text-muted-foreground">
                  {formatDate(receipt.date)}
                </p>
              </div>
              <span className="ml-4 text-xs text-muted-foreground tabular-nums">
                Tax: {formatCurrency(receipt.taxAmount)}
              </span>
            </Link>
          </li>
        ))}
      </ul>
    </ChartCard>
  );
}
