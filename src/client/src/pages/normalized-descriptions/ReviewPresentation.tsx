import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import { useRenameMutation } from "@/hooks/useNormalizedDescriptionActions";
import { formatDecimal } from "@/lib/format";
import { useId, useState } from "react";
import type { NormalizedDescription } from "./types";
interface ActionButtonProps {
  label: string;
  hint: string;
  disabled?: boolean;
  onClick: () => void;
}
/**
 * A row action whose consequence is readable before you commit to it.
 *
 * The hint is both a tooltip and the button's accessible description, so it reaches a keyboard or
 * screen-reader user as well as one who happens to hover. `aria-describedby` rather than
 * `aria-label`, because the label is the action and the hint is what it does — collapsing them
 * would make the button announce a paragraph where a verb belongs.
 */
export function ActionButton({
  label,
  hint,
  disabled,
  onClick,
}: ActionButtonProps) {
  const hintId = useId();

  return (
    <>
      <Tooltip>
        <TooltipTrigger asChild>
          <Button
            variant="outline"
            size="sm"
            disabled={disabled}
            onClick={onClick}
            aria-describedby={hintId}
          >
            {label}
          </Button>
        </TooltipTrigger>
        <TooltipContent className="max-w-xs">{hint}</TooltipContent>
      </Tooltip>
      <span id={hintId} className="sr-only">
        {hint}
      </span>
    </>
  );
}
/**
 * What this queue is and what the four actions do (RECEIPTS-874).
 *
 * Sits above the table rather than behind a help icon: the actions are destructive and mostly
 * irreversible, and a reviewer meeting the queue for the first time has no way to know that
 * Merge deletes a row. Collapsed by default after the first read would be nice, but a
 * remembered-dismissal is state we would have to persist and get wrong; a short block that is
 * cheap to skip is the better trade.
 */
export function ReviewQueueExplainer() {
  return (
    <div
      className="rounded-lg border bg-muted/30 p-4 text-sm"
      data-testid="review-queue-explainer"
    >
      <h2 className="card-title">What is in this queue</h2>
      <p className="mt-1 text-muted-foreground">
        These are catalog identities awaiting activation, including historical
        suggestions. Their existing purchase links are evidence, not proof of
        review. Activating a catalog identity does not verify linked purchases;
        review those through receipt entry or scoped source-group approval.
      </p>
      <dl className="mt-3 grid gap-2 sm:grid-cols-2">
        <div>
          <dt className="font-medium">Approve</dt>
          <dd className="text-muted-foreground">
            Make this identity available for selection. Nothing moves; purchase
            verification remains a separate decision.
          </dd>
        </div>
        <div>
          <dt className="font-medium">Merge into…</dt>
          <dd className="text-muted-foreground">
            This is the same as an entry you already have. Its items are
            re-pointed there and <strong>this entry is deleted</strong>. Cannot
            be undone.
          </dd>
        </div>
        <div>
          <dt className="font-medium">Split</dt>
          <dd className="text-muted-foreground">
            Some of these items belong together under a different name. You pick
            which, and the rest stay here.
          </dd>
        </div>
        <div>
          <dt className="font-medium">Reject</dt>
          <dd className="text-muted-foreground">
            This text does not deserve an entry. Its items become unnormalized
            and this identity is unavailable for selection.
          </dd>
        </div>
        <div>
          <dt className="font-medium">Link to template…</dt>
          <dd className="text-muted-foreground">
            You already have a template for this item. Its items move to that
            template's entry and <strong>this entry is deleted</strong>, unless
            it already is that entry.
          </dd>
        </div>
      </dl>
      <p className="mt-3 text-muted-foreground">
        “Declared by template” records where a catalog name came from. It does
        not establish that each linked purchase was reviewed.
      </p>
      <p className="mt-3 text-muted-foreground">
        You can also rename an entry in place. That changes the label only —
        never the receipt text it matches on — so renaming can never change what
        resolves here later.
      </p>
    </div>
  );
}

interface EditableNameProps {
  row: NormalizedDescription;
}
/**
 * The row's display name, editable in place (RECEIPTS-876).
 *
 * Editing writes `displayLabel` only. The matched text underneath is what the embedding is
 * anchored to, so it is shown read-only whenever a label diverges from it — a reviewer renaming
 * "MILK 2% GAL" to "Milk" still needs to see which receipt text the entry actually covers.
 */
export function EditableName({ row }: EditableNameProps) {
  const rename = useRenameMutation();
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");

  function startEditing() {
    if (rename.isPending) return;
    setDraft(row.displayLabel ?? row.canonicalName);
    setEditing(true);
  }

  function commit() {
    if (rename.isPending) return;
    const trimmed = draft.trim();
    if (!trimmed) return;
    // Renaming a row back to its matched text is a clear, not a label that happens to match.
    const next = trimmed === row.canonicalName ? null : trimmed;
    if (next === (row.displayLabel ?? null)) {
      setEditing(false);
      return;
    }

    rename.mutate(
      { id: row.id, displayLabel: next },
      { onSuccess: () => setEditing(false) },
    );
  }

  if (editing) {
    return (
      <div className="flex items-center gap-2">
        <Input
          value={draft}
          // Focus follows the user's own click on "Rename", so it is focus management rather
          // than the page-load focus stealing that jsx-a11y/no-autofocus guards against. A
          // callback ref does the same job without tripping the rule.
          ref={(el) => el?.focus()}
          aria-label={`Display name for ${row.displayName}`}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") commit();
            if (e.key === "Escape" && !rename.isPending) setEditing(false);
          }}
          className="h-8 max-w-[16rem]"
        />
        <Button
          size="sm"
          onClick={commit}
          disabled={!draft.trim() || rename.isPending}
        >
          {rename.isPending ? "Saving…" : "Save"}
        </Button>
        <Button
          variant="ghost"
          size="sm"
          onClick={() => setEditing(false)}
          disabled={rename.isPending}
        >
          Cancel
        </Button>
      </div>
    );
  }

  return (
    <div className="flex items-baseline gap-2">
      <span>{row.displayName}</span>
      <Button
        variant="ghost"
        size="sm"
        className="h-6 px-2 text-xs font-normal text-muted-foreground"
        onClick={startEditing}
        aria-label={`Rename ${row.displayName}`}
      >
        Rename
      </Button>
      {row.displayLabel && (
        <span className="text-xs font-normal text-muted-foreground">
          matches “{row.canonicalName}”
        </span>
      )}
    </div>
  );
}

interface NearestMatchProps {
  name: string | null | undefined;
  similarity: number | null | undefined;
}
/**
 * The evidence that explains why a row is sitting in the review queue at all (RECEIPTS-873).
 *
 * The absent case is deliberately distinguished from a zero score: a row with no recorded
 * neighbour was never compared against anything, and rendering that as "0.00" would assert a
 * near-miss that never happened. Guards are nullish, not falsy, so a genuine similarity of 0
 * still prints as 0.00.
 */
export function NearestMatch({ name, similarity }: NearestMatchProps) {
  if (similarity == null) {
    return (
      <span className="text-sm text-muted-foreground">
        No comparison recorded
      </span>
    );
  }

  // Merging the neighbour away nulls the FK (ON DELETE SET NULL) but leaves the score behind.
  // The score is still a true record of what happened, so we show it rather than pretending
  // no comparison was ever made.
  if (name == null) {
    return (
      <span className="text-sm">
        Nearly matched a since-removed entry at{" "}
        <span className="tabular-nums">{formatDecimal(similarity, 2)}</span>
      </span>
    );
  }

  return (
    <span className="text-sm">
      Nearly matched <span className="font-medium">{name}</span> at{" "}
      <span className="tabular-nums">{formatDecimal(similarity, 2)}</span>
    </span>
  );
}
/**
 * The most recent receipt this entry still appears on (RECEIPTS-880).
 *
 * "Never" is not a date in the distant past — an entry with nothing linked has never matched
 * anything, which is a different fact from one that last matched in 2023, and the two call for
 * opposite decisions.
 */
export function LastSeen({ value }: { value: string | null | undefined }) {
  if (value == null) {
    return <span className="text-sm text-muted-foreground">Never</span>;
  }

  return (
    <span className="text-sm text-muted-foreground">
      {new Date(value).toLocaleDateString()}
    </span>
  );
}
/**
 * That a user already named this item by hand (RECEIPTS-930).
 *
 * The strongest evidence on the row, and the only one that is not a machine's opinion: a template
 * exists because somebody typed it. A pending row carrying one is almost always an Approve, and an
 * Active row carrying one should not be merged away casually — it is somebody's curated entry, not
 * resolver output.
 *
 * Read off the FK, never inferred from the name, so it can only ever say "these are linked" and
 * never "these look alike". The count is surfaced because more than one template on a row is the
 * ordinary result of merging two template-backed entries, and naming one while implying it is the
 * only one would hide exactly the case worth seeing.
 */
export function TemplateEvidence({ row }: { row: NormalizedDescription }) {
  const name = row.linkedTemplateName;
  if (name == null) return null;

  const others = (row.linkedTemplateCount ?? 1) - 1;

  return (
    <p className="mt-1" data-testid="template-evidence">
      <Badge variant="secondary" className="font-normal">
        Declared by template “{name}”
        {others > 0 && ` and ${others} ${others === 1 ? "other" : "others"}`}
      </Badge>
    </p>
  );
}
/**
 * The raw receipt text this canonical row actually covers — the difference between approving a
 * name and approving a grouping.
 */
export function SampleRawDescriptions({
  samples,
}: {
  samples: string[] | undefined;
}) {
  if (!samples || samples.length === 0) return null;

  return (
    <p className="mt-1 text-xs font-normal text-muted-foreground">
      Seen as: {samples.join(", ")}
    </p>
  );
}
