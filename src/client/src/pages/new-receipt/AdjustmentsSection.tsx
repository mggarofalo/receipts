import { useMemo, useState } from "react";
import { generateId } from "@/lib/id";
import { formatCurrency } from "@/lib/format";
import { useEnumMetadata } from "@/hooks/useEnumMetadata";
import {
  AdjustmentForm,
  type AdjustmentFormValues,
} from "@/components/AdjustmentForm";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Pencil, Plus, Trash2 } from "lucide-react";

export interface ReceiptAdjustment {
  id: string;
  type: string;
  amount: number;
  description?: string;
}

interface AdjustmentsSectionProps {
  adjustments: ReceiptAdjustment[];
  onChange: (adjustments: ReceiptAdjustment[]) => void;
}

export function AdjustmentsSection({
  adjustments,
  onChange,
}: AdjustmentsSectionProps) {
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editing, setEditing] = useState<ReceiptAdjustment | null>(null);
  const { adjustmentTypeLabels } = useEnumMetadata();

  const total = useMemo(
    () => adjustments.reduce((sum, adjustment) => sum + adjustment.amount, 0),
    [adjustments],
  );

  function openCreate() {
    setEditing(null);
    setDialogOpen(true);
  }

  function openEdit(adjustment: ReceiptAdjustment) {
    setEditing(adjustment);
    setDialogOpen(true);
  }

  function handleSubmit(values: AdjustmentFormValues) {
    if (editing) {
      onChange(
        adjustments.map((adjustment) =>
          adjustment.id === editing.id ? { ...adjustment, ...values } : adjustment,
        ),
      );
    } else {
      onChange([...adjustments, { id: generateId(), ...values }]);
    }
    setDialogOpen(false);
  }

  function handleRemove(id: string) {
    onChange(adjustments.filter((adjustment) => adjustment.id !== id));
  }

  return (
    <Card>
      <CardHeader className="pb-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="space-y-1">
            <CardTitle className="text-lg">Adjustments</CardTitle>
            <p className="text-sm text-muted-foreground">
              Enter additions such as tips as positive amounts and reductions
              such as coupons or credits as negative amounts.
            </p>
          </div>
          <div className="flex items-center gap-3">
            <span className="text-sm text-muted-foreground">
              Total: {formatCurrency(total)}
            </span>
            <Button type="button" variant="secondary" size="sm" onClick={openCreate}>
              <Plus className="mr-1 h-4 w-4" />
              Add adjustment
            </Button>
          </div>
        </div>
      </CardHeader>
      {adjustments.length > 0 && (
        <CardContent>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Type</TableHead>
                <TableHead>Description</TableHead>
                <TableHead className="text-right">Amount</TableHead>
                <TableHead className="w-24">
                  <span className="sr-only">Actions</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {adjustments.map((adjustment) => (
                <TableRow key={adjustment.id}>
                  <TableCell>
                    {adjustmentTypeLabels[adjustment.type] ?? adjustment.type}
                  </TableCell>
                  <TableCell>{adjustment.description || "—"}</TableCell>
                  <TableCell className="text-right">
                    {formatCurrency(adjustment.amount)}
                  </TableCell>
                  <TableCell>
                    <div className="flex justify-end">
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon"
                        aria-label="Edit adjustment"
                        onClick={() => openEdit(adjustment)}
                      >
                        <Pencil className="h-4 w-4" />
                      </Button>
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon"
                        aria-label="Remove adjustment"
                        onClick={() => handleRemove(adjustment.id)}
                      >
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      )}

      <Dialog open={dialogOpen} onOpenChange={setDialogOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>
              {editing ? "Edit adjustment" : "Add adjustment"}
            </DialogTitle>
            <DialogDescription>
              Adjustments change the expected receipt total before it is saved.
            </DialogDescription>
          </DialogHeader>
          <AdjustmentForm
            mode={editing ? "edit" : "create"}
            defaultValues={editing ?? undefined}
            onSubmit={handleSubmit}
            onCancel={() => setDialogOpen(false)}
          />
        </DialogContent>
      </Dialog>
    </Card>
  );
}
