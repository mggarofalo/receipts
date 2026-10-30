import { forwardRef, useMemo, useState, type ComponentProps } from "react";
import { ChevronsUpDown } from "lucide-react";
import { Dialog as SheetPrimitive } from "radix-ui";
import { Button } from "@/components/ui/button";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import { Sheet, SheetContent, SheetTitle } from "@/components/ui/sheet";
import { useIsMobile } from "@/hooks/useIsMobile";
import type { CategoryChoice, CategoryRevision } from "@/lib/category-paths";
import { indexCategories } from "@/lib/category-tree";
import { cn } from "@/lib/utils";
import { CategoryBrowser, type CategorySuggestion } from "./CategoryBrowser";
import { CategoryCreation } from "./CategoryCreation";
import { CategoryMonogram } from "./CategoryMonogram";

export type { CategorySuggestion };

interface CategoryPathPickerProps extends Omit<
  ComponentProps<"button">,
  "value" | "onChange"
> {
  revision?: CategoryRevision;
  value?: CategoryChoice;
  onValueChange: (choice: CategoryChoice) => void;
  loading?: boolean;
  error?: boolean;
  canCreate?: boolean;
  allowUnclassified?: boolean;
  /** Offered above the tree so the likely answer is one tap away. */
  suggestions?: CategorySuggestion[];
}

function compactAncestry(path: CategoryChoice["path"]) {
  const ancestors = path.slice(0, -1);
  return ancestors.length > 2
    ? `${ancestors[0].name} / … / ${ancestors.at(-1)!.name}`
    : ancestors.map((node) => node.name).join(" / ");
}

export const CategoryPathPicker = forwardRef<
  HTMLButtonElement,
  CategoryPathPickerProps
>(function CategoryPathPicker(
  {
    revision,
    value,
    onValueChange,
    loading = false,
    error = false,
    canCreate = false,
    allowUnclassified = true,
    suggestions,
    disabled,
    className,
    ...props
  },
  ref,
) {
  const isMobile = useIsMobile();
  const [open, setOpen] = useState(false);
  const [creating, setCreating] = useState<{
    parentId: string | null;
    name: string;
  }>();
  const index = useMemo(
    () => indexCategories(revision?.nodes ?? []),
    [revision?.nodes],
  );
  const label = value
    ? value.path.map((node) => node.name).join(" / ") || "Unclassified"
    : "Choose category";
  function onOpenChange(next: boolean) {
    setOpen(next);
    if (!next) setCreating(undefined);
  }
  function select(categoryId: string | null) {
    if (!revision || disabled || loading || error) return;
    const row = categoryId === null ? undefined : index.byId.get(categoryId);
    if (categoryId !== null && !row) return;
    onValueChange({
      categoryId,
      taxonomyRevisionId: revision.id,
      path: row?.path ?? [],
    });
    onOpenChange(false);
  }

  const trigger = (
    <Button
      {...props}
      ref={ref}
      type="button"
      variant="outline"
      role="combobox"
      aria-expanded={open}
      aria-busy={loading || undefined}
      title={label}
      disabled={disabled || !revision || loading || error}
      className={cn(
        "h-auto min-h-9 w-full min-w-0 justify-between gap-2 py-1.5 text-left font-normal",
        className,
      )}
    >
      {value?.path.length ? (
        <CategoryMonogram
          root={value.path[0]}
          className="h-5 w-5 text-[0.65rem]"
        />
      ) : null}
      <span className="min-w-0 flex-1">
        <span className="block truncate">
          {value?.path.length
            ? value.path.at(-1)!.name
            : value
              ? label
              : loading
                ? "Loading categories…"
                : error
                  ? "Categories unavailable"
                  : label}
        </span>
        {value && value.path.length > 1 && (
          <span className="block truncate text-xs text-muted-foreground">
            {compactAncestry(value.path)}
          </span>
        )}
      </span>
      <ChevronsUpDown className="h-4 w-4 shrink-0 opacity-50" />
    </Button>
  );

  const panel =
    creating && revision ? (
      <div className="min-h-0 flex-1 overflow-y-auto">
        <CategoryCreation
          revision={revision}
          suggestedParentId={creating.parentId}
          initialName={creating.name}
          onCreated={(choice) => {
            onValueChange(choice);
            onOpenChange(false);
          }}
          onCancel={() => setCreating(undefined)}
        />
      </div>
    ) : (
      <CategoryBrowser
        index={index}
        value={value}
        allowUnclassified={allowUnclassified}
        suggestions={suggestions}
        touch={isMobile}
        onSelect={select}
        onCreate={
          canCreate && revision
            ? (parentId, name) => setCreating({ parentId, name })
            : undefined
        }
      />
    );

  if (isMobile)
    return (
      <Sheet open={open} onOpenChange={onOpenChange}>
        <SheetPrimitive.Trigger asChild>{trigger}</SheetPrimitive.Trigger>
        <SheetContent
          side="bottom"
          aria-describedby={undefined}
          data-slot="category-picker-sheet"
          className="h-[min(92dvh,48rem)] gap-0 overflow-hidden rounded-t-xl p-0"
          style={{
            backgroundColor: "var(--popover)",
            color: "var(--popover-foreground)",
          }}
          // Browsing by touch comes first; the keyboard opens only on request.
          onOpenAutoFocus={(event) => {
            event.preventDefault();
            (event.currentTarget as HTMLElement)
              .querySelector<HTMLElement>("[data-category-browser]")
              ?.focus();
          }}
        >
          <SheetTitle className="px-4 pt-4 pb-2 pr-12 text-base">
            Choose category
          </SheetTitle>
          {panel}
        </SheetContent>
      </Sheet>
    );

  return (
    <Popover open={open} onOpenChange={onOpenChange}>
      <PopoverTrigger asChild>{trigger}</PopoverTrigger>
      <PopoverContent
        className="flex w-[var(--radix-popover-trigger-width)] min-w-[min(26rem,calc(100vw-2rem))] max-w-[calc(100vw-2rem)] max-h-[min(34rem,var(--radix-popover-content-available-height))] flex-col overflow-hidden p-0 shadow-lg"
        style={{
          backgroundColor: "var(--popover)",
          color: "var(--popover-foreground)",
        }}
        align="start"
        aria-describedby={undefined}
      >
        {panel}
      </PopoverContent>
    </Popover>
  );
});
