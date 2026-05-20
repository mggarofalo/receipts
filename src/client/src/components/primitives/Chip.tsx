import { forwardRef, type ComponentPropsWithoutRef } from "react";
import { cn } from "@/lib/utils";

export type ChipVariant =
  | "default"
  | "pos"
  | "neg"
  | "warn"
  | "solid"
  | "ghost";

export interface ChipProps extends ComponentPropsWithoutRef<"span"> {
  variant?: ChipVariant;
}

/**
 * A compact status pill. Variant styling lives in `.chip` / `.chip.<variant>`.
 *
 * @example
 * <Chip variant="pos"><span className="dot" />Reconciled</Chip>
 */
export const Chip = forwardRef<HTMLSpanElement, ChipProps>(
  ({ variant = "default", className, ...props }, ref) => (
    <span
      ref={ref}
      className={cn("chip", variant !== "default" && variant, className)}
      {...props}
    />
  ),
);
Chip.displayName = "Chip";
