import { useMemo, useState } from "react";
import {
  useCreateTransaction,
  useUpdateTransaction,
  useDeleteTransactions,
} from "@/hooks/useTransactions";
import { useAllCards } from "@/hooks/useCards";
import {
  ReceiptTransactionForm,
  type ReceiptTransactionFormValues,
} from "@/components/ReceiptTransactionForm";
import {
  parseProblemDetails,
  extractFieldErrors,
} from "@/lib/problem-details";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Spinner } from "@/components/ui/spinner";
import {
  Table,
  TableBody,
  TableCell,
  TableFooter,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Checkbox } from "@/components/ui/checkbox";
import { formatCurrency } from "@/lib/format";
import { Pencil } from "lucide-react";

interface TransactionRow {
  transaction: {
    id: string;
    amount: number;
    date: string;
    cardId: string | null;
  };
  account: {
    id: string;
    name: string;
    isActive: boolean;
  };
}

interface ReceiptTransactionsCardProps {
  receiptId: string;
  receiptDate?: string;
  transactions: TransactionRow[];
  transactionsTotal: number;
}

export function ReceiptTransactionsCard({
  receiptId,
  receiptDate,
  transactions,
  transactionsTotal,
}: ReceiptTransactionsCardProps) {
  const createTransaction = useCreateTransaction();
  const updateTransaction = useUpdateTransaction();
  const deleteTransactions = useDeleteTransactions();

  const { data: cards } = useAllCards();

  const cardNameMap = useMemo(() => {
    const map = new Map<string, string>();
    for (const c of cards ?? []) map.set(c.id, c.name);
    return map;
  }, [cards]);

  const [createOpen, setCreateOpen] = useState(false);
  const [editTxn, setEditTxn] = useState<TransactionRow | null>(null);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [selectedTxns, setSelectedTxns] = useState<Set<string>>(new Set());
  const [serverErrors, setServerErrors] = useState<Record<string, string>>({});

  function handleCreate(values: ReceiptTransactionFormValues) {
    setServerErrors({});
    createTransaction.mutate(
      {
        receiptId,
        body: {
          cardId: values.cardId,
          accountId: values.accountId,
          amount: values.amount,
          date: values.date,
        },
      },
      {
        onSuccess: () => setCreateOpen(false),
        onError: (error) => {
          const problem = parseProblemDetails(error);
          if (problem) setServerErrors(extractFieldErrors(problem));
        },
      },
    );
  }

  function handleUpdate(values: ReceiptTransactionFormValues) {
    if (!editTxn) return;
    setServerErrors({});
    updateTransaction.mutate(
      {
        body: {
          id: editTxn.transaction.id,
          cardId: values.cardId,
          accountId: values.accountId,
          amount: values.amount,
          date: values.date,
        },
      },
      {
        onSuccess: () => setEditTxn(null),
        onError: (error) => {
          const problem = parseProblemDetails(error);
          if (problem) setServerErrors(extractFieldErrors(problem));
        },
      },
    );
  }

  function toggleSelect(id: string) {
    setSelectedTxns((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  return (
    <>
      <Card>
        <CardHeader>
          <div className="flex items-center justify-between">
            <CardTitle>Transactions ({transactions.length})</CardTitle>
            <div className="flex gap-2">
              {selectedTxns.size > 0 && (
                <Button
                  variant="destructive"
                  size="sm"
                  onClick={() => setDeleteOpen(true)}
                >
                  Delete ({selectedTxns.size})
                </Button>
              )}
              <Button size="sm" onClick={() => { setServerErrors({}); setCreateOpen(true); }}>
                Add Transaction
              </Button>
            </div>
          </div>
        </CardHeader>
        <CardContent>
          {transactions.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No transactions for this receipt.
            </p>
          ) : (
            <div className="overflow-x-auto rounded-md border">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead className="w-12">
                      <Checkbox
                        aria-label="Select all transactions"
                        checked={
                          selectedTxns.size === transactions.length &&
                          transactions.length > 0
                        }
                        onCheckedChange={() => {
                          if (selectedTxns.size === transactions.length) {
                            setSelectedTxns(new Set());
                          } else {
                            setSelectedTxns(
                              new Set(transactions.map((t) => t.transaction.id)),
                            );
                          }
                        }}
                      />
                    </TableHead>
                    <TableHead className="text-right">Amount</TableHead>
                    <TableHead>Date</TableHead>
                    <TableHead>Card</TableHead>
                    <TableHead className="min-w-[16ch]">Account</TableHead>
                    <TableHead>Status</TableHead>
                    <TableHead className="w-24">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {transactions.map((ta) => (
                    <TableRow key={ta.transaction.id}>
                      <TableCell>
                        <Checkbox
                          aria-label={`Select ${ta.account.name} transaction`}
                          checked={selectedTxns.has(ta.transaction.id)}
                          onCheckedChange={() => toggleSelect(ta.transaction.id)}
                        />
                      </TableCell>
                      <TableCell className="text-right">
                        {formatCurrency(ta.transaction.amount)}
                      </TableCell>
                      <TableCell>{ta.transaction.date}</TableCell>
                      <TableCell>
                        {ta.transaction.cardId
                          ? (cardNameMap.get(ta.transaction.cardId) ?? "")
                          : ""}
                      </TableCell>
                      <TableCell>
                        <span className="block max-w-[32ch] break-words whitespace-normal">
                          {ta.account.name}
                        </span>
                      </TableCell>
                      <TableCell>
                        <Badge
                          variant={
                            ta.account.isActive ? "default" : "secondary"
                          }
                        >
                          {ta.account.isActive ? "Active" : "Inactive"}
                        </Badge>
                      </TableCell>
                      <TableCell>
                        <Button
                          variant="ghost"
                          size="icon"
                          aria-label="Edit"
                          onClick={() => {
                            setServerErrors({});
                            setEditTxn(ta);
                          }}
                        >
                          <Pencil className="h-4 w-4" />
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
                <TableFooter>
                  <TableRow>
                    <TableCell className="text-right font-medium">
                      Transaction Total
                    </TableCell>
                    <TableCell className="text-right font-bold">
                      {formatCurrency(transactionsTotal)}
                    </TableCell>
                    <TableCell colSpan={6} />
                  </TableRow>
                </TableFooter>
              </Table>
            </div>
          )}
        </CardContent>
      </Card>

      {/* Create Dialog */}
      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add Transaction</DialogTitle>
          </DialogHeader>
          <ReceiptTransactionForm
            mode="create"
            defaultValues={receiptDate ? { date: receiptDate } : undefined}
            isSubmitting={createTransaction.isPending}
            serverErrors={serverErrors}
            onCancel={() => setCreateOpen(false)}
            onSubmit={handleCreate}
          />
        </DialogContent>
      </Dialog>

      {/* Edit Dialog */}
      <Dialog
        open={editTxn !== null}
        onOpenChange={(open) => !open && setEditTxn(null)}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Edit Transaction</DialogTitle>
          </DialogHeader>
          {editTxn && (
            <ReceiptTransactionForm
              mode="edit"
              defaultValues={{
                cardId: editTxn.transaction.cardId ?? "",
                accountId: editTxn.account.id,
                amount: editTxn.transaction.amount,
                date: editTxn.transaction.date,
              }}
              isSubmitting={updateTransaction.isPending}
              serverErrors={serverErrors}
              onCancel={() => setEditTxn(null)}
              onSubmit={handleUpdate}
            />
          )}
        </DialogContent>
      </Dialog>

      {/* Delete Confirmation Dialog */}
      <Dialog open={deleteOpen} onOpenChange={setDeleteOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete Transactions</DialogTitle>
          </DialogHeader>
          <p className="text-sm text-muted-foreground">
            Are you sure you want to delete {selectedTxns.size} transaction(s)?
            This action can be undone by restoring.
          </p>
          <div className="flex justify-end gap-2 pt-4">
            <Button variant="outline" onClick={() => setDeleteOpen(false)}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              disabled={deleteTransactions.isPending}
              onClick={() => {
                const ids = [...selectedTxns];
                setSelectedTxns(new Set());
                setDeleteOpen(false);
                deleteTransactions.mutate(ids);
              }}
            >
              {deleteTransactions.isPending && <Spinner size="sm" />}
              {deleteTransactions.isPending ? "Deleting..." : "Delete"}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
