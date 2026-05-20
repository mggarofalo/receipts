import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

interface StatCardProps {
  title: string;
  value: string;
  subtitle?: string;
  loading?: boolean;
  className?: string;
}

/**
 * KPI card that matches the design system's `.kpi` block — mono uppercase
 * label, a large serif value, a faint mono sub-line, and reserved space at
 * the bottom-right for a sparkline if a caller wants to render one.
 */
export function StatCard({
  title,
  value,
  subtitle,
  loading,
  className,
}: StatCardProps) {
  return (
    <div className={cn("kpi", className)}>
      <div className="label">{title}</div>
      <div className="val money-big num">
        {loading ? <Skeleton className="h-10 w-32 mt-1" /> : value}
      </div>
      {subtitle && !loading && <div className="sub">{subtitle}</div>}
    </div>
  );
}
