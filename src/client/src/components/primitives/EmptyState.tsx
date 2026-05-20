import {
  forwardRef,
  type ComponentPropsWithoutRef,
  type ReactNode,
} from "react";
import { cn } from "@/lib/utils";
import { Icon, type IconComponent } from "./icons";

export interface EmptyStateProps extends ComponentPropsWithoutRef<"div"> {
  /** An `Icon.*` member; defaults to `Icon.Inbox`. */
  icon?: IconComponent;
  title: string;
  body?: ReactNode;
  /** Action buttons rendered below the copy. */
  actions?: ReactNode;
}

/**
 * Shown wherever a list or surface has no data. Icon size and stroke are
 * driven by the `.empty .icon-frame svg` rule.
 *
 * @example
 * <EmptyState
 *   icon={Icon.Inbox}
 *   title="No receipts yet"
 *   body="Add your first receipt to get started."
 *   actions={<Button>New receipt</Button>}
 * />
 */
export const EmptyState = forwardRef<HTMLDivElement, EmptyStateProps>(
  ({ icon, title, body, actions, className, ...props }, ref) => {
    const IconComp = icon ?? Icon.Inbox;
    return (
      <div ref={ref} className={cn("empty", className)} {...props}>
        <div className="icon-frame">
          <IconComp aria-hidden />
        </div>
        <h3>{title}</h3>
        {body && <p>{body}</p>}
        {actions && <div className="actions">{actions}</div>}
      </div>
    );
  },
);
EmptyState.displayName = "EmptyState";
