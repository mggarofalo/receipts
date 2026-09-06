import { RequestFailure } from "@/components/RequestFailure";
import { useLayoutEffect, useRef, useState } from "react";
import {
  useSyncYnabMemos,
  useResolveYnabMemoSync,
  useMemoSyncSummary,
  useYnabConnectionStatus,
  type YnabMemoSyncResult,
  type YnabTransactionCandidateDto,
} from "@/hooks/useYnab";
import { useSelectedYnabBudget } from "@/hooks/useYnab";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from "@/components/ui/dialog";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Spinner } from "@/components/ui/spinner";

interface YnabMemoSyncCardProps {
  receiptId: string;
  embedded?: boolean;
  disabled?: boolean;
}

function outcomeLabel(outcome: string): string {
  switch (outcome) {
    case "Synced":
      return "Synced";
    case "AlreadySynced":
      return "Already synced";
    case "NoMatch":
      return "No match";
    case "Ambiguous":
      return "Ambiguous";
    case "CurrencySkipped":
      return "Currency skipped";
    case "ReconciledSkipped":
      return "Reconciled";
    case "Failed":
      return "Failed";
    default:
      return outcome;
  }
}

function outcomeBadgeVariant(
  outcome: string,
): "default" | "secondary" | "destructive" | "outline" {
  switch (outcome) {
    case "Synced":
      return "default";
    case "AlreadySynced":
      return "secondary";
    case "Ambiguous":
      return "outline";
    case "NoMatch":
    case "CurrencySkipped":
    case "ReconciledSkipped":
      return "secondary";
    case "Failed":
      return "destructive";
    default:
      return "outline";
  }
}

function formatMilliunits(amount: number): string {
  return (amount / 1000).toLocaleString(undefined, {
    style: "currency",
    currency: "USD",
  });
}

export function YnabMemoSyncCard({
  receiptId,
  embedded = false,
  disabled = false,
}: YnabMemoSyncCardProps) {
  const { isConfigured, isLoading: connectionLoading, isError: connectionError, refetch: retryConnection, isFetching: connectionFetching } =
    useYnabConnectionStatus();
  const { selectedBudgetId, isError: budgetError, refetch: retryBudget, isFetching: budgetFetching } = useSelectedYnabBudget();

  const unavailable = disabled || connectionError || budgetError;
  return <>
    {unavailable && <RequestFailure
      message="YNAB is temporarily unavailable."
      retry={() => { void retryConnection(); void retryBudget(); }}
      isRetrying={connectionFetching || budgetFetching}
    />}
    {!connectionLoading && isConfigured && selectedBudgetId && (
      <fieldset disabled={unavailable} className="min-w-0">
        <YnabMemoSyncContent receiptId={receiptId} embedded={embedded} disabled={unavailable} />
      </fieldset>
    )}
  </>;
}

export function YnabMemoSyncContent({
  receiptId,
  embedded = false,
  disabled = false,
}: YnabMemoSyncCardProps) {
  // An earlier mutation callback must consult the latest committed availability,
  // before launching another write. Commit synchronously before external callbacks run.
  const disabledRef = useRef(disabled);
  useLayoutEffect(() => { disabledRef.current = disabled; }, [disabled]);
  const syncMemos = useSyncYnabMemos();
  const resolveSync = useResolveYnabMemoSync();
  const [results, setResults] = useState<YnabMemoSyncResult[] | undefined>();
  const [resolveTarget, setResolveTarget] = useState<{
    localTransactionId: string;
    candidates: YnabTransactionCandidateDto[];
  } | null>(null);
  const summary = useMemoSyncSummary(results);

  function handleSync() {
    if (disabled) return;
    syncMemos.mutate(receiptId, {
      onSuccess: (data) => {
        setResults(data?.results as YnabMemoSyncResult[] | undefined);
      },
    });
  }

  function handleResolve(ynabTransactionId: string) {
    if (disabled || !resolveTarget) return;
    resolveSync.mutate(
      {
        localTransactionId: resolveTarget.localTransactionId,
        ynabTransactionId,
      },
      {
        onSuccess: () => {
          setResolveTarget(null);
          // Preserve the completed resolution, but wait for recovery before a new write.
          if (disabledRef.current) return;
          syncMemos.mutate(receiptId, {
            onSuccess: (data) => {
              setResults(data?.results as YnabMemoSyncResult[] | undefined);
            },
          });
        },
      },
    );
  }

  return (
    <>
      <Card className={embedded ? "border-0 shadow-none" : undefined}>
        <CardHeader>
          <div className="flex items-center justify-between">
            <div>
              <CardTitle className={embedded ? "text-base" : undefined}>
                Memo sync
              </CardTitle>
              <CardDescription>
                Match transactions and update YNAB memos with receipt links.
              </CardDescription>
            </div>
            <Button
              onClick={handleSync}
              disabled={disabled || syncMemos.isPending}
              size="sm"
            >
              {syncMemos.isPending ? (
                <>
                  <Spinner className="mr-2 h-4 w-4" />
                  Syncing...
                </>
              ) : (
                "Sync Memos"
              )}
            </Button>
          </div>
        </CardHeader>

        {results && results.length > 0 && (
          <CardContent>
            {/* aria-live region so screen readers announce memo sync outcomes */}
            <div aria-live="polite" aria-atomic="true">
              {summary && (
                <div className="mb-4 flex flex-wrap gap-2 text-sm text-muted-foreground">
                  {summary.synced > 0 && (
                    <Badge variant="default">{summary.synced} synced</Badge>
                  )}
                  {summary.alreadySynced > 0 && (
                    <Badge variant="secondary">
                      {summary.alreadySynced} already synced
                    </Badge>
                  )}
                  {summary.noMatch > 0 && (
                    <Badge variant="secondary">
                      {summary.noMatch} no match
                    </Badge>
                  )}
                  {summary.ambiguous > 0 && (
                    <Badge variant="outline">
                      {summary.ambiguous} ambiguous
                    </Badge>
                  )}
                  {summary.reconciledSkipped > 0 && (
                    <Badge variant="secondary">
                      {summary.reconciledSkipped} reconciled
                    </Badge>
                  )}
                  {summary.failed > 0 && (
                    <Badge variant="destructive">{summary.failed} failed</Badge>
                  )}
                </div>
              )}
            </div>

            <div className="space-y-2">
              {results.map((result) => (
                <div
                  key={result.localTransactionId}
                  className="flex items-center justify-between rounded-md border p-3"
                >
                  <div className="flex items-center gap-3">
                    <Badge variant={outcomeBadgeVariant(result.outcome)}>
                      {outcomeLabel(result.outcome)}
                    </Badge>
                    {result.error && (
                      <span className="text-sm text-destructive">
                        {result.error}
                      </span>
                    )}
                  </div>
                  {result.outcome === "Ambiguous" &&
                    result.ambiguousCandidates && (
                      <Button
                        variant="outline"
                        size="sm"
                        disabled={disabled}
                        onClick={() =>
                          setResolveTarget({
                            localTransactionId: result.localTransactionId,
                            candidates:
                              result.ambiguousCandidates as YnabTransactionCandidateDto[],
                          })
                        }
                      >
                        Resolve
                      </Button>
                    )}
                </div>
              ))}
            </div>
          </CardContent>
        )}

        {results && results.length === 0 && (
          <CardContent>
            <p className="text-sm text-muted-foreground">
              No transactions found for this receipt.
            </p>
          </CardContent>
        )}
      </Card>

      {/* Resolve Ambiguous Dialog */}
      <Dialog
        open={!!resolveTarget}
        onOpenChange={(open) => !open && setResolveTarget(null)}
      >
        <DialogContent className="max-w-2xl">
          <DialogHeader>
            <DialogTitle>Resolve Ambiguous Match</DialogTitle>
            <DialogDescription>
              Automatic matching was inconclusive. Review the transaction details and
              select a match only if it is correct.
            </DialogDescription>
          </DialogHeader>
          {resolveTarget && (
            <div className="rounded-md border">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Date</TableHead>
                    <TableHead>Payee</TableHead>
                    <TableHead className="text-right">Amount</TableHead>
                    <TableHead>Memo</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {resolveTarget.candidates.map((candidate) => (
                    <TableRow key={candidate.id}>
                      <TableCell>{candidate.date}</TableCell>
                      <TableCell>{candidate.payeeName ?? "\u2014"}</TableCell>
                      <TableCell className="text-right">
                        {formatMilliunits(candidate.amount)}
                      </TableCell>
                      <TableCell className="max-w-[200px] truncate text-muted-foreground">
                        {candidate.memo ?? "\u2014"}
                      </TableCell>
                      <TableCell>
                        <Button
                          size="sm"
                          variant="outline"
                          disabled={disabled || resolveSync.isPending}
                          onClick={() => handleResolve(candidate.id)}
                        >
                          Select
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
        </DialogContent>
      </Dialog>
    </>
  );
}
