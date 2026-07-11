import { Icon } from "@/components/primitives";

interface ErrorStateProps {
  /** Short headline, e.g. "Couldn't load receipts". */
  title?: string;
  /** One-line explanation of what went wrong / what to try. */
  message?: string;
  /** Wire to the query's `refetch` to let the user retry in place. */
  onRetry?: () => void;
  /** Disables the retry button and shows a pending label while refetching. */
  isRetrying?: boolean;
}

/**
 * Full-width error placeholder for a failed list/data query.
 *
 * Mirrors the app's native empty-state markup (`.empty` / `.icon-frame` /
 * `.btn`, the same structure ReceiptDetail's error uses) so a failed query is
 * visually distinct from a genuinely-empty result — the bug in RECEIPTS-784
 * was that `data ?? []` made both look identical ("No receipts yet"). The
 * Retry button re-runs the query so users aren't stranded.
 */
export function ErrorState({
  title = "Something went wrong",
  message = "We couldn't load this data. Check your connection and try again.",
  onRetry,
  isRetrying = false,
}: ErrorStateProps) {
  return (
    <div className="empty" role="alert">
      <div className="icon-frame">
        <Icon.AlertTriangle />
      </div>
      <h3>{title}</h3>
      <p>{message}</p>
      {onRetry && (
        <div className="actions">
          <button
            type="button"
            className="btn primary"
            onClick={onRetry}
            disabled={isRetrying}
          >
            {isRetrying ? "Retrying…" : "Try again"}
          </button>
        </div>
      )}
    </div>
  );
}
