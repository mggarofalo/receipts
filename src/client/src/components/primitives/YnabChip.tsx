import { forwardRef, type ComponentPropsWithoutRef } from "react";
import { cn } from "@/lib/utils";

export type YnabStatus = "synced" | "pending" | "error" | "none";

const STATUS: Record<YnabStatus, { label: string; chip: string }> = {
  synced: { label: "YNAB", chip: "chip pos" },
  pending: { label: "Pending", chip: "chip warn" },
  error: { label: "Error", chip: "chip neg" },
  none: { label: "—", chip: "chip ghost" },
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
    const { label, chip } = STATUS[status];
    return (
      <span
        ref={ref}
        className={cn(chip, "ynab-chip", className)}
        style={status === "none" ? { opacity: 0.5, ...style } : style}
        {...props}
      >
        <span
          className="dot"
          style={
            status === "none" ? { background: "var(--mute-2)" } : undefined
          }
        />
        {label}
      </span>
    );
  },
);
YnabChip.displayName = "YnabChip";
