import { useEffect, useId, useState } from "react";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { useReceiptNormalizationPreview } from "@/hooks/useReceiptNormalizationPreview";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Checkbox } from "@/components/ui/checkbox";
import { RequestFailure } from "@/components/RequestFailure";
import { ReceiptNormalizationShortlist } from "@/components/ReceiptNormalizationShortlist";
import {
  currentNormalizationChoice,
  normalizationSourceKey,
  type ReceiptNormalizationChoice,
  type ReceiptNormalizationIntent,
  type ReceiptNormalizationSource,
} from "@/lib/receipt-normalization";

export interface ReceiptNormalizationFieldProps {
  source: ReceiptNormalizationSource;
  clientRowId: string;
  value?: ReceiptNormalizationChoice;
  onChange: (choice: ReceiptNormalizationChoice | undefined) => void;
  expectedItemRevision?: string;
  currentAssignment?: {
    sourceKey: string;
    state: "verified" | "excluded" | "needsReview";
    name?: string | null;
  };
  disabled?: boolean;
}

export function ReceiptNormalizationField(
  props: ReceiptNormalizationFieldProps,
) {
  const sourceKey = normalizationSourceKey(props.source);
  const settledKey = useDebouncedValue(sourceKey);
  const { value, onChange } = props;
  useEffect(() => {
    if (value && value.sourceKey !== sourceKey) onChange(undefined);
  }, [value, sourceKey, onChange]);
  if (!props.source.location.trim() || !props.source.description.trim()) {
    return (
      <p className="text-sm text-muted-foreground">
        Enter a store and description to recognize this purchase. You can save
        it for review.
      </p>
    );
  }
  if (sourceKey !== settledKey) {
    return (
      <p className="text-sm text-muted-foreground">
        Checking this description… You can still save for review.
      </p>
    );
  }
  return (
    <NormalizationChoices
      key={`${props.clientRowId}:${sourceKey}`}
      {...props}
    />
  );
}

function NormalizationChoices({
  source,
  clientRowId,
  value,
  onChange,
  expectedItemRevision,
  currentAssignment,
  disabled,
}: ReceiptNormalizationFieldProps) {
  const [expanded, setExpanded] = useState(false);
  const [query, setQuery] = useState("");
  const [offset, setOffset] = useState(0);
  const [newName, setNewName] = useState("");
  const [remember, setRemember] = useState(false);
  const preview = useReceiptNormalizationPreview(source, query, offset);
  const choice = currentNormalizationChoice(value, source);
  const sourceKey = normalizationSourceKey(source);
  const existing =
    currentAssignment?.sourceKey === sourceKey ? currentAssignment : undefined;
  const id = useId();
  const ready =
    preview.isSuccess && !preview.isFetching && !preview.isDebouncing;
  const evidence = ready ? preview.data : undefined;
  const sourceAmbiguous = preview.data?.sourceAmbiguous === true;
  if (sourceAmbiguous && remember) setRemember(false);

  // Record the exact observed mapping once. Refetches never advance a choice's
  // revision: the server must reject a revoked/changed mapping on submission.
  useEffect(() => {
    if (
      disabled ||
      choice ||
      existing?.state === "verified" ||
      existing?.state === "excluded" ||
      evidence?.sourceAmbiguous ||
      !evidence?.mappingId ||
      !evidence.mappingRevision
    )
      return;
    onChange({
      sourceKey,
      label: evidence.excluded
        ? "Excluded by your remembered rule"
        : (evidence.targetName ?? "Recognized product"),
      intent: {
        kind: "knownSource",
        clientRowId,
        sourceFingerprint: evidence.sourceFingerprint,
        expectedMappingRevision: evidence.mappingRevision,
        expectedItemRevision,
        rememberSource: false,
      },
    });
  }, [
    disabled,
    choice,
    evidence,
    onChange,
    sourceKey,
    clientRowId,
    expectedItemRevision,
    existing?.state,
  ]);

  function choose(
    intent: Omit<
      ReceiptNormalizationIntent,
      "clientRowId" | "sourceFingerprint"
    >,
    label: string,
  ) {
    if (!evidence) return;
    onChange({
      sourceKey,
      label,
      intent: {
        ...intent,
        rememberSource: intent.rememberSource && !sourceAmbiguous,
        clientRowId,
        sourceFingerprint: evidence.sourceFingerprint,
        expectedItemRevision,
      },
    });
    setExpanded(false);
  }

  return (
    <fieldset
      disabled={disabled}
      className="min-w-0 space-y-2 rounded-md border p-3 [overflow-wrap:anywhere]"
      aria-label="Product normalization"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm" aria-live="polite">
          {choice ? (
            choice.intent.kind === "defer" ? (
              "Needs review — saved with the receipt"
            ) : (
              <>
                {" "}
                {choice.intent.kind === "knownSource"
                  ? "Recognized"
                  : "Selected for this purchase"}
                : <strong>{choice.label}</strong>
              </>
            )
          ) : existing?.state === "verified" ? (
            <>
              Reviewed purchase:{" "}
              <strong>{existing.name ?? "Product confirmed"}</strong>
            </>
          ) : existing?.state === "excluded" ? (
            "Excluded from product grouping"
          ) : preview.isFetching || preview.isDebouncing ? (
            "Checking known descriptions…"
          ) : (
            "Needs review"
          )}
        </p>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => {
            if (expanded) {
              setNewName("");
              setQuery("");
              setOffset(0);
              setRemember(false);
            }
            setExpanded(!expanded);
          }}
        >
          {expanded
            ? "Close choices"
            : choice && choice.intent.kind !== "defer"
              ? "Change product"
              : "Choose product"}
        </Button>
      </div>
      {choice && choice.intent.kind !== "defer" && (
        <p className="text-xs text-muted-foreground">
          {choice.intent.kind === "knownSource"
            ? "Using your remembered rule for this exact store, description and item code."
            : choice.intent.rememberSource
              ? "Will remember this exact store, description and item code when you save."
              : "This purchase only."}{" "}
          Final checks happen when you save.
        </p>
      )}
      {preview.isError && (
        <RequestFailure
          message="Product lookup is unavailable. Your receipt can still be saved for review."
          retry={() => void preview.refetch()}
          isRetrying={preview.isFetching}
        />
      )}
      {expanded && (
        <div className="space-y-3">
          <p className="text-xs text-muted-foreground">
            Match the same product, including known brand, size and variety.
            Choosing a name does not change receipt text or prices.
          </p>
          <ReceiptNormalizationShortlist
            source={source}
            disabled={disabled || !ready}
            onChoose={(candidate) => choose({
              kind: "confirmExisting",
              targetId: candidate.id,
              expectedTargetRevision: candidate.revision,
              rememberSource: remember,
            }, candidate.displayLabel ?? candidate.canonicalName)}
          />
          <label htmlFor={`${id}-search`} className="block text-sm">
            Search all product names
          </label>
          <Input
            id={`${id}-search`}
            value={query}
            maxLength={200}
            onKeyDown={(event) => {
              if (event.key === "Enter") event.preventDefault();
            }}
            onChange={(event) => {
              setQuery(event.target.value);
              setOffset(0);
            }}
          />
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={preview.isFetching || preview.isDebouncing}
            onClick={() => {
              onChange(undefined);
              void preview.refetch();
            }}
          >
            Refresh product choices
          </Button>
          {evidence?.mappingId && (
            <p className="text-sm">
              A remembered rule already exists. Choosing this purchase only
              leaves that rule unchanged.{" "}
              <a
                className="underline"
                target="_blank"
                rel="noopener noreferrer"
                href={`/admin/normalized-descriptions?${new URLSearchParams({ tab: "catalog", catalogSource: JSON.stringify(source) })}`}
              >
                Review the rule and its historical scope in a new tab
              </a>
              .
            </p>
          )}
          <label className="flex items-start gap-2 text-sm">
            <Checkbox
              checked={remember && !sourceAmbiguous}
              disabled={sourceAmbiguous}
              onCheckedChange={(checked) => setRemember(checked === true)}
            />
            <span>
              Remember this exact description at {source.location}
              {source.itemCode
                ? ` (code ${source.itemCode})`
                : " (without an item code)"}
              . Leave unchecked for this purchase only.
            </span>
          </label>
          {sourceAmbiguous && (
            <p className="text-sm">
              This description has been used for different products. Choose for
              this purchase only; it cannot safely become a remembered rule.
            </p>
          )}
          {preview.isFetching || preview.isDebouncing ? (
            <p role="status">Loading product choices…</p>
          ) : (
            evidence && (
              <>
                <ul className="space-y-1" aria-label="Product choices">
                  {evidence.candidates.map((candidate) => (
                    <li key={candidate.id}>
                      <Button
                        className="h-auto max-w-full whitespace-normal break-words text-left"
                        type="button"
                        variant="outline"
                        onClick={() =>
                          choose(
                            {
                              kind: "confirmExisting",
                              targetId: candidate.id,
                              expectedTargetRevision: candidate.revision,
                              rememberSource: remember,
                            },
                            candidate.name,
                          )
                        }
                      >
                        Use {candidate.name}
                      </Button>
                    </li>
                  ))}
                </ul>
                {!evidence.candidates.length && (
                  <p>
                    No matching product names. Search another name or name a new
                    product below.
                  </p>
                )}
                <div className="flex flex-wrap items-center gap-2 text-sm">
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    disabled={offset === 0}
                    onClick={() => setOffset(Math.max(0, offset - 10))}
                  >
                    Previous products
                  </Button>
                  <span>{evidence.total} products</span>
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    disabled={offset + evidence.limit >= evidence.total}
                    onClick={() => setOffset(offset + 10)}
                  >
                    Next products
                  </Button>
                </div>
              </>
            )
          )}
          <div className="space-y-2 border-t pt-3">
            <label htmlFor={`${id}-name`} className="block text-sm">
              New product name
            </label>
            <Input
              id={`${id}-name`}
              value={newName}
              maxLength={200}
              onKeyDown={(event) => {
                if (event.key === "Enter") event.preventDefault();
              }}
              onChange={(event) => setNewName(event.target.value)}
              placeholder="A readable name using only details you know"
            />
            <Button
              type="button"
              variant="secondary"
              disabled={!ready || !newName.trim()}
              onClick={() =>
                choose(
                  {
                    kind: "confirmNew",
                    newName: newName.trim(),
                    rememberSource: remember,
                  },
                  newName.trim(),
                )
              }
            >
              New product and use it
            </Button>
            <p className="text-xs text-muted-foreground">
              Created together with the receipt when you save. Existing names
              and changed choices are checked again.
            </p>
          </div>
          <Button
            type="button"
            variant="ghost"
            onClick={() => {
              if (evidence)
                choose(
                  { kind: "defer", rememberSource: false },
                  "Needs review",
                );
              else {
                onChange(undefined);
                setExpanded(false);
              }
            }}
          >
            Review later
          </Button>
        </div>
      )}
    </fieldset>
  );
}
