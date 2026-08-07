import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import {
  useCreateReceiptItem,
  useUpdateReceiptItem,
  useDeleteReceiptItems,
} from "@/hooks/useReceiptItems";
import {
  ReceiptItemForm,
  type ReceiptItemFormValues,
} from "@/components/ReceiptItemForm";
import {
  parseProblemDetails,
  extractFieldErrors,
} from "@/lib/problem-details";
import { Button } from "@/components/ui/button";
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

interface ReceiptItem {
  id: string;
  receiptItemCode?: string | null;
  description: string;
  quantity: number;
  unitPrice: number;
  category: string;
  subcategory?: string | null;
  normalizedDescriptionName?: string | null;
}

interface ReceiptItemsCardProps {
  receiptId: string;
  items: ReceiptItem[];
  subtotal: number;
  location?: string | null;
}

export function ReceiptItemsCard({
  receiptId,
  items,
  subtotal,
  location,
}: ReceiptItemsCardProps) {
  const queryClient = useQueryClient();
  const createReceiptItem = useCreateReceiptItem();
  const updateReceiptItem = useUpdateReceiptItem();
  const deleteReceiptItems = useDeleteReceiptItems();

  const [createOpen, setCreateOpen] = useState(false);
  const [editItem, setEditItem] = useState<ReceiptItem | null>(null);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [selectedItems, setSelectedItems] = useState<Set<string>>(new Set());
  const [serverErrors, setServerErrors] = useState<Record<string, string>>({});

  function invalidateTrip() {
    queryClient.invalidateQueries({ queryKey: ["trips", receiptId] });
  }

  function handleCreate(values: ReceiptItemFormValues) {
    setServerErrors({});
    createReceiptItem.mutate(
      {
        receiptId,
        body: {
          receiptItemCode: values.receiptItemCode,
          description: values.description,
          quantity: values.quantity,
          unitPrice: values.unitPrice,
          category: values.category,
          subcategory: values.subcategory,
          // Present only when this line was entered from a template and the description still
          // matches it. The server uses it to stamp the item's canonical description and skip
          // the resolver (RECEIPTS-881).
          itemTemplateId: values.itemTemplateId,
        },
      },
      {
        onSuccess: () => {
          setCreateOpen(false);
          invalidateTrip();
        },
        onError: (error) => {
          const problem = parseProblemDetails(error);
          if (problem) setServerErrors(extractFieldErrors(problem));
        },
      },
    );
  }

  function handleUpdate(values: ReceiptItemFormValues) {
    if (!editItem) return;
    setServerErrors({});
    updateReceiptItem.mutate(
      {
        body: {
          id: editItem.id,
          receiptItemCode: values.receiptItemCode,
          description: values.description,
          quantity: values.quantity,
          unitPrice: values.unitPrice,
          category: values.category,
          subcategory: values.subcategory,
        },
      },
      {
        onSuccess: () => {
          setEditItem(null);
          invalidateTrip();
        },
        onError: (error) => {
          const problem = parseProblemDetails(error);
          if (problem) setServerErrors(extractFieldErrors(problem));
        },
      },
    );
  }

  function toggleSelect(id: string) {
    setSelectedItems((prev) => {
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
            <CardTitle>Items ({items.length})</CardTitle>
            <div className="flex gap-2">
              {selectedItems.size > 0 && (
                <Button
                  variant="destructive"
                  size="sm"
                  onClick={() => setDeleteOpen(true)}
                >
                  Delete ({selectedItems.size})
                </Button>
              )}
              <Button size="sm" onClick={() => { setServerErrors({}); setCreateOpen(true); }}>
                Add Item
              </Button>
            </div>
          </div>
        </CardHeader>
        <CardContent>
          {items.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No items for this receipt.
            </p>
          ) : (
            <div className="rounded-md border">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead className="w-12">
                      <Checkbox
                        aria-label="Select all items"
                        checked={
                          selectedItems.size === items.length &&
                          items.length > 0
                        }
                        onCheckedChange={() => {
                          if (selectedItems.size === items.length) {
                            setSelectedItems(new Set());
                          } else {
                            setSelectedItems(
                              new Set(items.map((item) => item.id)),
                            );
                          }
                        }}
                      />
                    </TableHead>
                    <TableHead>Code</TableHead>
                    <TableHead className="min-w-[16ch]">Description</TableHead>
                    <TableHead className="text-right">Qty</TableHead>
                    <TableHead className="text-right">Unit Price</TableHead>
                    <TableHead className="text-right">Total</TableHead>
                    <TableHead>Category</TableHead>
                    <TableHead>Subcategory</TableHead>
                    <TableHead className="w-24">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {items.map((item) => (
                    <TableRow key={item.id}>
                      <TableCell>
                        <Checkbox
                          aria-label={`Select ${item.description} item`}
                          checked={selectedItems.has(item.id)}
                          onCheckedChange={() => toggleSelect(item.id)}
                        />
                      </TableCell>
                      <TableCell className="font-mono">
                        {item.receiptItemCode ?? ""}
                      </TableCell>
                      <TableCell className="whitespace-normal break-words max-w-[32ch]">
                        <div>{item.description}</div>
                        {item.normalizedDescriptionName &&
                          item.normalizedDescriptionName !==
                            item.description && (
                            <div
                              className="text-xs text-muted-foreground"
                              data-testid={`normalized-as-${item.id}`}
                            >
                              normalized as {item.normalizedDescriptionName}
                            </div>
                          )}
                      </TableCell>
                      <TableCell className="text-right">
                        {item.quantity}
                      </TableCell>
                      <TableCell className="text-right">
                        {formatCurrency(item.unitPrice)}
                      </TableCell>
                      <TableCell className="text-right">
                        {formatCurrency(item.quantity * item.unitPrice)}
                      </TableCell>
                      <TableCell>{item.category}</TableCell>
                      <TableCell>{item.subcategory ?? ""}</TableCell>
                      <TableCell>
                        <Button
                          variant="ghost"
                          size="icon"
                          aria-label="Edit"
                          onClick={() => {
                            setServerErrors({});
                            setEditItem(item);
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
                    <TableCell colSpan={5} className="text-right font-medium">
                      Subtotal
                    </TableCell>
                    <TableCell className="text-right font-bold">
                      {formatCurrency(subtotal)}
                    </TableCell>
                    <TableCell colSpan={3} />
                  </TableRow>
                </TableFooter>
              </Table>
            </div>
          )}
        </CardContent>
      </Card>

      {/* Create Dialog */}
      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-lg">
          <DialogHeader>
            <DialogTitle>Add Item</DialogTitle>
          </DialogHeader>
          <ReceiptItemForm
            mode="create"
            defaultValues={{ receiptId }}
            hideReceiptField
            isSubmitting={createReceiptItem.isPending}
            serverErrors={serverErrors}
            onCancel={() => setCreateOpen(false)}
            onSubmit={handleCreate}
            location={location}
          />
        </DialogContent>
      </Dialog>

      {/* Edit Dialog */}
      <Dialog
        open={editItem !== null}
        onOpenChange={(open) => !open && setEditItem(null)}
      >
        <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-lg">
          <DialogHeader>
            <DialogTitle>Edit Item</DialogTitle>
          </DialogHeader>
          {editItem && (
            <ReceiptItemForm
              mode="edit"
              defaultValues={{
                receiptId,
                receiptItemCode: editItem.receiptItemCode ?? "",
                description: editItem.description,
                quantity: editItem.quantity,
                unitPrice: editItem.unitPrice,
                category: editItem.category,
                subcategory: editItem.subcategory ?? "",
              }}
              hideReceiptField
              isSubmitting={updateReceiptItem.isPending}
              serverErrors={serverErrors}
              onCancel={() => setEditItem(null)}
              onSubmit={handleUpdate}
              location={location}
            />
          )}
        </DialogContent>
      </Dialog>

      {/* Delete Confirmation Dialog */}
      <Dialog open={deleteOpen} onOpenChange={setDeleteOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete Items</DialogTitle>
          </DialogHeader>
          <p className="text-sm text-muted-foreground">
            Are you sure you want to delete {selectedItems.size} item(s)?
            This action can be undone by restoring.
          </p>
          <div className="flex justify-end gap-2 pt-4">
            <Button variant="outline" onClick={() => setDeleteOpen(false)}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              disabled={deleteReceiptItems.isPending}
              onClick={() => {
                const ids = [...selectedItems];
                setSelectedItems(new Set());
                setDeleteOpen(false);
                deleteReceiptItems.mutate(ids, {
                  onSuccess: () => invalidateTrip(),
                });
              }}
            >
              {deleteReceiptItems.isPending && <Spinner size="sm" />}
              {deleteReceiptItems.isPending ? "Deleting..." : "Delete"}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
