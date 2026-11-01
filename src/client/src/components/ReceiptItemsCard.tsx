import { RememberCategoryControl } from "@/components/categories/RememberCategory";
import { type CurrentClassification } from "@/lib/category-paths";
import { calculateLineTotal } from "@/lib/receipt-arithmetic";
import {
  normalizationSourceKey,
  receiptNormalizationSource,
} from "@/lib/receipt-normalization";
import { useMemo, useState, useRef } from "react";
import { ReceiptNormalizationError } from "@/components/ReceiptNormalizationError";
import { useQueryClient } from "@tanstack/react-query";
import { invalidateDomainChange } from "@/lib/query-invalidation";
import {
  useCreateReceiptItem,
  useUpdateReceiptItem,
  useDeleteReceiptItems,
} from "@/hooks/useReceiptItems";
import {
  ReceiptItemForm,
  type ReceiptItemFormValues,
} from "@/components/ReceiptItemForm";
import { parseProblemDetails, extractFieldErrors } from "@/lib/problem-details";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
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
import { formatUnitPrice, formatCurrency } from "@/lib/format";
import { Pencil } from "lucide-react";
import {
  EnvelopeBadge,
  EnvelopeOverrideMenu,
  OccasionChip,
  PaidFromSelect,
} from "@/components/envelopes/BudgetChoices";
import {
  useEnvelopes,
  useItemTypeEnvelopeDefaults,
  useOccasions,
} from "@/hooks/useEnvelopes";
import {
  useSetReceiptBudgetChoices,
  useSetReceiptItemBudgetChoices,
} from "@/hooks/useBudgetChoices";
import { previewItemEnvelope, receiptEnvelope } from "@/lib/envelope-display";
import type { components } from "@/generated/api";

interface ReceiptItem {
  id: string;
  receiptItemCode?: string | null;
  description: string;
  quantity: number;
  unitPrice: number;
  currentClassification?: CurrentClassification | null;
  normalizedDescriptionName?: string | null;
  normalizationRevision?: string;
  normalizationReviewState?: components["schemas"]["NormalizationReviewState"];
  envelopeId?: string | null;
  envelopeSource?: components["schemas"]["EnvelopeSource"];
  occasionId?: string | null;
}

interface ReceiptItemsCardProps {
  receiptId: string;
  items: ReceiptItem[];
  subtotal: number;
  location?: string | null;
  paidFromEnvelopeId?: string | null;
  occasionId?: string | null;
}

function categoryLabel(item: ReceiptItem): string {
  const classification = item.currentClassification;
  if (classification) {
    if (classification.categoryId === null) return "Unclassified";
    if (!classification.recordedPath.length) return "Unresolved category path";
    const path = classification.recordedPath
      .map((node) => node.name)
      .join(" / ");
    return classification.recordedPath.some((node) => node.archived)
      ? `${path} (archived in recorded revision)`
      : path;
  }
  return "Unresolved category";
}

export function ReceiptItemsCard({
  receiptId,
  items,
  subtotal,
  location,
  paidFromEnvelopeId = null,
  occasionId = null,
}: ReceiptItemsCardProps) {
  const createReceiptItem = useCreateReceiptItem();
  const { envelopes } = useEnvelopes();
  const { occasions } = useOccasions();
  const { defaults } = useItemTypeEnvelopeDefaults();
  const setReceiptChoices = useSetReceiptBudgetChoices();
  const setItemChoices = useSetReceiptItemBudgetChoices();
  const choicesBusy = setReceiptChoices.isPending || setItemChoices.isPending;
  const paidFrom = useMemo(
    () =>
      receiptEnvelope(
        paidFromEnvelopeId,
        // An override counts as the envelope the item would get automatically.
        items.map((item) =>
          item.envelopeSource === "item"
            ? previewItemEnvelope(
                { typePath: item.currentClassification?.recordedPath },
                null,
                defaults,
                envelopes,
              )
            : item.envelopeId,
        ),
      ),
    [items, paidFromEnvelopeId, defaults, envelopes],
  );
  const updateReceiptItem = useUpdateReceiptItem();
  const deleteReceiptItems = useDeleteReceiptItems();

  const [createOpen, setCreateOpen] = useState(false);
  const [editItem, setEditItem] = useState<ReceiptItem | null>(null);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [selectedItems, setSelectedItems] = useState<Set<string>>(new Set());
  const [serverErrors, setServerErrors] = useState<Record<string, string>>({});
  const [saveError, setSaveError] = useState<unknown>();
  const dialogSession = useRef(0);
  const queryClient = useQueryClient();
  const currentEditItem = items.find((item) => item.id === editItem?.id);

  function closeEditor() {
    dialogSession.current++;
    setCreateOpen(false);
    setEditItem(null);
    setSaveError(undefined);
  }

  function handleCreate(values: ReceiptItemFormValues) {
    setServerErrors({});
    setSaveError(undefined);
    const session = dialogSession.current;
    createReceiptItem.mutate(
      {
        receiptId,
        body: {
          receiptItemCode: values.receiptItemCode,
          description: values.description,
          quantity: values.quantity,
          unitPrice: values.unitPrice,
          // Present only when this line was entered from a template and the description still
          // matches it. The server uses it to stamp the item's canonical description and skip
          // the resolver (RECEIPTS-881).
          itemTemplateId: values.itemTemplateId,
          normalizationIntent: values.normalizationIntent,
          classification: values.classification,
        },
      },
      {
        onSuccess: () => {
          if (session === dialogSession.current) closeEditor();
        },
        onError: (error) => {
          if (session !== dialogSession.current) return;
          setSaveError(error);
          const problem = parseProblemDetails(error);
          if (problem) setServerErrors(extractFieldErrors(problem));
        },
      },
    );
  }

  function handleUpdate(values: ReceiptItemFormValues) {
    if (!editItem) return;
    setServerErrors({});
    setSaveError(undefined);
    const session = dialogSession.current;
    updateReceiptItem.mutate(
      {
        body: {
          id: editItem.id,
          receiptItemCode: values.receiptItemCode,
          description: values.description,
          quantity: values.quantity,
          unitPrice: values.unitPrice,
          normalizationIntent: values.normalizationIntent,
          classification: values.classification,
        },
      },
      {
        onSuccess: () => {
          if (session === dialogSession.current) closeEditor();
        },
        onError: (error) => {
          if (session !== dialogSession.current) return;
          setSaveError(error);
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
            <div className="flex flex-wrap items-center gap-2">
              <CardTitle>Items ({items.length})</CardTitle>
              <PaidFromSelect
                envelopes={envelopes}
                value={paidFromEnvelopeId}
                disabled={choicesBusy}
                onChange={(next) =>
                  setReceiptChoices.mutate({
                    receiptId,
                    paidFromEnvelopeId: next,
                    occasionId,
                  })
                }
              />
              <OccasionChip
                occasions={occasions}
                value={occasionId}
                disabled={choicesBusy}
                onChange={(next) =>
                  setReceiptChoices.mutate({
                    receiptId,
                    paidFromEnvelopeId,
                    occasionId: next,
                  })
                }
              />
            </div>
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
              <Button
                size="sm"
                onClick={() => {
                  dialogSession.current++;
                  setSaveError(undefined);
                  setServerErrors({});
                  setCreateOpen(true);
                }}
              >
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
                        <div className="text-xs text-muted-foreground">
                          {item.normalizationReviewState === "verified"
                            ? "Verified"
                            : item.normalizationReviewState === "excluded"
                              ? "Excluded"
                              : null}
                        </div>
                        {item.normalizedDescriptionName &&
                          item.normalizedDescriptionName !==
                            item.description && (
                            <div
                              className="text-xs text-muted-foreground"
                              data-testid={`normalized-as-${item.id}`}
                            >
                              {item.normalizationReviewState === "verified"
                                ? "normalized as"
                                : "Unreviewed association:"}{" "}
                              {item.normalizedDescriptionName}
                            </div>
                          )}
                      </TableCell>
                      <TableCell className="text-right">
                        {item.quantity}
                      </TableCell>
                      <TableCell className="text-right">
                        {formatUnitPrice(item.unitPrice)}
                      </TableCell>
                      <TableCell className="text-right">
                        {formatCurrency(
                          calculateLineTotal(item.quantity, item.unitPrice),
                        )}
                      </TableCell>
                      <TableCell>
                        {categoryLabel(item)}{" "}
                        {location && (
                          <RememberCategoryControl
                            key={normalizationSourceKey(
                              receiptNormalizationSource(location, item),
                            )}
                            source={receiptNormalizationSource(location, item)}
                            classification={item.currentClassification}
                          />
                        )}
                        {item.envelopeSource && (
                          <EnvelopeBadge
                            envelopes={envelopes}
                            envelopeId={item.envelopeId ?? null}
                            receiptEnvelopeId={paidFrom}
                          />
                        )}
                      </TableCell>
                      <TableCell>
                        <Button
                          variant="ghost"
                          size="icon"
                          aria-label="Edit"
                          onClick={() => {
                            dialogSession.current++;
                            setSaveError(undefined);
                            setServerErrors({});
                            setEditItem(item);
                          }}
                        >
                          <Pencil className="h-4 w-4" />
                        </Button>
                        <EnvelopeOverrideMenu
                          envelopes={envelopes}
                          value={
                            item.envelopeSource === "item"
                              ? (item.envelopeId ?? null)
                              : null
                          }
                          itemLabel={item.description}
                          disabled={choicesBusy}
                          onChange={(envelopeId) =>
                            setItemChoices.mutate({
                              itemId: item.id,
                              envelopeId,
                              occasionId: item.occasionId ?? null,
                            })
                          }
                        />
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
                    <TableCell colSpan={2} />
                  </TableRow>
                </TableFooter>
              </Table>
            </div>
          )}
        </CardContent>
      </Card>

      {/* Create Dialog */}
      <Dialog
        open={createOpen}
        onOpenChange={(open) => {
          if (!open) closeEditor();
        }}
      >
        <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-lg">
          <DialogHeader>
            <DialogTitle>Add Item</DialogTitle>
          </DialogHeader>
          <ReceiptNormalizationError error={saveError} />
          {createOpen && (
            <ReceiptItemForm
              mode="create"
              defaultValues={{ receiptId }}
              hideReceiptField
              isSubmitting={createReceiptItem.isPending}
              serverErrors={serverErrors}
              onCancel={closeEditor}
              onSubmit={handleCreate}
              location={location}
            />
          )}
        </DialogContent>
      </Dialog>

      {/* Edit Dialog */}
      <Dialog
        open={editItem !== null}
        onOpenChange={(open) => {
          if (!open) closeEditor();
        }}
      >
        <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-lg">
          <DialogHeader>
            <DialogTitle>Edit Item</DialogTitle>
          </DialogHeader>
          <ReceiptNormalizationError error={saveError} />
          {saveError != null && (
            <Button
              type="button"
              variant="outline"
              onClick={() =>
                invalidateDomainChange(queryClient, "receipt-item")
              }
            >
              Refresh saved receipt information
            </Button>
          )}
          {editItem && (
            <ReceiptItemForm
              key={editItem.id}
              mode="edit"
              currentClassification={editItem.currentClassification}
              defaultValues={{
                receiptId,
                receiptItemCode: editItem.receiptItemCode ?? "",
                description: editItem.description,
                quantity: editItem.quantity,
                unitPrice: editItem.unitPrice,
              }}
              hideReceiptField
              isSubmitting={updateReceiptItem.isPending}
              serverErrors={serverErrors}
              onCancel={closeEditor}
              onSubmit={handleUpdate}
              location={location}
              normalizationRevision={currentEditItem?.normalizationRevision}
              normalizationReviewState={
                currentEditItem?.normalizationReviewState
              }
              normalizedDescriptionName={
                currentEditItem?.normalizedDescriptionName
              }
              normalizationSourceKey={
                currentEditItem
                  ? normalizationSourceKey(
                      receiptNormalizationSource(location, currentEditItem),
                    )
                  : undefined
              }
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
            Are you sure you want to delete {selectedItems.size} item(s)? This
            action can be undone by restoring.
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
                deleteReceiptItems.mutate(ids);
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
