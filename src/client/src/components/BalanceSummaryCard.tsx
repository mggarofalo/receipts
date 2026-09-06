import { calculateReceiptBalance } from "@/lib/receipt-arithmetic";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { formatCurrency } from "@/lib/format";

interface BalanceSummaryCardProps {
  subtotal: number;
  taxAmount: number;
  adjustmentTotal: number;
  expectedTotal: number;
  transactionsTotal?: number;
  showBalance?: boolean;
}

export function BalanceSummaryCard({
  subtotal,
  taxAmount,
  adjustmentTotal,
  expectedTotal,
  transactionsTotal,
  showBalance = false,
}: BalanceSummaryCardProps) {
  const isBalanced =
    transactionsTotal != null
      ? !calculateReceiptBalance(expectedTotal, transactionsTotal).hasVisibleDiscrepancy
      : true;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Balance Summary</CardTitle>
      </CardHeader>
      <CardContent>
        <div className="grid grid-cols-2 gap-x-8 gap-y-2 text-sm sm:grid-cols-4">
          <div>
            <p className="text-muted-foreground">Subtotal</p>
            <p className="text-lg font-semibold">{formatCurrency(subtotal)}</p>
          </div>
          <div>
            <p className="text-muted-foreground">Tax</p>
            <p className="text-lg font-semibold">{formatCurrency(taxAmount)}</p>
          </div>
          <div>
            <p className="text-muted-foreground">Adjustments</p>
            <p className="text-lg font-semibold">{formatCurrency(adjustmentTotal)}</p>
          </div>
          <div>
            <p className="text-muted-foreground">Expected Total</p>
            <p className="text-lg font-semibold">{formatCurrency(expectedTotal)}</p>
          </div>
        </div>
        {showBalance && transactionsTotal != null && (
          <div className="mt-4 flex items-center gap-2 text-sm">
            <span className="text-muted-foreground">Transactions Total:</span>
            <span className="font-semibold">{formatCurrency(transactionsTotal)}</span>
            <Badge variant={isBalanced ? "default" : "destructive"}>
              {isBalanced ? "Balanced" : "Unbalanced"}
            </Badge>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
