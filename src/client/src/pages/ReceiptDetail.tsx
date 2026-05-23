import { useState } from "react";
import { Link, useParams, Navigate } from "react-router";
import { useTripByReceiptId } from "@/hooks/useTrips";
import { useUpdateReceipt } from "@/hooks/useReceipts";
import { useCreateAdjustment } from "@/hooks/useAdjustments";
import {
  useReceiptYnabSyncStatuses,
  useYnabConnectionStatus,
} from "@/hooks/useYnab";
import { usePageTitle } from "@/hooks/usePageTitle";
import {
  parseProblemDetails,
  extractFieldErrors,
} from "@/lib/problem-details";
import { ValidationWarnings } from "@/components/ValidationWarnings";
import { BalanceSummaryCard } from "@/components/BalanceSummaryCard";
import { ReceiptItemsCard } from "@/components/ReceiptItemsCard";
import { ReceiptTransactionsCard } from "@/components/ReceiptTransactionsCard";
import { AdjustmentsCard } from "@/components/AdjustmentsCard";
import {
  ReceiptHeaderForm,
  type ReceiptHeaderFormValues,
} from "@/components/ReceiptHeaderForm";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { ChangeHistory } from "@/components/ChangeHistory";
import { YnabMemoSyncCard } from "@/components/YnabMemoSyncCard";
import { CardSkeleton } from "@/components/ui/card-skeleton";
import { YnabPushButton } from "@/components/YnabPushButton";
import { YnabSplitComparisonCard } from "@/components/YnabSplitComparisonCard";
import { ReconcileSheet } from "@/components/ReconcileSheet";
import { Icon, PageHead, YnabChip } from "@/components/primitives";

function ReceiptDetail() {
  usePageTitle("Receipt Detail");
  const { id } = useParams<{ id: string }>();

  const { data: trip, isLoading, isError } = useTripByReceiptId(id ?? null);
  const updateReceipt = useUpdateReceipt();
  const createAdjustment = useCreateAdjustment();
  const { statusMap: ynabStatusMap } = useReceiptYnabSyncStatuses(
    id ? [id] : [],
  );
  const persistedYnabStatus = id ? ynabStatusMap.get(id) : undefined;
  // YNAB-gated rendering — when no PAT is configured, the YNAB push and chip
  // surfaces can't do anything useful (RECEIPTS-731). The split-comparison
  // and memo-sync cards self-gate; the push Card and PageHead chip below
  // share this signal.
  const { isConfigured: ynabConfigured, isLoading: ynabConnectionLoading } =
    useYnabConnectionStatus();
  const ynabReady = !ynabConnectionLoading && ynabConfigured;

  const [editOpen, setEditOpen] = useState(false);
  const [reconcileOpen, setReconcileOpen] = useState(false);
  const [serverErrors, setServerErrors] = useState<Record<string, string>>({});

  if (!id) {
    return <Navigate to="/receipts" replace />;
  }

  const transactionsTotal =
    trip?.transactions?.reduce(
      (sum: number, ta) => sum + Number(ta.transaction.amount ?? 0),
      0,
    ) ?? 0;

  const subtotal = Number(trip?.receipt?.subtotal ?? 0);
  const adjustmentTotal = Number(trip?.receipt?.adjustmentTotal ?? 0);
  const expectedTotal = Number(trip?.receipt?.expectedTotal ?? 0);
  const taxAmount = Number(trip?.receipt?.receipt?.taxAmount ?? 0);

  const allWarnings = [
    ...(trip?.receipt?.warnings ?? []),
    ...(trip?.warnings ?? []),
  ].map((w) => ({
    property: w.property,
    message: w.message,
    severity: w.severity != null ? Number(w.severity) : undefined,
  }));

  function handleUpdate(values: ReceiptHeaderFormValues) {
    if (!id) return;
    setServerErrors({});
    updateReceipt.mutate(
      {
        id,
        location: values.location,
        date: values.date,
        taxAmount: values.taxAmount,
      },
      {
        onSuccess: () => setEditOpen(false),
        onError: (error) => {
          const problem = parseProblemDetails(error);
          if (problem) setServerErrors(extractFieldErrors(problem));
        },
      },
    );
  }

  // Reconcile is offered only for a total mismatch — its single action is to
  // add a balancing adjustment. Validation warnings still surface in the
  // banner, but they are not something the reconcile sheet can resolve.
  const transactionsImbalanced =
    trip != null &&
    trip.transactions.length > 0 &&
    Math.abs(expectedTotal - transactionsTotal) >= 0.005;

  const yChip: "synced" | "pending" | "error" | "none" =
    persistedYnabStatus === "Synced"
      ? "synced"
      : persistedYnabStatus === "Pending"
        ? "pending"
        : persistedYnabStatus === "Failed"
          ? "error"
          : "none";

  return (
    <>
      <PageHead
        title={trip?.receipt?.receipt?.location ?? "Receipt"}
        sub={
          trip
            ? `${trip.receipt.receipt.date} · REC-${id.slice(0, 8).toUpperCase()}`
            : "Loading…"
        }
        actions={
          trip && (
            <>
              <Link to="/receipts" className="btn">
                ← All receipts
              </Link>
              <button
                type="button"
                className="btn"
                onClick={() => {
                  setServerErrors({});
                  setEditOpen(true);
                }}
              >
                <Icon.Edit /> Edit
              </button>
              {ynabReady && <YnabChip status={yChip} />}
            </>
          )
        }
      />

      {isLoading && (
        <div
          role="status"
          aria-live="polite"
          aria-busy="true"
          style={{ display: "flex", flexDirection: "column", gap: 14 }}
        >
          <span className="sr-only">Loading receipt details…</span>
          <CardSkeleton lines={1} silent />
          <CardSkeleton lines={3} silent />
          <CardSkeleton lines={3} silent />
        </div>
      )}

      {isError && (
        <div className="empty" role="alert">
          <div className="icon-frame">
            <Icon.AlertTriangle />
          </div>
          <h3>Receipt not found</h3>
          <p>No receipt matches this ID. It may have been deleted.</p>
          <div className="actions">
            <Link to="/receipts" className="btn primary">
              Back to receipts
            </Link>
          </div>
        </div>
      )}

      {trip && (
        <div
          style={{ display: "flex", flexDirection: "column", gap: 14 }}
        >
          {(allWarnings.length > 0 || transactionsImbalanced) && (
            <div
              className="warn-banner"
              role="status"
              aria-live="polite"
            >
              <Icon.AlertTriangle className="ico" aria-hidden="true" />
              <div style={{ flex: 1 }}>
                {allWarnings.length > 0 ? (
                  <ValidationWarnings warnings={allWarnings} />
                ) : (
                  <div>
                    Receipt total doesn’t match the linked transactions.
                  </div>
                )}
              </div>
              {transactionsImbalanced && (
                <button
                  type="button"
                  className="btn"
                  onClick={() => setReconcileOpen(true)}
                >
                  Reconcile
                </button>
              )}
            </div>
          )}

          <BalanceSummaryCard
            subtotal={subtotal}
            taxAmount={taxAmount}
            adjustmentTotal={adjustmentTotal}
            expectedTotal={expectedTotal}
            transactionsTotal={transactionsTotal}
            showBalance={trip.transactions.length > 0}
          />

          <ReceiptItemsCard
            receiptId={id}
            items={trip.receipt.items.map((i) => ({
              id: i.id,
              receiptItemCode: i.receiptItemCode,
              description: i.description,
              quantity: Number(i.quantity ?? 0),
              unitPrice: Number(i.unitPrice ?? 0),
              category: i.category,
              subcategory: i.subcategory,
              normalizedDescriptionName: i.normalizedDescriptionName,
            }))}
            subtotal={subtotal}
            location={trip.receipt.receipt.location}
          />

          <AdjustmentsCard
            receiptId={id}
            adjustments={trip.receipt.adjustments.map((adj) => ({
              id: adj.id,
              receiptId: id,
              type: adj.type,
              amount: Number(adj.amount ?? 0),
              description: adj.description ?? null,
            }))}
            adjustmentTotal={adjustmentTotal}
          />

          <ReceiptTransactionsCard
            receiptId={id}
            receiptDate={trip.receipt.receipt.date}
            transactions={trip.transactions.map((ta) => ({
              transaction: {
                id: ta.transaction.id,
                amount: Number(ta.transaction.amount ?? 0),
                date: ta.transaction.date,
                cardId: ta.transaction.cardId ?? null,
              },
              account: {
                id: ta.account.id,
                name: ta.account.name,
                isActive: ta.account.isActive ?? true,
              },
            }))}
            transactionsTotal={transactionsTotal}
          />

          <YnabMemoSyncCard receiptId={id} />

          {ynabReady && (
            <Card>
              <CardHeader>
                <CardTitle>YNAB sync</CardTitle>
                <CardDescription>
                  Push this receipt’s transactions to YNAB with category
                  splits.
                </CardDescription>
              </CardHeader>
              <CardContent>
                <YnabPushButton
                  receiptId={id}
                  hasTransactions={trip.transactions.length > 0}
                  persistedSyncStatus={persistedYnabStatus}
                />
              </CardContent>
            </Card>
          )}

          <YnabSplitComparisonCard receiptId={id} />

          <Card>
            <CardHeader>
              <CardTitle>Change history</CardTitle>
            </CardHeader>
            <CardContent>
              <ChangeHistory entityType="Receipt" entityId={id} />
            </CardContent>
          </Card>

          <ReconcileSheet
            open={reconcileOpen}
            onClose={() => setReconcileOpen(false)}
            isSubmitting={createAdjustment.isPending}
            receiptId={id}
            receiptLabel={trip.receipt.receipt.location}
            receiptDate={trip.receipt.receipt.date}
            receiptTotal={expectedTotal}
            transactionsTotal={transactionsTotal}
            onCreateAdjustment={(adjustment) => {
              createAdjustment.mutate(
                { receiptId: id, body: adjustment },
                { onSuccess: () => setReconcileOpen(false) },
              );
            }}
          />

          <Dialog open={editOpen} onOpenChange={setEditOpen}>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>Edit receipt</DialogTitle>
              </DialogHeader>
              <ReceiptHeaderForm
                defaultValues={{
                  location: trip.receipt.receipt.location,
                  date: trip.receipt.receipt.date,
                  taxAmount: Number(trip.receipt.receipt.taxAmount ?? 0),
                }}
                isSubmitting={updateReceipt.isPending}
                serverErrors={serverErrors}
                onCancel={() => setEditOpen(false)}
                onSubmit={handleUpdate}
              />
            </DialogContent>
          </Dialog>
        </div>
      )}
    </>
  );
}

export default ReceiptDetail;
