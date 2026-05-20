import { forwardRef, type ComponentPropsWithoutRef } from "react";
import { cn } from "@/lib/utils";

export interface CheckboxProps extends Omit<
  ComponentPropsWithoutRef<"button">,
  "type"
> {
  /** Whether the box is checked. */
  on: boolean;
}

/**
 * The design-system checkbox: a 15×15 box that fills with the accent when on.
 * The check mark is drawn by the `.checkbox.on::after` rule. For full form
 * integration prefer `@/components/ui/checkbox`.
 *
 * @example
 * <Checkbox on={selected} onClick={() => setSelected((v) => !v)} />
 */
export const Checkbox = forwardRef<HTMLButtonElement, CheckboxProps>(
  ({ on, className, ...props }, ref) => (
    <button
      ref={ref}
      type="button"
      role="checkbox"
      aria-checked={on}
      className={cn("checkbox", on && "on", className)}
      {...props}
    />
  ),
);
Checkbox.displayName = "Checkbox";
