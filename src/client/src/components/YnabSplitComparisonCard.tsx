import { useMemo } from "react";
import { Link } from "react-router";
import {
  useYnabConnectionStatus,
  useYnabSplitComparison,
  type SplitLineDto,
  type TransactionSplitComparisonDto,
} from "@/hooks/useYnab";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Badge } from "@/components/ui/badge";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Skeleton } from "@/components/ui/skeleton";

interface YnabSplitComparisonCardProps {
  receiptId: string;
}

/**
 * Formats a YNAB milliunit value as a dollar string (e.g. -11000 → "-$11.00").
 * YNAB amounts are stored in signed milliunits — negative for outflows.
 */
function formatMilliunits(milliunits: number): string {
  const dollars = milliunits / 1000;
  const sign = dollars < 0 ? "-" : "";
  return `${sign}$${Math.abs(dollars).toFixed(2)}`;
}

export function YnabSplitComparisonCard({
  receiptId,
}: YnabSplitComparisonCardProps) {
  // The split-comparison endpoint is YNAB-gated (503 when YNAB is not set up).
  // Only query — and only render the card — once we know YNAB is configured;
  // otherwise the card would surface a spurious 503 on every receipt detail.
  const { isConfigured, isLoading: connectionLoading } =
    useYnabConnectionStatus();
  const { data, isLoading, error } = useYnabSplitComparison(
    receiptId,
    isConfigured,
  );

  // Derived values — memoized so downstream render stays stable regardless of
  // which branch we render. Safe because we only derive from `data`.
  const hasAnyActual = useMemo(
    () =>
      data?.transactionComparisons.some((tc) => tc.actual != null) ?? false,
    [data],
  );

  // A Synced transaction whose YNAB fetch failed leaves actual=null but sets
  // actualFetchError. We must not fall through to NotYetPushedState in that
  // case — the user needs to see the fetch error, not a "push me" prompt.
  const hasAnyFetchError = useMemo(
    () =>
      data?.transactionComparisons.some((tc) => tc.actualFetchError != null) ??
      false,
    [data],
  );

  const hasAnyMismatch = useMemo(
    () =>
      data?.transactionComparisons.some((tc) => tc.matches === false) ?? false,
    [data],
  );

  // No YNAB integration → the comparison is meaningless. Render nothing rather
  // than a 503 error card. (All hooks above run unconditionally to keep order
  // stable.)
  if (connectionLoading || !isConfigured) return null;

  return (
    <Card>
      <CardHeader>
        <CardTitle>YNAB Split Comparison</CardTitle>
        <CardDescription>
          Expected category split (computed locally) vs. the actual state
          currently stored in YNAB.
        </CardDescription>
      </CardHeader>
      <CardContent>
        {isLoading ? (
          <div className="space-y-2">
            <Skeleton className="h-4 w-full" />
            <Skeleton className="h-4 w-3/4" />
            <Skeleton className="h-4 w-5/6" />
          </div>
        ) : error ? (
          <Alert variant="destructive">
            <AlertDescription>
              Could not load split comparison:{" "}
              {error instanceof Error ? error.message : String(error)}
            </AlertDescription>
          </Alert>
        ) : !data ? null : !data.canComputeExpected ? (
          <UnavailableState
            reason={data.expectedUnavailableReason}
            unmapped={data.unmappedCategories}
          />
        ) : data.transactionComparisons.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No transactions to compare.
          </p>
        ) : !hasAnyActual && !hasAnyFetchError ? (
          <NotYetPushedState comparisons={data.transactionComparisons} />
        ) : (
          <PushedState
            comparisons={data.transactionComparisons}
            hasAnyMismatch={hasAnyMismatch}
          />
        )}
      </CardContent>
    </Card>
  );
}

function UnavailableState({
  reason,
  unmapped,
}: {
  reason: string | null | undefined;
  unmapped: string[];
}) {
  const isUnmapped = unmapped.length > 0;
  const variant = isUnmapped ? "destructive" : "default";
  return (
    <div className="space-y-3">
      <Alert variant={variant}>
        <AlertDescription>
          {reason ?? "Split comparison is not available."}
        </AlertDescription>
      </Alert>
      {isUnmapped && (
        <Alert variant="destructive">
          <AlertDescription>
            Unmapped categories: {unmapped.join(", ")}. Map them in{" "}
            <Link to="/settings/ynab" className="underline">
              YNAB Settings
            </Link>
            .
          </AlertDescription>
        </Alert>
      )}
    </div>
  );
}

function NotYetPushedState({
  comparisons,
}: {
  comparisons: TransactionSplitComparisonDto[];
}) {
  return (
    <div className="space-y-4">
      <p className="text-sm text-muted-foreground">
        Actual will appear here after you push to YNAB.
      </p>
      {comparisons.map((tc) => (
        <ExpectedOnlyTransactionSection key={tc.localTransactionId} tc={tc} />
      ))}
    </div>
  );
}

function PushedState({
  comparisons,
  hasAnyMismatch,
}: {
  comparisons: TransactionSplitComparisonDto[];
  hasAnyMismatch: boolean;
}) {
  return (
    <div className="space-y-4">
      {hasAnyMismatch && (
        <Badge variant="destructive">Mismatch detected</Badge>
      )}
      {comparisons.map((tc) => (
        <TransactionSection key={tc.localTransactionId} tc={tc} />
      ))}
    </div>
  );
}

function ExpectedOnlyTransactionSection({
  tc,
}: {
  tc: TransactionSplitComparisonDto;
}) {
  const sortedExpected = useMemo(
    () => sortLinesByAmountDesc(tc.expected),
    [tc.expected],
  );
  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between">
        <h4 className="text-sm font-semibold">{tc.accountName}</h4>
        <span className="text-sm text-muted-foreground">
          {formatMilliunits(tc.totalMilliunits)}
        </span>
      </div>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Category</TableHead>
            <TableHead className="text-right">Expected</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {sortedExpected.map((line, idx) => (
            <TableRow key={`${line.ynabCategoryId}-${idx}`}>
              <TableCell>{line.categoryName}</TableCell>
              <TableCell className="text-right">
                {formatMilliunits(line.milliunits)}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

function TransactionSection({ tc }: { tc: TransactionSplitComparisonDto }) {
  const rows = useMemo(() => buildRows(tc), [tc]);
  const showActualColumn = tc.actual != null;

  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between">
        <h4 className="text-sm font-semibold">{tc.accountName}</h4>
        <div className="flex items-center gap-2">
          {tc.matches === false && (
            <Badge variant="destructive">Mismatch</Badge>
          )}
          <span className="text-sm text-muted-foreground">
            {formatMilliunits(tc.totalMilliunits)}
          </span>
        </div>
      </div>

      {tc.actualFetchError && (
        <Alert variant="destructive">
          <AlertDescription>
            Could not fetch current YNAB state: {tc.actualFetchError}
          </AlertDescription>
        </Alert>
      )}

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Category</TableHead>
            <TableHead className="text-right">Expected</TableHead>
            {showActualColumn && (
              <TableHead className="text-right">Actual</TableHead>
            )}
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row, idx) => (
            <TableRow
              key={`${row.categoryName}-${idx}`}
              className={
                row.isMismatch
                  ? "bg-yellow-500/10 border-l-2 border-l-yellow-500"
                  : undefined
              }
            >
              <TableCell>{row.categoryName}</TableCell>
              <TableCell className="text-right">
                {row.expected != null ? formatMilliunits(row.expected) : "—"}
              </TableCell>
              {showActualColumn && (
                <TableCell className="text-right">
                  {row.actual != null ? formatMilliunits(row.actual) : "—"}
                </TableCell>
              )}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

type ComparisonRow = {
  categoryName: string;
  ynabCategoryId: string;
  expected: number | null;
  actual: number | null;
  isMismatch: boolean;
};

/**
 * Merges expected and actual split lines into a single row set. Rows are
 * keyed by a `(categoryId, milliunits)` tuple — mirroring the server's
 * SplitsMatch logic — so that duplicate-category lines with different
 * amounts survive the merge and unmatched lines surface as mismatches.
 * Sorted by absolute amount descending.
 */
function buildRows(tc: TransactionSplitComparisonDto): ComparisonRow[] {
  const keyOf = (line: SplitLineDto) =>
    `${line.ynabCategoryId}::${line.milliunits}`;

  const byKey = new Map<string, ComparisonRow>();
  for (const line of tc.expected) {
    byKey.set(keyOf(line), {
      categoryName: line.categoryName,
      ynabCategoryId: line.ynabCategoryId,
      expected: line.milliunits,
      actual: null,
      isMismatch: false,
    });
  }
  if (tc.actual) {
    for (const line of tc.actual) {
      const existing = byKey.get(keyOf(line));
      if (existing) {
        // Same (category, amount) on both sides — a clean match.
        existing.actual = line.milliunits;
      } else {
        byKey.set(keyOf(line), {
          categoryName: line.categoryName,
          ynabCategoryId: line.ynabCategoryId,
          expected: null,
          actual: line.milliunits,
          isMismatch: true,
        });
      }
    }
    // Any row with only expected (no actual pair) is a mismatch.
    for (const row of byKey.values()) {
      if (row.actual == null) {
        row.isMismatch = true;
      }
    }
  }
  return Array.from(byKey.values()).sort((a, b) => {
    const aVal = Math.abs(a.expected ?? a.actual ?? 0);
    const bVal = Math.abs(b.expected ?? b.actual ?? 0);
    return bVal - aVal;
  });
}

function sortLinesByAmountDesc(lines: SplitLineDto[]): SplitLineDto[] {
  return [...lines].sort(
    (a, b) => Math.abs(b.milliunits) - Math.abs(a.milliunits),
  );
}
