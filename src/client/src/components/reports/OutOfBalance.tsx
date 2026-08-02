import { useNavigate } from "react-router";
import {
  useOutOfBalanceReport,
  type OutOfBalanceParams,
} from "@/hooks/useOutOfBalanceReport";
import { useCsvExport } from "@/hooks/useCsvExport";
import { useReportSearchParams } from "@/hooks/useReportSearchParams";
import client from "@/lib/api-client";
import { csvFilename } from "@/lib/export-csv";
import { fetchAllReportPages } from "@/lib/fetch-all-report-pages";
import { formatCurrency } from "@/lib/format";
import { parseEnumParam, parsePositiveIntParam } from "@/lib/report-params";
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
import { SortableTableHead } from "@/components/SortableTableHead";

type SortColumn = "date" | "difference";
type SortDirection = "asc" | "desc";

const SORT_COLUMNS = ["date", "difference"] as const;
const SORT_DIRECTIONS = ["asc", "desc"] as const;

interface OutOfBalanceUrlParams {
  sortBy: SortColumn;
  sortDirection: SortDirection;
  page: number;
}

function parseOutOfBalanceParams(
  searchParams: URLSearchParams,
): OutOfBalanceUrlParams {
  return {
    sortBy: parseEnumParam(searchParams.get("sortBy"), SORT_COLUMNS, "date"),
    sortDirection: parseEnumParam(
      searchParams.get("sortDirection"),
      SORT_DIRECTIONS,
      "asc",
    ),
    page: parsePositiveIntParam(searchParams.get("page"), 1),
  };
}

export default function OutOfBalance() {
  const navigate = useNavigate();
  const [urlParams, updateParams] = useReportSearchParams(
    parseOutOfBalanceParams,
  );
  const { sortBy, sortDirection, page } = urlParams;
  const pageSize = 50;

  const params: OutOfBalanceParams = {
    sortBy,
    sortDirection,
    page,
    pageSize,
  };

  const { data, isLoading, isError } = useOutOfBalanceReport(params);
  const { exportCsv, isExporting } = useCsvExport();

  function handleExport() {
    exportCsv({
      filename: csvFilename("out-of-balance"),
      headers: [
        "Date",
        "Location",
        "Item Subtotal",
        "Tax",
        "Adjustments",
        "Expected Total",
        "Actual Total",
        "Difference",
        "Receipt ID",
      ],
      rows: async () => {
        const items = await fetchAllReportPages(
          async (exportPage, exportPageSize) => {
            const { data: pageData, error } = await client.GET(
              "/api/reports/out-of-balance",
              {
                params: {
                  query: {
                    sortBy,
                    sortDirection,
                    page: exportPage,
                    pageSize: exportPageSize,
                  },
                },
              },
            );
            if (error) throw error;
            return {
              items: pageData?.items ?? [],
              totalCount: Number(pageData?.totalCount ?? 0),
            };
          },
        );
        return items.map((item) => [
          item.date,
          item.location,
          item.itemSubtotal,
          item.taxAmount,
          item.adjustmentTotal,
          item.expectedTotal,
          item.transactionTotal,
          item.difference,
          item.receiptId,
        ]);
      },
    });
  }

  function handleSort(column: string) {
    const nextColumn = column as SortColumn;
    if (sortBy === nextColumn) {
      updateParams({
        sortDirection: sortDirection === "asc" ? "desc" : "asc",
        page: 1,
      });
    } else {
      updateParams({ sortBy: nextColumn, sortDirection: "asc", page: 1 });
    }
  }

  function handleRowClick(receiptId: string) {
    navigate(`/receipts/${receiptId}`);
  }

  function handleViewClick(e: React.MouseEvent, receiptId: string) {
    e.stopPropagation();
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
      <div className="flex items-center gap-6 rounded-lg border p-4">
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
        <Button
          variant="outline"
          size="sm"
          className="ml-auto"
          disabled={isExporting}
          onClick={handleExport}
        >
          {isExporting ? "Exporting..." : "Export CSV"}
        </Button>
      </div>

      <Table>
        <TableHeader>
          <TableRow>
            <SortableTableHead column="date" label="Date" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
            <TableHead>Location</TableHead>
            <TableHead className="text-right">Item Total</TableHead>
            <TableHead className="text-right">Tax</TableHead>
            <TableHead className="text-right">Adjustments</TableHead>
            <TableHead className="text-right">Expected Total</TableHead>
            <TableHead className="text-right">Actual Total</TableHead>
            <SortableTableHead column="difference" label="Difference" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} className="text-right" align="right" />
            <TableHead className="w-16">View</TableHead>
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
              <TableCell>
                <button
                  type="button"
                  className="text-primary underline-offset-4 hover:underline"
                  onClick={(e) => handleViewClick(e, item.receiptId)}
                >
                  View
                </button>
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
              onClick={() => updateParams({ page: page - 1 })}
            >
              Previous
            </Button>
            <Button
              variant="outline"
              size="sm"
              disabled={page >= totalPages}
              onClick={() => updateParams({ page: page + 1 })}
            >
              Next
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
