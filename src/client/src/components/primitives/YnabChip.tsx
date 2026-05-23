import { forwardRef, type ComponentPropsWithoutRef } from "react";
import { cn } from "@/lib/utils";

export type YnabStatus = "synced" | "pending" | "error" | "none";

const STATUS: Record<
  YnabStatus,
  { label: string; chip: string; ariaLabel: string }
> = {
  synced: { label: "YNAB", chip: "chip pos", ariaLabel: "YNAB: synced" },
  pending: { label: "Pending", chip: "chip warn", ariaLabel: "YNAB: pending" },
  error: { label: "Error", chip: "chip neg", ariaLabel: "YNAB: error" },
  none: { label: "", chip: "", ariaLabel: "" },
};

export interface YnabChipProps extends ComponentPropsWithoutRef<"span"> {
  status: YnabStatus;
}

/**
 * Indicates a receipt's YNAB sync state — a coloured chip with a dot.
 *
 * @example
 * <YnabChip status="synced" />
 */
export const YnabChip = forwardRef<HTMLSpanElement, YnabChipProps>(
  ({ status, className, style, ...props }, ref) => {
    if (status === "none") return null;
    const { label, chip, ariaLabel } = STATUS[status];
    return (
      <span
        ref={ref}
        className={cn(chip, "ynab-chip", className)}
        style={style}
        aria-label={ariaLabel}
        {...props}
      >
        <span className="dot" aria-hidden="true" />
        {label}
      </span>
    );
  },
);
YnabChip.displayName = "YnabChip";
