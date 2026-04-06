import { usePushYnabTransactions } from "@/hooks/useYnab";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { Badge } from "@/components/ui/badge";
import { Alert, AlertDescription } from "@/components/ui/alert";

interface YnabPushButtonProps {
  receiptId: string;
  hasTransactions: boolean;
}

export function YnabPushButton({
  receiptId,
  hasTransactions,
}: YnabPushButtonProps) {
  const pushMutation = usePushYnabTransactions();

  const handlePush = () => {
    pushMutation.mutate(receiptId);
  };

  const result = pushMutation.data;
  const hasSynced = result?.success === true;

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-3">
        <Button
          variant="outline"
          size="sm"
          onClick={handlePush}
          disabled={pushMutation.isPending || hasSynced || !hasTransactions}
        >
          {pushMutation.isPending ? (
            <>
              <Spinner className="mr-2 h-3 w-3" />
              Pushing to YNAB...
            </>
          ) : hasSynced ? (
            "Pushed to YNAB"
          ) : (
            "Push to YNAB"
          )}
        </Button>

        {hasSynced && (
          <Badge variant="outline" className="text-green-600 border-green-300">
            Synced
          </Badge>
        )}

        {result && !result.success && (
          <Badge
            variant="outline"
            className="text-destructive border-destructive/50"
          >
            Failed
          </Badge>
        )}
      </div>

      {result && !result.success && result.error && (
        <Alert variant="destructive">
          <AlertDescription>{result.error}</AlertDescription>
        </Alert>
      )}

      {result &&
        !result.success &&
        result.unmappedCategories &&
        result.unmappedCategories.length > 0 && (
          <Alert variant="destructive">
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

      {hasSynced && result.pushedTransactions.length > 0 && (
        <div className="text-sm text-muted-foreground">
          {result.pushedTransactions.length} transaction(s) pushed
          {result.pushedTransactions.some((t) => t.subTransactionCount > 1) &&
            " with category splits"}
        </div>
      )}
    </div>
  );
}
