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

export function YnabBulkSyncCard() {
  const { receiptIds, totalReceipts, isLoading: receiptsLoading } =
    useAllReceiptIds();
  const bulkPush = useBulkPushYnabTransactions();
  const bulkMemoSync = useSyncYnabMemosBulk();
  const [memoResults, setMemoResults] = useState<
    YnabMemoSyncResult[] | undefined
  >();
  const memoSummary = useMemoSyncSummary(memoResults);

  const noReceipts = totalReceipts === 0 && !receiptsLoading;
  const isBusy = bulkPush.isPending || bulkMemoSync.isPending;

  function handleBulkPush() {
    bulkPush.mutate(receiptIds);
  }

  function handleBulkMemoSync() {
    bulkMemoSync.mutate(receiptIds, {
      onSuccess: (data) => {
        setMemoResults(data?.results as YnabMemoSyncResult[] | undefined);
      },
    });
  }

  const pushData = bulkPush.data;
  const pushSucceeded =
    pushData?.results?.filter((r) => r.result.success).length ?? 0;
  const pushFailed =
    pushData?.results?.filter((r) => !r.result.success).length ?? 0;
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
        {/* Push All to YNAB */}
        <div className="space-y-2">
          <div className="flex items-center gap-3">
            <Button
              variant="outline"
              onClick={handleBulkPush}
              disabled={isBusy || noReceipts || receiptsLoading}
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
              </div>
            )}
          </div>

          {pushData &&
            pushData.results
              ?.filter((r) => !r.result.success && r.result.error)
              .map((r) => (
                <Alert key={r.receiptId} variant="destructive">
                  <AlertDescription>
                    Receipt {r.receiptId.slice(0, 8)}...: {r.result.error}
                  </AlertDescription>
                </Alert>
              ))}
        </div>

        {/* Sync All Memos */}
        <div className="space-y-2">
          <div className="flex items-center gap-3">
            <Button
              variant="outline"
              onClick={handleBulkMemoSync}
              disabled={isBusy || noReceipts || receiptsLoading}
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
                {memoSummary.failed > 0 && (
                  <Badge variant="destructive">
                    {memoSummary.failed} failed
                  </Badge>
                )}
              </div>
            )}
          </div>
        </div>

        {bulkPush.isError && (
          <Alert variant="destructive">
            <AlertDescription>
              Failed to push transactions to YNAB. Please try again.
            </AlertDescription>
          </Alert>
        )}

        {bulkMemoSync.isError && (
          <Alert variant="destructive">
            <AlertDescription>
              Failed to sync memos to YNAB. Please try again.
            </AlertDescription>
          </Alert>
        )}

        {noReceipts && (
          <p className="text-sm text-muted-foreground">
            No receipts found. Create some receipts first.
          </p>
        )}
      </CardContent>
    </Card>
  );
}
