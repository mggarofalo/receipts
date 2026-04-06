import { useState } from "react";
import {
  useSyncYnabMemos,
  useResolveYnabMemoSync,
  useMemoSyncSummary,
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

export function YnabMemoSyncCard({ receiptId }: YnabMemoSyncCardProps) {
  const { selectedBudgetId } = useSelectedYnabBudget();
  const syncMemos = useSyncYnabMemos();
  const resolveSync = useResolveYnabMemoSync();
  const [results, setResults] = useState<YnabMemoSyncResult[] | undefined>();
  const [resolveTarget, setResolveTarget] = useState<{
    localTransactionId: string;
    candidates: YnabTransactionCandidateDto[];
  } | null>(null);
  const summary = useMemoSyncSummary(results);

  if (!selectedBudgetId) {
    return null;
  }

  function handleSync() {
    syncMemos.mutate(receiptId, {
      onSuccess: (data) => {
        setResults(data?.results as YnabMemoSyncResult[] | undefined);
      },
    });
  }

  function handleResolve(ynabTransactionId: string) {
    if (!resolveTarget) return;
    resolveSync.mutate(
      {
        localTransactionId: resolveTarget.localTransactionId,
        ynabTransactionId,
      },
      {
        onSuccess: () => {
          setResolveTarget(null);
          // Re-sync to refresh results
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
      <Card>
        <CardHeader>
          <div className="flex items-center justify-between">
            <div>
              <CardTitle>YNAB Memo Sync</CardTitle>
              <CardDescription>
                Match transactions and update YNAB memos with receipt links.
              </CardDescription>
            </div>
            <Button
              onClick={handleSync}
              disabled={syncMemos.isPending}
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
                {summary.failed > 0 && (
                  <Badge variant="destructive">{summary.failed} failed</Badge>
                )}
              </div>
            )}

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
              Multiple YNAB transactions match. Select the correct one.
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
                          disabled={resolveSync.isPending}
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
