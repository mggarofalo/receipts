import type { CategoryNode } from "@/lib/category-paths";
import { monogram, rootHueStyle } from "@/lib/category-tree";
import { cn } from "@/lib/utils";

/** The root family's letter and hue; decorative, so hidden from assistive tech. */
export function CategoryMonogram({
  root,
  className,
}: {
  root?: CategoryNode;
  className?: string;
}) {
  if (!root)
    return (
      <span
        aria-hidden="true"
        className={cn("cat-monogram", className)}
        data-empty=""
        data-letter="–"
      />
    );
  return (
    <span
      aria-hidden="true"
      className={cn("cat-hue cat-monogram", className)}
      style={rootHueStyle(root)}
      data-letter={monogram(root.name)}
    />
  );
}
