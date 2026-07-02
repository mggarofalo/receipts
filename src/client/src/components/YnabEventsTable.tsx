import { Link } from "react-router";
import { format } from "date-fns";
import type { YnabSyncEventResponse } from "@/hooks/useYnabEvents";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { SortableTableHead } from "@/components/SortableTableHead";

interface YnabEventsTableProps {
  events: YnabSyncEventResponse[];
  isLoading: boolean;
  sortBy?: string | null;
  sortDirection?: "asc" | "desc";
  onToggleSort?: (column: string) => void;
}

function formatTimestamp(iso: string): string {
  return format(new Date(iso), "MMM d, HH:mm:ss");
}

export function YnabEventsTable({
  events,
  isLoading,
  sortBy = null,
  sortDirection = "desc",
  onToggleSort,
}: YnabEventsTableProps) {
  if (isLoading) {
    return (
      <div className="space-y-2">
        {Array.from({ length: 5 }).map((_, i) => (
          <Skeleton key={i} className="h-12 w-full" />
        ))}
      </div>
    );
  }

  if (events.length === 0) {
    return (
      <div className="py-12 text-center text-muted-foreground">
        No YNAB activity yet.
      </div>
    );
  }

  return (
    <div className="rounded-md border">
      <Table>
        <TableHeader>
          <TableRow>
            {onToggleSort ? (
              <>
                <SortableTableHead
                  column="occurredAt"
                  label="Time"
                  currentSortBy={sortBy}
                  currentSortDirection={sortDirection}
                  onToggleSort={onToggleSort}
                />
                <SortableTableHead
                  column="eventType"
                  label="Type"
                  currentSortBy={sortBy}
                  currentSortDirection={sortDirection}
                  onToggleSort={onToggleSort}
                />
                <SortableTableHead
                  column="success"
                  label="Outcome"
                  currentSortBy={sortBy}
                  currentSortDirection={sortDirection}
                  onToggleSort={onToggleSort}
                />
              </>
            ) : (
              <>
                <TableHead>Time</TableHead>
                <TableHead>Type</TableHead>
                <TableHead>Outcome</TableHead>
              </>
            )}
            <TableHead>HTTP</TableHead>
            <TableHead>Receipt</TableHead>
            <TableHead>Detail</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {events.map((e) => (
            <TableRow key={e.id}>
              <TableCell className="text-xs whitespace-nowrap">
                {formatTimestamp(e.occurredAt)}
              </TableCell>
              <TableCell>{e.eventType}</TableCell>
              <TableCell>
                {e.success ? (
                  <Badge className="bg-green-100 text-green-800 hover:bg-green-100 border-green-300">
                    Success
                  </Badge>
                ) : (
                  <Badge variant="destructive">Failure</Badge>
                )}
              </TableCell>
              <TableCell className="text-xs">{e.httpStatus ?? "—"}</TableCell>
              <TableCell className="text-xs">
                {e.receiptId ? (
                  <Link
                    to={`/receipts/${e.receiptId}`}
                    className="text-primary hover:underline font-mono"
                  >
                    {e.receiptId.slice(0, 8)}
                  </Link>
                ) : (
                  <span className="text-muted-foreground">—</span>
                )}
              </TableCell>
              <TableCell
                className="text-xs text-muted-foreground max-w-md truncate"
                title={e.errorMessage ?? undefined}
              >
                {e.errorMessage ?? "—"}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
