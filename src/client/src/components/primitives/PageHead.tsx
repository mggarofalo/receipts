import {
  forwardRef,
  type ComponentPropsWithoutRef,
  type ReactNode,
} from "react";
import { cn } from "@/lib/utils";

export interface PageHeadProps extends Omit<
  ComponentPropsWithoutRef<"div">,
  "title"
> {
  title: ReactNode;
  /** Mono uppercase strap line rendered under the title. */
  sub?: ReactNode;
  /** Action cluster; wraps below the title when the head is narrow. */
  actions?: ReactNode;
}

/**
 * Standard page header: serif title, optional sub-line, optional action
 * cluster. Layout is driven by the `.page-head` rule.
 *
 * @example
 * <PageHead title="Receipts" sub="42 total" actions={<Button>New</Button>} />
 */
export const PageHead = forwardRef<HTMLDivElement, PageHeadProps>(
  ({ title, sub, actions, className, ...props }, ref) => (
    <div ref={ref} className={cn("page-head", className)} {...props}>
      <div>
        <h1 className="page-title">{title}</h1>
        {sub && <div className="page-sub">{sub}</div>}
      </div>
      {actions && <div>{actions}</div>}
    </div>
  ),
);
PageHead.displayName = "PageHead";
