import { useEffect, useRef, useState } from "react";
import { Icon } from "@/components/primitives";
import { Combobox } from "@/components/ui/combobox";
import { Input } from "@/components/ui/input";
import { useEnumMetadata } from "@/hooks/useEnumMetadata";
import { formatCurrency } from "@/lib/format";

/** The adjustment the reconcile sheet asks the caller to create. */
export interface ReconcileAdjustment {
  type: string;
  amount: number;
  description: string | null;
}

export interface ReconcileSheetProps {
  open: boolean;
  onClose: () => void;
  /**
   * Called when the user commits the balancing adjustment. The caller is
   * responsible for persisting it and closing the sheet on success.
   */
  onCreateAdjustment: (adjustment: ReconcileAdjustment) => void;
  isSubmitting?: boolean;
  receiptId: string;
  receiptLabel: string;
  receiptDate: string;
  /** The receipt's expected total (items + tax + existing adjustments). */
  receiptTotal: number;
  /** The summed total of the linked transactions. */
  transactionsTotal: number;
}

/**
 * Reconcile sheet for a receipt whose total does not match its linked
 * transactions. The only resolution it offers is to add an adjustment that
 * absorbs the difference (the receipt total then matches the transactions),
 * or to cancel and edit the receipt manually.
 */
export function ReconcileSheet({
  open,
  onClose,
  onCreateAdjustment,
  isSubmitting = false,
  receiptId,
  receiptLabel,
  receiptDate,
  receiptTotal,
  transactionsTotal,
}: ReconcileSheetProps) {
  const { adjustmentTypes } = useEnumMetadata();
  const [type, setType] = useState("");
  const [description, setDescription] = useState("");

  const sheetRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<Element | null>(null);

  // Amount needed so that receiptTotal + adjustment === transactionsTotal.
  const delta = Math.round((transactionsTotal - receiptTotal) * 100) / 100;
  const balanced = Math.abs(delta) < 0.005;

  // Reset the form each time the sheet opens (previous-prop render pattern).
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setType("");
      setDescription("");
    }
  }

  // Focus the sheet on open; restore focus to the trigger on close.
  useEffect(() => {
    if (!open) return;
    triggerRef.current = document.activeElement;
    sheetRef.current?.focus();
    return () => {
      const trigger = triggerRef.current as HTMLElement | null;
      if (trigger && typeof trigger.focus === "function") trigger.focus();
    };
  }, [open]);

  // The domain requires a description when the adjustment type is "other".
  const needsDescription = type.trim().toLowerCase() === "other";
  const descriptionMissing = needsDescription && description.trim() === "";
  const canSubmit =
    !balanced && type.trim() !== "" && !descriptionMissing && !isSubmitting;

  function handleSheetKeyDown(e: React.KeyboardEvent<HTMLDivElement>) {
    if (e.key === "Escape") {
      onClose();
      e.preventDefault();
      return;
    }
    if (e.key === "Tab") {
      const root = sheetRef.current;
      if (!root) return;
      const focusables = root.querySelectorAll<HTMLElement>(
        'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
      );
      if (focusables.length === 0) return;
      const first = focusables[0];
      const last = focusables[focusables.length - 1];
      const active = document.activeElement as HTMLElement | null;
      if (e.shiftKey && active === first) {
        last.focus();
        e.preventDefault();
      } else if (!e.shiftKey && active === last) {
        first.focus();
        e.preventDefault();
      }
    }
  }

  function handleOverlayMouseDown(e: React.MouseEvent<HTMLDivElement>) {
    if (e.target === e.currentTarget) onClose();
  }

  function handleCreate() {
    if (!canSubmit) return;
    onCreateAdjustment({
      type,
      amount: delta,
      description: description.trim() === "" ? null : description.trim(),
    });
  }

  if (!open) return null;

  return (
    <div
      className="recon-overlay"
      role="presentation"
      onMouseDown={handleOverlayMouseDown}
    >
      {/* This is a modal dialog: it owns Escape-to-close and a Tab focus trap.
          jsx-a11y flags handlers on the non-interactive <aside>, but
          role="dialog" + aria-modal is the correct pattern here. */}
      {/* eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions */}
      <aside
        ref={sheetRef}
        className="recon-sheet"
        role="dialog"
        aria-modal="true"
        aria-labelledby="recon-title"
        aria-describedby="recon-sub"
        tabIndex={-1}
        onKeyDown={handleSheetKeyDown}
      >
        <header className="recon-head">
          <div>
            <div className="recon-title" id="recon-title">
              Reconcile receipt
            </div>
            <div className="recon-sub" id="recon-sub">
              REC-{receiptId.slice(0, 8).toUpperCase()} · {receiptLabel} ·{" "}
              {receiptDate}
            </div>
          </div>
          <button
            type="button"
            className="icon-btn"
            onClick={onClose}
            aria-label="Close reconcile sheet"
          >
            <Icon.X />
          </button>
        </header>

        <div className="recon-delta-bar">
          <div>
            <div className="k">Receipt total</div>
            <div className="v">{formatCurrency(receiptTotal)}</div>
          </div>
          <div className="sep" aria-hidden="true">
            {balanced ? "=" : "≠"}
          </div>
          <div>
            <div className="k">Transactions</div>
            <div className="v">{formatCurrency(transactionsTotal)}</div>
          </div>
          <div className="sep" aria-hidden="true">
            Δ
          </div>
          <div>
            <div className="k">Difference</div>
            <div className={"v " + (balanced || delta > 0 ? "pos" : "neg")}>
              {balanced
                ? "±0.00"
                : (delta > 0 ? "+" : "−") +
                  formatCurrency(Math.abs(delta)).replace(/^-/, "")}
            </div>
          </div>
        </div>

        <div className="recon-body">
          {balanced ? (
            <div className="empty" style={{ marginTop: 0 }}>
              <div className="icon-frame">
                <Icon.Check />
              </div>
              <h3>Receipt is balanced</h3>
              <p>
                The receipt total already matches its linked transactions.
                Nothing to reconcile.
              </p>
            </div>
          ) : (
            <>
              <div className="recon-section-label">
                Balance with an adjustment
              </div>
              <p
                style={{
                  fontSize: 13.5,
                  color: "var(--ink-2)",
                  margin: "0 0 16px",
                  lineHeight: 1.5,
                }}
              >
                Add an adjustment of{" "}
                <strong style={{ color: "var(--ink)" }}>
                  {formatCurrency(delta)}
                </strong>{" "}
                so the receipt total matches the linked transactions. Pick the
                adjustment type below, or cancel to edit the receipt yourself.
              </p>

              <div style={{ marginBottom: 14 }}>
                <label
                  htmlFor="recon-adj-type"
                  style={{
                    display: "block",
                    fontSize: 12.5,
                    fontWeight: 500,
                    color: "var(--ink)",
                    marginBottom: 6,
                  }}
                >
                  Adjustment type{" "}
                  <span aria-hidden="true" style={{ color: "var(--neg-ink)" }}>
                    *
                  </span>
                </label>
                <Combobox
                  id="recon-adj-type"
                  options={adjustmentTypes}
                  value={type}
                  onValueChange={setType}
                  placeholder="Select adjustment type…"
                  searchPlaceholder="Search types…"
                  aria-label="Adjustment type"
                />
              </div>

              {needsDescription && (
                <div style={{ marginBottom: 14 }}>
                  <label
                    htmlFor="recon-adj-desc"
                    style={{
                      display: "block",
                      fontSize: 12.5,
                      fontWeight: 500,
                      color: "var(--ink)",
                      marginBottom: 6,
                    }}
                  >
                    Description{" "}
                    <span
                      aria-hidden="true"
                      style={{ color: "var(--neg-ink)" }}
                    >
                      *
                    </span>
                  </label>
                  <Input
                    id="recon-adj-desc"
                    value={description}
                    onChange={(e) => setDescription(e.target.value)}
                    placeholder="Describe this adjustment"
                    aria-label="Adjustment description"
                    aria-required="true"
                  />
                  {descriptionMissing && (
                    <p
                      role="status"
                      style={{
                        fontSize: 12,
                        color: "var(--neg-ink)",
                        margin: "6px 0 0",
                      }}
                    >
                      A description is required for the “other” type.
                    </p>
                  )}
                </div>
              )}
            </>
          )}
        </div>

        <footer className="recon-foot">
          <span className="sr-only">Press Escape to close.</span>
          <span style={{ marginLeft: "auto", display: "flex", gap: 8 }}>
            <button type="button" className="btn" onClick={onClose}>
              Cancel
            </button>
            {!balanced && (
              <button
                type="button"
                className="btn primary"
                onClick={handleCreate}
                disabled={!canSubmit}
              >
                <Icon.Check />{" "}
                {isSubmitting ? "Creating…" : "Create adjustment"}
              </button>
            )}
          </span>
        </footer>
      </aside>
    </div>
  );
}
