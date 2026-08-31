import { formatCurrency } from "@/lib/format";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip";

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
  const expectedTotal = subtotal + taxAmount + adjustmentTotal;
  const balanceDiff = Math.abs(expectedTotal - transactionTotal);
  const isBalanced = balanceDiff < 0.01;
  const isOver = expectedTotal > transactionTotal;

  return (
    <div className="sticky top-6 space-y-4">
      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="text-lg">Balance</CardTitle>
        </CardHeader>
        <CardContent>
          <dl className="grid grid-cols-2 gap-y-2 text-sm">
            <dt className="text-muted-foreground">Subtotal</dt>
            <dd className="text-right">{formatCurrency(subtotal)}</dd>
            <dt className="text-muted-foreground">Tax</dt>
            <dd className="text-right">{formatCurrency(taxAmount)}</dd>
            <dt className="text-muted-foreground">Adjustments</dt>
            <dd className="text-right">{formatCurrency(adjustmentTotal)}</dd>
            <Separator className="col-span-2 my-1" />
            <dt className="font-medium">Expected Total</dt>
            <dd className="text-right font-medium">
              {formatCurrency(expectedTotal)}
            </dd>
            <dt className="font-medium">Transaction Total</dt>
            <dd className="text-right font-medium">
              {formatCurrency(transactionTotal)}
            </dd>
          </dl>
          <div className="mt-3 text-right" role="status" aria-live="polite">
            <Badge variant={isBalanced ? "default" : "secondary"}>
              {isBalanced
                ? "Balanced"
                : isOver
                  ? `Over by ${formatCurrency(balanceDiff)}`
                  : `Remaining: ${formatCurrency(balanceDiff)}`}
            </Badge>
          </div>
        </CardContent>
      </Card>

      <div className="space-y-2">
        <Tooltip>
          <TooltipTrigger asChild>
            <span className="inline-block w-full">
              <Button
                className="w-full"
                onClick={onSubmit}
                disabled={isSubmitting || !isBalanced}
              >
                {isSubmitting ? "Submitting..." : "Submit Receipt"}
              </Button>
            </span>
          </TooltipTrigger>
          {!isBalanced && (
            <TooltipContent>
              <p>
                {isOver
                  ? `Over by ${formatCurrency(balanceDiff)}.`
                  : `${formatCurrency(balanceDiff)} remaining.`}{" "}
                Adjust transactions or line items so totals match.
              </p>
            </TooltipContent>
          )}
        </Tooltip>
        <Button variant="ghost" className="w-full" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </div>
  );
}
