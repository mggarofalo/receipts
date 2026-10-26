import { useId } from "react";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  CardAction,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import type { ReactNode } from "react";

interface ChartCardProps {
  title: string;
  subtitle?: string;
  loading?: boolean;
  empty?: boolean;
  emptyMessage?: string;
  action?: ReactNode;
  children: ReactNode | ((titleId: string) => ReactNode);
  className?: string;
}

export function ChartCard({
  title,
  subtitle,
  loading,
  empty,
  emptyMessage = "No data available",
  action,
  children,
  className,
}: ChartCardProps) {
  const titleId = useId();

  return (
    <Card className={cn("flex flex-col", className)}>
      <CardHeader className="flex flex-col gap-3 sm:flex-row sm:flex-wrap sm:justify-between">
        <div>
          <CardTitle id={titleId}>{title}</CardTitle>
          {subtitle && <CardDescription>{subtitle}</CardDescription>}
        </div>
        {action && (
          <CardAction className="w-full min-w-0 sm:w-auto">{action}</CardAction>
        )}
      </CardHeader>
      <CardContent className="flex-1">
        {loading ? (
          <div
            role="status"
            aria-live="polite"
            aria-busy="true"
            className="space-y-3"
          >
            <span className="sr-only">Loading…</span>
            <Skeleton aria-hidden="true" className="h-4 w-full" />
            <Skeleton aria-hidden="true" className="h-4 w-3/4" />
            <Skeleton aria-hidden="true" className="h-32 w-full" />
          </div>
        ) : empty ? (
          <div className="flex items-center justify-center h-32 text-muted-foreground text-sm">
            {emptyMessage}
          </div>
        ) : typeof children === "function" ? (
          children(titleId)
        ) : (
          children
        )}
      </CardContent>
    </Card>
  );
}
