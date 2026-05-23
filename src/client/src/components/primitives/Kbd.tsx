import { forwardRef, type ComponentPropsWithoutRef } from "react";
import { cn } from "@/lib/utils";

/**
 * A keyboard-key label. Mono, bordered, sits on `--surface`.
 *
 * @example
 * <Kbd>⌘</Kbd> <Kbd>K</Kbd>
 */
export const Kbd = forwardRef<HTMLElement, ComponentPropsWithoutRef<"kbd">>(
  ({ className, ...props }, ref) => (
    <kbd ref={ref} className={cn("kbd", className)} {...props} />
  ),
);
Kbd.displayName = "Kbd";
