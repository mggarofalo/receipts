import { useRef, useState } from "react";
import {
  useCategoryMapping,
  type MappingSource,
  type MappingChange,
} from "@/hooks/useCategoryMapping";
import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import { usePermission } from "@/hooks/usePermission";
import {
  categoryPaths,
  type CategoryChoice,
  type CurrentClassification,
} from "@/lib/category-paths";
import {
  extractErrorMessage,
  parseProblemDetails,
} from "@/lib/problem-details";
import { generateId } from "@/lib/id";
import { CategoryPathPicker } from "./CategoryPathPicker";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Dialog,
  DialogContent,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";

/** Approval is an independent operation; it never saves or reclassifies a receipt. */
export function RememberCategory({
  source,
  classification,
  preferredCategoryId,
  confirmDifferentMapping = false,
  onSaved,
  dialog = false,
}: {
  source: MappingSource;
  classification?: CurrentClassification | null;
  preferredCategoryId?: string;
  confirmDifferentMapping?: boolean;
  onSaved?: () => void;
  dialog?: boolean;
}) {
  const { isAdmin } = usePermission();
  const { inspection, approvedTaxonomy, change } = useCategoryMapping(source);
  const taxonomy = useCategoryTaxonomy();
  const [choice, setChoice] = useState<CategoryChoice>();
  const [reason, setReason] = useState("");
  const [saved, setSaved] = useState(false);
  const [confirmedHeadId, setConfirmedHeadId] = useState<string>();
  const request = useRef<MappingChange | null>(null);
  const busy = useRef(false);
  const status = parseProblemDetails(change.error)?.status;
  const conflict = status === 409;
  const rejected =
    typeof status === "number" &&
    status >= 400 &&
    status < 500 &&
    status !== 408;
  const locked = change.isPending || change.isError;
  const currentRows = categoryPaths(taxonomy.data?.nodes ?? []);
  const preselectedCategoryId =
    classification?.categoryId || preferredCategoryId;
  // Only a recorded category identity can prefill the choice; legacy labels are not approval.
  const selected =
    choice ??
    (preselectedCategoryId && taxonomy.data
      ? {
          categoryId: preselectedCategoryId,
          taxonomyRevisionId: taxonomy.data.id,
          path:
            currentRows.find((row) => row.node.id === preselectedCategoryId)
              ?.path ?? [],
        }
      : undefined);
  const valid =
    !!selected?.categoryId &&
    selected.taxonomyRevisionId === taxonomy.data?.id &&
    currentRows.some((row) => row.node.id === selected.categoryId);
  const inspected = inspection.data;
  const head = inspected?.mapping;
  const differentMapping = !!(
    confirmDifferentMapping &&
    head?.category &&
    head.category.id !== selected?.categoryId
  );
  const alreadyRemembered = !!(
    confirmDifferentMapping &&
    head?.category &&
    head.category.id === preferredCategoryId
  );
  const approvedPath = categoryPaths(approvedTaxonomy.data?.nodes ?? []).find(
    (row) => row.node.id === head?.category?.id,
  )?.label;

  function submit(revoke: boolean) {
    if (
      !isAdmin() ||
      busy.current ||
      conflict ||
      alreadyRemembered ||
      (differentMapping && confirmedHeadId !== head?.id)
    )
      return;
    if (!request.current) {
      if (
        !inspected ||
        !taxonomy.data ||
        inspected.taxonomyRevisionId !== taxonomy.data.id ||
        (!revoke && !valid)
      )
        return;
      request.current = {
        source: inspected.source,
        sourceFingerprint: inspected.sourceFingerprint,
        expectedMappingRevisionId: head?.id ?? null,
        expectedTaxonomyRevisionId: taxonomy.data.id,
        category: revoke ? null : { id: selected!.categoryId! },
        operationId: generateId(),
        reason:
          reason.trim() ||
          (revoke
            ? "Remembered category explicitly revoked"
            : head?.category
              ? "Remembered category explicitly changed"
              : "Category explicitly remembered from saved receipt item"),
      };
    }
    busy.current = true;
    setSaved(false);
    change.mutate(request.current, {
      onSuccess: () => {
        request.current = null;
        setSaved(true);
        setReason("");
        onSaved?.();
      },
      onSettled: () => {
        busy.current = false;
      },
    });
  }

  async function refresh() {
    if (busy.current) return;
    request.current = null;
    change.reset();
    setSaved(false);
    setConfirmedHeadId(undefined);
    await Promise.all([inspection.refetch(), taxonomy.refetch()]);
  }

  const content = (
    <div className="space-y-3 rounded-md border p-3 text-sm">
      <p>
        Use this category for future exact matches. Existing receipts stay
        unchanged.
      </p>
      <dl className="break-words">
        <dt className="font-medium">Store</dt>
        <dd className="whitespace-pre-wrap">{source.location}</dd>
        <dt className="font-medium">Raw description</dt>
        <dd className="whitespace-pre-wrap">{source.description}</dd>
        <dt className="font-medium">Item code</dt>
        <dd className="whitespace-pre-wrap">{source.itemCode || "No code"}</dd>
      </dl>
      {inspection.isPending && (
        <p role="status">Loading remembered category…</p>
      )}
      {inspection.isError && (
        <p role="alert">
          {extractErrorMessage(inspection.error) ??
            "Remembered category unavailable."}
        </p>
      )}
      {inspected && (
        <div>
          <p>
            Remembered category:{" "}
            {head?.category ? (approvedPath ?? head.category.id) : "None"}
          </p>
          <p>Status: {inspected.disposition}</p>
          {approvedTaxonomy.isError && (
            <p role="alert">The recorded category path is unavailable.</p>
          )}
        </div>
      )}
      {alreadyRemembered && (
        <p role="status">
          This exact source is already remembered with the saved category. No
          mapping change is needed.
        </p>
      )}
      <details className="text-muted-foreground">
        <summary className="cursor-pointer">
          Matching details and history
        </summary>
        <p>
          Matches use the exact store, raw description and item code. Only
          surrounding spaces are ignored; case and other whitespace remain
          distinct.
        </p>
        {head && (
          <p>
            {head.category ? "Approved" : "Revoked"} by {head.actor} at{" "}
            {head.recordedAt}. Reason: {head.reason}
          </p>
        )}
      </details>
      {isAdmin() ? (
        <>
          {differentMapping && !alreadyRemembered && (
            <label className="flex items-start gap-2">
              <input
                type="checkbox"
                checked={confirmedHeadId === head?.id}
                onChange={(event) =>
                  setConfirmedHeadId(
                    event.target.checked ? head?.id : undefined,
                  )
                }
              />
              Replace the current remembered category for this exact source
              after reviewing it
            </label>
          )}
          <CategoryPathPicker
            aria-label="Category for future matches"
            revision={taxonomy.data}
            loading={taxonomy.isLoading}
            error={taxonomy.isError}
            value={selected}
            disabled={locked}
            onValueChange={(value) => {
              setChoice(value);
              setSaved(false);
            }}
          />
          <Input
            aria-label="Reason for remembered category change"
            placeholder="Optional note"
            value={reason}
            disabled={locked}
            onChange={(event) => setReason(event.target.value)}
          />
          {taxonomy.isError && <p role="alert">Categories unavailable.</p>}
          {inspected &&
            taxonomy.data &&
            inspected.taxonomyRevisionId !== taxonomy.data.id && (
              <p role="alert">
                The category list changed. Refresh before approving.
              </p>
            )}
          {!change.isError && (
            <div className="flex flex-wrap gap-2">
              <Button
                type="button"
                size="sm"
                disabled={
                  locked ||
                  !valid ||
                  alreadyRemembered ||
                  (differentMapping && confirmedHeadId !== head?.id) ||
                  !inspected ||
                  inspection.isFetching ||
                  taxonomy.isFetching ||
                  inspected?.taxonomyRevisionId !== taxonomy.data?.id
                }
                onClick={() => submit(false)}
              >
                {head?.category
                  ? "Change remembered category"
                  : "Remember for future matches"}
              </Button>
              {head?.category && (
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  disabled={
                    locked ||
                    inspection.isFetching ||
                    taxonomy.isFetching ||
                    inspected?.taxonomyRevisionId !== taxonomy.data?.id
                  }
                  onClick={() => submit(true)}
                >
                  Revoke remembered category
                </Button>
              )}
            </div>
          )}
          {change.isPending && <p role="status">Saving remembered category…</p>}
          {change.isError && (
            <div role="alert">
              <p>
                {extractErrorMessage(change.error) ??
                  "Could not confirm the change."}
              </p>
              {rejected ? (
                <p>
                  {conflict
                    ? "The mapping or categories changed. Refresh and review before approving again."
                    : "Refresh to review the current values and correct this change."}
                </p>
              ) : (
                <Button type="button" size="sm" onClick={() => submit(false)}>
                  Retry same change
                </Button>
              )}
            </div>
          )}
          {saved && (
            <p role="status">
              Remembered category updated. Existing receipts keep their
              categories.
            </p>
          )}
        </>
      ) : (
        <p>
          An active Admin can approve, change or revoke remembered categories.
        </p>
      )}
      <Button
        type="button"
        size="sm"
        variant="outline"
        disabled={
          change.isPending ||
          inspection.isFetching ||
          taxonomy.isFetching ||
          (change.isError && !rejected)
        }
        onClick={() => void refresh()}
      >
        Refresh remembered category
      </Button>
    </div>
  );
  return dialog ? (
    <DialogContent
      className="max-h-[90dvh] overflow-y-auto"
      aria-describedby={undefined}
    >
      <DialogTitle>Remember category</DialogTitle>
      {content}
    </DialogContent>
  ) : (
    content
  );
}

export function RememberCategoryControl(
  props: Parameters<typeof RememberCategory>[0],
) {
  const [opened, setOpened] = useState(false);
  const [loaded, setLoaded] = useState(false);
  return (
    <Dialog
      open={opened}
      onOpenChange={(open) => {
        setLoaded(true);
        setOpened(open);
      }}
    >
      <DialogTrigger asChild>
        <Button type="button" variant="link" size="sm">
          Remember category…
        </Button>
      </DialogTrigger>
      {loaded && <RememberCategory {...props} dialog />}
    </Dialog>
  );
}
