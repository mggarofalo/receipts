import { useId, useState } from "react";
import {
  useCatalogActions,
  type CatalogOperation,
} from "@/hooks/useNormalizationCatalog";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { extractErrorMessage } from "@/lib/problem-details";

export function CatalogOperationReview({
  operation,
}: {
  operation: CatalogOperation;
}) {
  const { operate } = useCatalogActions();
  const controlId = useId();
  const [confirmedRevision, setConfirmedRevision] = useState<string | null>(
    null,
  );
  const [undoRevision, setUndoRevision] = useState<string | null>(null);
  const confirmed = confirmedRevision === operation.revision;
  const confirmUndo = undoRevision === operation.revision;
  const active = ["applying", "undoing"].includes(operation.state);
  const paused = ["paused", "undo_paused"].includes(operation.state);
  return (
    <section
      className="space-y-3 rounded-lg border p-4"
      aria-label="Historical normalization operation"
    >
      <h2 className="card-title">{operation.source.description}</h2>
      <p>
        {operation.source.location} · Item code:{" "}
        {operation.source.itemCode || "not recorded"}
      </p>
      <p>
        Decision:{" "}
        {operation.exclude
          ? "Exclude from normalization"
          : (operation.targetName ?? operation.newCanonicalName)}
      </p>
      <p role="status">Status: {operation.state}</p>
      <dl className="grid grid-cols-2 gap-2 sm:grid-cols-4">
        {[
          ["Matching purchases", operation.total],
          ["Eligible", operation.eligible],
          ["Protected", operation.protected],
          ["Applied", operation.applied],
          ["Changed since preview", operation.conflicted],
          ["Remaining", operation.remaining],
          ["Undone", operation.undone],
        ].map(([label, value]) => (
          <div key={label}>
            <dt>{label}</dt>
            <dd className="text-xl">{value}</dd>
          </div>
        ))}
      </dl>
      <p className="text-sm">
        {operation.includeLegacyUnknown
          ? "Unknown legacy assignments are included."
          : "Unknown legacy assignments are preserved."}{" "}
        Manual and template assignments are protected. Original descriptions,
        prices, quantities, and receipt totals stay unchanged.
      </p>
      {operation.state === "preview" && (
        <>
          <p>
            Approval saves this exact source mapping for future purchases and
            applies it to the eligible history captured by this preview. Changed
            purchases are skipped.
          </p>
          <label
            className="flex items-center gap-2"
            htmlFor={`${controlId}-approve`}
          >
            <Checkbox
              id={`${controlId}-approve`}
              checked={confirmed}
              disabled={operate.isPending}
              onCheckedChange={(value) =>
                setConfirmedRevision(value === true ? operation.revision : null)
              }
            />
            I reviewed the source, target, and historical scope
          </label>
          <Button
            disabled={!confirmed || operate.isPending}
            onClick={() => operate.mutate({ operation, action: "approve" })}
          >
            Approve mapping and apply
          </Button>
        </>
      )}
      {active && (
        <p>
          Processing in the background. You can leave and return to this
          operation.
        </p>
      )}
      {operation.state === "restored" && (
        <p>
          This operation was restored from a backup as history. It cannot resume
          or undo against regenerated purchase revisions. Review the source to
          make a fresh, explicit correction.
        </p>
      )}
      {operation.errorDetail && <p role="alert">{operation.errorDetail}</p>}
      {paused && (
        <Button
          disabled={operate.isPending}
          onClick={() => operate.mutate({ operation, action: "resume" })}
        >
          Resume operation
        </Button>
      )}
      {["preview", "applying", "paused", "undoing", "undo_paused"].includes(
        operation.state,
      ) && (
        <Button
          variant="outline"
          disabled={operate.isPending}
          onClick={() => operate.mutate({ operation, action: "cancel" })}
        >
          Stop operation and future reuse
        </Button>
      )}
      {operation.mappingId &&
        !["undoing", "undo_paused", "undone", "restored"].includes(
          operation.state,
        ) && (
          <div className="space-y-2">
            <p>
              Undo revokes this mapping and restores only assignments still
              owned by this operation. Later manual edits are preserved.
            </p>
            <label
              className="flex items-center gap-2"
              htmlFor={`${controlId}-undo`}
            >
              <Checkbox
                id={`${controlId}-undo`}
                checked={confirmUndo}
                disabled={operate.isPending}
                onCheckedChange={(value) =>
                  setUndoRevision(value === true ? operation.revision : null)
                }
              />
              Revoke this mapping and undo eligible changes
            </label>
            <Button
              variant="destructive"
              disabled={!confirmUndo || operate.isPending}
              onClick={() => operate.mutate({ operation, action: "undo" })}
            >
              Undo operation
            </Button>
          </div>
        )}
      {operate.error && (
        <p role="alert" className="text-destructive">
          {extractErrorMessage(operate.error)} Refresh the operation before
          retrying a stale decision.
        </p>
      )}
    </section>
  );
}
