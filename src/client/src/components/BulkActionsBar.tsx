import { Icon } from "@/components/primitives";
import { Spinner } from "@/components/ui/spinner";
import { cn } from "@/lib/utils";

interface BulkActionsBarProps {
  /** Number of currently-selected items. The bar renders nothing when 0. */
  selectedCount: number;
  /** Total visible items, for "N of M" disclosure. */
  totalCount?: number;
  /** Singular noun for the entity, e.g. "receipt". */
  itemLabel: string;
  onClearSelection: () => void;
  onDelete: () => void;
  onPushToYnab?: () => void;
  isPushingToYnab?: boolean;
  isDeleting?: boolean;
  className?: string;
}

/**
 * Sticky bottom-anchored bar that appears when one or more rows are selected
 * on a list page. Renders inline (not portaled) so the test wrapper can
 * find it without extra plumbing; positioning is driven by the .bulk-actions
 * CSS class in index.css.
 */
export function BulkActionsBar({
  selectedCount,
  totalCount,
  itemLabel,
  onClearSelection,
  onDelete,
  onPushToYnab,
  isPushingToYnab = false,
  isDeleting = false,
  className,
}: BulkActionsBarProps) {
  if (selectedCount <= 0) return null;

  const noun = selectedCount === 1 ? itemLabel : `${itemLabel}s`;
  // Show "of N" only when we have a meaningful total — keeps the visible
  // text the same as the SR-announced text so we don't ship two readings
  // of the same fact.
  const ofTotal =
    totalCount != null && totalCount > 0 ? ` of ${totalCount}` : "";

  return (
    <div
      className={cn("bulk-actions", className)}
      role="region"
      aria-label="Bulk actions"
      // aria-live so screen readers announce the selection count as it
      // changes; polite so it doesn't pre-empt the user mid-task.
      aria-live="polite"
    >
      <div className="bulk-actions-count">
        <strong>{selectedCount}</strong>
        {ofTotal} {noun} selected
      </div>
      <div className="bulk-actions-spacer" />
      <button
        type="button"
        className="btn xs ghost"
        onClick={onClearSelection}
      >
        Clear
      </button>
      {onPushToYnab && (
        <button
          type="button"
          className="btn"
          onClick={onPushToYnab}
          disabled={isPushingToYnab}
        >
          {isPushingToYnab ? (
            <>
              <Spinner className="mr-2 h-3 w-3" />
              Pushing…
            </>
          ) : (
            <>
              <Icon.Link /> Push to YNAB
            </>
          )}
        </button>
      )}
      <button
        type="button"
        className="btn danger"
        onClick={onDelete}
        disabled={isDeleting}
      >
        {isDeleting ? (
          <>
            <Spinner className="mr-2 h-3 w-3" />
            Deleting…
          </>
        ) : (
          <>
            <Icon.Trash /> Delete
          </>
        )}
      </button>
    </div>
  );
}
