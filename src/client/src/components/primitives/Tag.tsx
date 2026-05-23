import { forwardRef, type ComponentPropsWithoutRef } from "react";
import { cn } from "@/lib/utils";

/**
 * A small inline label for categories, stores, and similar metadata.
 *
 * @example
 * <Tag>Groceries</Tag>
 */
export const Tag = forwardRef<
  HTMLSpanElement,
  ComponentPropsWithoutRef<"span">
>(({ className, ...props }, ref) => (
  <span ref={ref} className={cn("tag", className)} {...props} />
));
Tag.displayName = "Tag";
