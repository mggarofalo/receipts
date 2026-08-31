import { type ReceiptYnabSyncStatusValue } from "@/hooks/useYnab";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { YnabPushButton } from "@/components/YnabPushButton";
import { YnabMemoSyncContent } from "@/components/YnabMemoSyncCard";
import { YnabSplitComparisonContent } from "@/components/YnabSplitComparisonCard";

interface YnabReceiptCardProps {
  receiptId: string;
  hasTransactions: boolean;
  isAvailable: boolean;
  persistedSyncStatus?: ReceiptYnabSyncStatusValue;
}

/**
 * The receipt-level home for every YNAB workflow. The outer gate prevents
 * child controls and YNAB-dependent queries from rendering until both the
 * integration and its selected budget are available.
 */
export function YnabReceiptCard({
  receiptId,
  hasTransactions,
  isAvailable,
  persistedSyncStatus,
}: YnabReceiptCardProps) {
  if (!isAvailable) return null;

  return (
    <Card>
      <CardHeader>
        <CardTitle>YNAB</CardTitle>
        <CardDescription>
          Push this receipt, link transaction memos, and compare its category
          splits with YNAB.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-5">
        <section aria-labelledby="ynab-transaction-sync-heading">
          <div className="mb-3 space-y-1">
            <h3
              id="ynab-transaction-sync-heading"
              className="text-base font-semibold"
            >
              Transaction sync
            </h3>
            <p className="text-sm text-muted-foreground">
              Push this receipt’s transactions to YNAB with category splits.
            </p>
          </div>
          <YnabPushButton
            receiptId={receiptId}
            hasTransactions={hasTransactions}
            persistedSyncStatus={persistedSyncStatus}
          />
        </section>

        <Separator />
        <YnabMemoSyncContent receiptId={receiptId} embedded />
        <Separator />
        <YnabSplitComparisonContent receiptId={receiptId} embedded />
      </CardContent>
    </Card>
  );
}
