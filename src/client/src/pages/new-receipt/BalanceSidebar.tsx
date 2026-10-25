import { sumAmounts, calculateReceiptBalance } from "@/lib/receipt-arithmetic";
import { formatCurrency } from "@/lib/format";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
interface BalanceSidebarProps {
  subtotal: number;
  taxAmount: number;
  adjustmentTotal: number;
  transactionTotal: number;
  isSubmitting: boolean;
  onSubmit: () => void;
  onCancel: () => void;
}

export function BalanceSidebar({
  subtotal,
  taxAmount,
  adjustmentTotal,
  transactionTotal,
  isSubmitting,
  onSubmit,
  onCancel,
}: BalanceSidebarProps) {
  const expectedTotal = sumAmounts([subtotal, taxAmount, adjustmentTotal]);
  const {
    absoluteDifference: balanceDiff,
    isWithinCreationTolerance: isBalanced,
    expectedExceedsPayments: isOver,
  } = calculateReceiptBalance(expectedTotal, transactionTotal);

  return (
    <div className="flex flex-wrap items-center justify-between gap-x-6 gap-y-3">
      <div className="min-w-0 space-y-1">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
          <span className="text-sm text-muted-foreground">Receipt total</span>
          <strong className="text-lg tabular-nums">
            {formatCurrency(expectedTotal)}
          </strong>
          <span role="status" aria-live="polite">
            <Badge variant={isBalanced ? "default" : "secondary"}>
              {isBalanced
                ? "Balanced"
                : isOver
                  ? `Over by ${formatCurrency(balanceDiff)}`
                  : `Remaining: ${formatCurrency(balanceDiff)}`}
            </Badge>
          </span>
        </div>
        <details className="text-sm">
          <summary className="w-fit cursor-pointer text-muted-foreground">
            Balance details
          </summary>
          <dl className="mt-2 grid max-w-sm grid-cols-2 gap-x-6 gap-y-1 tabular-nums">
            <dt className="text-muted-foreground">Subtotal</dt>
            <dd className="text-right">{formatCurrency(subtotal)}</dd>
            <dt className="text-muted-foreground">Tax</dt>
            <dd className="text-right">{formatCurrency(taxAmount)}</dd>
            <dt className="text-muted-foreground">Adjustments</dt>
            <dd className="text-right">{formatCurrency(adjustmentTotal)}</dd>
            <dt className="font-medium">Expected Total</dt>
            <dd className="text-right font-medium">
              {formatCurrency(expectedTotal)}
            </dd>
            <dt className="font-medium">Transaction Total</dt>
            <dd className="text-right font-medium">
              {formatCurrency(transactionTotal)}
            </dd>
          </dl>
        </details>
        {!isBalanced && (
          <p className="text-xs text-muted-foreground">
            Adjust payments or items so totals match.
          </p>
        )}
      </div>
      <div className="flex items-center gap-2">
        <Button variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
        <Button onClick={onSubmit} disabled={isSubmitting || !isBalanced}>
          {isSubmitting ? "Submitting..." : "Submit Receipt"}
        </Button>
      </div>
    </div>
  );
}
