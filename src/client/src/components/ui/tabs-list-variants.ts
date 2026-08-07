import { cva } from "class-variance-authority";

// `max-w-full overflow-x-auto` so a tab strip that outgrows a narrow viewport scrolls inside its
// own box instead of widening the document (RECEIPTS-880). `w-fit` alone let four tabs push the
// page ~10px past 375px, and a horizontally-scrolling page detaches every fixed element from the
// content and fights vertical scrolling on touch. Guarded by tests/visual/page-overflow.spec.ts.
export const tabsListVariants = cva(
  "rounded-lg p-[3px] group-data-[orientation=horizontal]/tabs:h-9 data-[variant=line]:rounded-none group/tabs-list text-muted-foreground inline-flex w-fit max-w-full overflow-x-auto items-center justify-center group-data-[orientation=vertical]/tabs:h-fit group-data-[orientation=vertical]/tabs:flex-col",
  {
    variants: {
      variant: {
        default: "bg-muted",
        line: "gap-1 bg-transparent",
      },
    },
    defaultVariants: {
      variant: "default",
    },
  },
);
