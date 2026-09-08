import {
  usePushYnabTransactions,
  type ReceiptYnabSyncStatusValue,
} from "@/hooks/useYnab";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { YnabSyncBadge } from "@/components/YnabSyncBadge";
import { Alert, AlertDescription } from "@/components/ui/alert";

interface YnabPushButtonProps {
  receiptId: string;
  hasTransactions: boolean;
  persistedSyncStatus?: ReceiptYnabSyncStatusValue;
  syncStatusUnavailable?: boolean;
}

export function YnabPushButton({
  receiptId,
  hasTransactions,
  persistedSyncStatus,
  syncStatusUnavailable = false,
}: YnabPushButtonProps) {
  const pushMutation = usePushYnabTransactions();

  const handlePush = () => {
    pushMutation.mutate(receiptId);
  };

  const result = pushMutation.data;
  const mutationSucceeded = result?.success === true;
  const mutationFailed = result != null && result.success === false;

  // Effective status: a fresh mutation result trumps whatever was persisted.
  // Otherwise fall back to the status fetched on page load.
  const effectiveStatus: ReceiptYnabSyncStatusValue | undefined =
    mutationSucceeded
      ? "synced"
      : mutationFailed
        ? "failed"
        : persistedSyncStatus;

  const isSynced = effectiveStatus === "synced";

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-3">
        <Button
          variant="outline"
          size="sm"
          onClick={handlePush}
          disabled={pushMutation.isPending || isSynced || !hasTransactions || syncStatusUnavailable}
        >
          {pushMutation.isPending ? (
            <>
              <Spinner className="mr-2 h-3 w-3" />
              Pushing to YNAB...
            </>
          ) : mutationSucceeded ? (
            "Pushed to YNAB"
          ) : isSynced ? (
            "Already pushed"
          ) : (
            "Push to YNAB"
          )}
        </Button>

        {syncStatusUnavailable && !mutationSucceeded ? (
          <span className="text-sm text-muted-foreground">Sync status unavailable</span>
        ) : <YnabSyncBadge status={effectiveStatus} />}
      </div>

      {/* aria-live region so screen readers announce push outcomes */}
      <div aria-live="polite" aria-atomic="true">
        {result && !result.success && result.error && (
          <Alert variant="destructive" role="alert">
            <AlertDescription>{result.error}</AlertDescription>
          </Alert>
        )}

        {result &&
          !result.success &&
          result.unmappedCategories &&
          result.unmappedCategories.length > 0 && (
            <Alert variant="destructive" role="alert">
              <AlertDescription>
                Unmapped categories:{" "}
                {result.unmappedCategories.join(", ")}. Map them in{" "}
                <a href="/settings/ynab" className="underline">
                  YNAB Settings
                </a>
                .
              </AlertDescription>
            </Alert>
          )}

        {mutationSucceeded && result != null && result.pushedTransactions.length > 0 && (
          <div className="text-sm text-muted-foreground">
            {result.pushedTransactions.length} transaction(s) pushed
            {result.pushedTransactions.some((t) => t.subTransactionCount > 1) &&
              " with category splits"}
          </div>
        )}
      </div>
    </div>
  );
}
