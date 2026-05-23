import { useState } from "react";
import { useNavigate } from "react-router";
import {
  useOutOfBalanceReport,
  type OutOfBalanceParams,
} from "@/hooks/useOutOfBalanceReport";
import { formatCurrency } from "@/lib/format";
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

type SortColumn = "date" | "difference";
type SortDirection = "asc" | "desc";

export default function OutOfBalance() {
  const navigate = useNavigate();
  const [sortBy, setSortBy] = useState<SortColumn>("date");
  const [sortDirection, setSortDirection] = useState<SortDirection>("asc");
  const [page, setPage] = useState(1);
  const pageSize = 50;

  const params: OutOfBalanceParams = {
    sortBy,
    sortDirection,
    page,
    pageSize,
  };

  const { data, isLoading, isError } = useOutOfBalanceReport(params);

  function handleSort(column: SortColumn) {
    if (sortBy === column) {
      setSortDirection((prev) => (prev === "asc" ? "desc" : "asc"));
    } else {
      setSortBy(column);
      setSortDirection("asc");
    }
    setPage(1);
  }

  function sortIndicator(column: SortColumn) {
    if (sortBy !== column) return null;
    return sortDirection === "asc" ? " \u2191" : " \u2193";
  }

  function handleRowClick(receiptId: string) {
    navigate(`/receipts/${receiptId}`);
  }

  const totalPages = data ? Math.ceil(Number(data.totalCount ?? 0) / pageSize) : 0;

  if (isLoading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-20 w-full rounded-lg" />
        <Skeleton className="h-64 w-full rounded-lg" />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="rounded-lg border border-destructive p-6 text-center">
        <p className="text-destructive">
          Failed to load out-of-balance report.
        </p>
      </div>
    );
  }

  if (!data || data.totalCount === 0) {
    return (
      <div className="rounded-lg border p-6 text-center">
        <h2 className="card-title">All Balanced</h2>
        <p className="mt-2 text-muted-foreground">
          All receipts are balanced. No discrepancies found.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex gap-6 rounded-lg border p-4">
        <div>
          <p className="card-sub">
            Out-of-Balance Receipts
          </p>
          <p className="money-med">{data.totalCount}</p>
        </div>
        <div>
          <p className="card-sub">Total Discrepancy</p>
          <p className="money-med">
            {formatCurrency(Number(data.totalDiscrepancy ?? 0))}
          </p>
        </div>
      </div>

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead
              className="cursor-pointer select-none"
              onClick={() => handleSort("date")}
            >
              Date{sortIndicator("date")}
            </TableHead>
            <TableHead>Location</TableHead>
            <TableHead className="text-right">Item Total</TableHead>
            <TableHead className="text-right">Tax</TableHead>
            <TableHead className="text-right">Adjustments</TableHead>
            <TableHead className="text-right">Expected Total</TableHead>
            <TableHead className="text-right">Actual Total</TableHead>
            <TableHead
              className="cursor-pointer select-none text-right"
              onClick={() => handleSort("difference")}
            >
              Difference{sortIndicator("difference")}
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {data.items.map((item) => (
            <TableRow
              key={item.receiptId}
              className="cursor-pointer"
              onClick={() => handleRowClick(item.receiptId)}
            >
              <TableCell>{item.date}</TableCell>
              <TableCell>{item.location}</TableCell>
              <TableCell className="text-right money">
                {formatCurrency(Number(item.itemSubtotal ?? 0))}
              </TableCell>
              <TableCell className="text-right money">
                {formatCurrency(Number(item.taxAmount ?? 0))}
              </TableCell>
              <TableCell className="text-right money">
                {formatCurrency(Number(item.adjustmentTotal ?? 0))}
              </TableCell>
              <TableCell className="text-right money">
                {formatCurrency(Number(item.expectedTotal ?? 0))}
              </TableCell>
              <TableCell className="text-right money">
                {formatCurrency(Number(item.transactionTotal ?? 0))}
              </TableCell>
              <TableCell
                className="text-right font-medium money"
                style={{
                  color:
                    Number(item.difference ?? 0) < 0
                      ? "var(--neg-ink)"
                      : "var(--warn-ink)",
                }}
              >
                {formatCurrency(Number(item.difference ?? 0))}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>

      {totalPages > 1 && (
        <div className="flex items-center justify-between">
          <p className="text-sm text-muted-foreground">
            Page {page} of {totalPages}
          </p>
          <div className="flex gap-2">
            <Button
              variant="outline"
              size="sm"
              disabled={page <= 1}
              onClick={() => setPage((p) => p - 1)}
            >
              Previous
            </Button>
            <Button
              variant="outline"
              size="sm"
              disabled={page >= totalPages}
              onClick={() => setPage((p) => p + 1)}
            >
              Next
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
