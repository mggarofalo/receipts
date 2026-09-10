import { useState } from "react";
import {
  useAllReceiptIds,
  useBulkPushYnabTransactions,
  useSyncYnabMemosBulk,
  useMemoSyncSummary,
  type YnabMemoSyncResult,
} from "@/hooks/useYnab";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Spinner } from "@/components/ui/spinner";
import { RequestFailure } from "@/components/RequestFailure";

export function YnabBulkSyncCard() {
  const {
    receiptIds,
    totalReceipts,
    isTruncated,
    isLoading: receiptsLoading,
    isError: receiptsError,
    refetch: retryReceipts,
    isFetching: receiptsFetching,
  } = useAllReceiptIds();
  const bulkPush = useBulkPushYnabTransactions();
  const bulkMemoSync = useSyncYnabMemosBulk();
  const [memoResults, setMemoResults] = useState<
    YnabMemoSyncResult[] | undefined
  >();
  const memoSummary = useMemoSyncSummary(memoResults);

  const receiptsUnavailable = receiptsError || isTruncated;
  const noReceipts =
    totalReceipts === 0 && !receiptsLoading && !receiptsUnavailable;
  const isBusy = bulkPush.isPending || bulkMemoSync.isPending;

  function handleBulkPush() {
    bulkPush.mutate(receiptIds);
  }

  function handleBulkMemoSync() {
    bulkMemoSync.mutate(receiptIds, {
      onSuccess: (data) => {
        setMemoResults(data?.results);
      },
    });
  }

  const pushData = bulkPush.data;
  const pushSucceeded =
    pushData?.results?.filter((r) => r.result.operationStatus === "synced")
      .length ?? 0;
  const pushNeedsReview =
    pushData?.results?.filter(
      (r) =>
        r.result.operationStatus === "unknown" ||
        r.result.operationStatus === "pending",
    ).length ?? 0;
  const pushFailed =
    (pushData?.results?.length ?? 0) - pushSucceeded - pushNeedsReview;
  const pushTotal = pushData?.results?.length ?? 0;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Bulk YNAB Sync</CardTitle>
        <CardDescription>
          Push all receipts to YNAB or sync all transaction memos at once.
          {totalReceipts > 0 && !receiptsLoading && (
            <span className="ml-1">({totalReceipts} receipts)</span>
          )}
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {receiptsUnavailable && (
          <RequestFailure
            message={
              isTruncated
                ? `Loaded ${receiptIds.length.toLocaleString()} receipt IDs, but the server reported ${totalReceipts.toLocaleString()} receipts. Bulk YNAB actions are disabled.`
                : "The receipt list is unavailable. Bulk YNAB actions are disabled until all receipts can be loaded."
            }
            retry={() => {
              void retryReceipts();
            }}
            isRetrying={receiptsFetching}
          />
        )}
        {/* Push All to YNAB */}
        <div className="space-y-2">
          <div className="flex items-center gap-3">
            <Button
              variant="outline"
              onClick={handleBulkPush}
              disabled={
                isBusy || noReceipts || receiptsLoading || receiptsUnavailable
              }
            >
              {bulkPush.isPending ? (
                <>
                  <Spinner className="mr-2 h-3 w-3" />
                  Pushing to YNAB...
                </>
              ) : (
                "Push All to YNAB"
              )}
            </Button>

            {/* aria-live region so screen readers announce push result badges inline */}
            <div aria-live="polite" aria-atomic="true">
              {pushData && pushTotal > 0 && (
                <div className="flex gap-2">
                  {pushSucceeded > 0 && (
                    <Badge
                      variant="outline"
                      className="border-green-300 text-green-600"
                    >
                      {pushSucceeded} succeeded
                    </Badge>
                  )}
                  {pushFailed > 0 && (
                    <Badge
                      variant="outline"
                      className="border-destructive/50 text-destructive"
                    >
                      {pushFailed} failed
                    </Badge>
                  )}
                  {pushNeedsReview > 0 && (
                    <Badge
                      variant="outline"
                      className="border-amber-300 text-amber-700"
                    >
                      {pushNeedsReview} need review
                    </Badge>
                  )}
                </div>
              )}
            </div>
          </div>

          {/* Error alerts outside the flex row so they span full width */}
          {pushData &&
            pushData.results
              ?.filter((r) => r.result.error)
              .map((r) => (
                <Alert
                  key={r.receiptId}
                  variant={
                    r.result.operationStatus === "failed"
                      ? "destructive"
                      : "default"
                  }
                  role="alert"
                >
                  <AlertDescription>
                    Receipt {r.receiptId.slice(0, 8)}...: {r.result.error}
                  </AlertDescription>
                </Alert>
              ))}

          {bulkPush.isError && (
            <Alert variant="destructive" role="alert">
              <AlertDescription>
                Failed to push transactions to YNAB. Please try again.
              </AlertDescription>
            </Alert>
          )}
        </div>

        {/* Sync All Memos */}
        <div className="space-y-2">
          <div className="flex items-center gap-3">
            <Button
              variant="outline"
              onClick={handleBulkMemoSync}
              disabled={
                isBusy || noReceipts || receiptsLoading || receiptsUnavailable
              }
            >
              {bulkMemoSync.isPending ? (
                <>
                  <Spinner className="mr-2 h-3 w-3" />
                  Syncing Memos...
                </>
              ) : (
                "Sync All Memos"
              )}
            </Button>

            {/* aria-live region so screen readers announce memo sync result badges inline */}
            <div aria-live="polite" aria-atomic="true">
              {memoSummary && (
                <div className="flex flex-wrap gap-2">
                  {memoSummary.synced > 0 && (
                    <Badge variant="default">{memoSummary.synced} synced</Badge>
                  )}
                  {memoSummary.alreadySynced > 0 && (
                    <Badge variant="secondary">
                      {memoSummary.alreadySynced} already synced
                    </Badge>
                  )}
                  {memoSummary.noMatch > 0 && (
                    <Badge variant="secondary">
                      {memoSummary.noMatch} no match
                    </Badge>
                  )}
                  {memoSummary.ambiguous > 0 && (
                    <Badge variant="outline">
                      {memoSummary.ambiguous} ambiguous
                    </Badge>
                  )}
                  {memoSummary.currencySkipped > 0 && (
                    <Badge variant="secondary">
                      {memoSummary.currencySkipped} currency skipped
                    </Badge>
                  )}
                  {memoSummary.reconciledSkipped > 0 && (
                    <Badge variant="secondary">
                      {memoSummary.reconciledSkipped} reconciled
                    </Badge>
                  )}
                  {memoSummary.failed > 0 && (
                    <Badge variant="destructive">
                      {memoSummary.failed} failed
                    </Badge>
                  )}
                </div>
              )}
            </div>
          </div>

          {/* Error alert outside the flex row so it spans full width */}
          {bulkMemoSync.isError && (
            <Alert variant="destructive" role="alert">
              <AlertDescription>
                Failed to sync memos to YNAB. Please try again.
              </AlertDescription>
            </Alert>
          )}
        </div>

        {noReceipts && (
          <p className="text-sm text-muted-foreground">
            No receipts found. Create some receipts first.
          </p>
        )}
      </CardContent>
    </Card>
  );
}
