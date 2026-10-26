import { forwardRef, useMemo, useState, type ComponentProps } from "react";
import { Check, ChevronsUpDown, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import {
  categoryPaths,
  type CategoryChoice,
  type CategoryRevision,
} from "@/lib/category-paths";
import { cn } from "@/lib/utils";
import { CategoryCreation } from "./CategoryCreation";

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
    disabled,
    className,
    ...props
  },
  ref,
) {
  const [open, setOpen] = useState(false);
  const [creating, setCreating] = useState(false);
  const rows = useMemo(
    () => categoryPaths(revision?.nodes ?? []),
    [revision?.nodes],
  );
  const label = value
    ? value.path.map((node) => node.name).join(" / ") || "Unclassified"
    : "Choose category";
  function select(categoryId: string | null) {
    if (!revision || disabled || loading || error) return;
    const row = rows.find((entry) => entry.node.id === categoryId);
    if (categoryId !== null && !row) return;
    onValueChange({
      categoryId,
      taxonomyRevisionId: revision.id,
      path: row?.path ?? [],
    });
    setOpen(false);
  }
  return (
    <Popover
      open={open}
      onOpenChange={(next) => {
        setOpen(next);
        if (!next) setCreating(false);
      }}
    >
      <PopoverTrigger asChild>
        <Button
          {...props}
          ref={ref}
          type="button"
          variant="outline"
          role="combobox"
          aria-expanded={open}
          aria-busy={loading || undefined}
          disabled={disabled || !revision || loading || error}
          className={cn(
            "h-9 w-full min-w-0 justify-between font-normal",
            className,
          )}
        >
          <span className="truncate">
            {value
              ? label
              : loading
                ? "Loading categories…"
                : error
                  ? "Categories unavailable"
                  : label}
          </span>
          <ChevronsUpDown className="ml-2 h-4 w-4 shrink-0 opacity-50" />
        </Button>
      </PopoverTrigger>
      <PopoverContent
        className="w-[--radix-popover-trigger-width] min-w-[18rem] p-0"
        align="start"
        aria-describedby={undefined}
      >
        {!creating && (
          <Command>
            <CommandInput placeholder="Search category paths…" />
            <CommandList>
              <CommandEmpty>No categories found.</CommandEmpty>
              <CommandGroup>
                {allowUnclassified && (
                  <CommandItem
                    value="unclassified"
                    onSelect={() => select(null)}
                  >
                    <Check
                      className={cn(
                        "mr-2 h-4 w-4",
                        value?.categoryId === null
                          ? "opacity-100"
                          : "opacity-0",
                      )}
                    />
                    Unclassified
                  </CommandItem>
                )}
                {rows.map((row) => (
                  <CommandItem
                    key={row.node.id}
                    value={row.node.id}
                    keywords={[row.label]}
                    onSelect={() => select(row.node.id)}
                  >
                    <Check
                      className={cn(
                        "mr-2 h-4 w-4",
                        value?.categoryId === row.node.id
                          ? "opacity-100"
                          : "opacity-0",
                      )}
                    />
                    <span>{row.label}</span>
                  </CommandItem>
                ))}
              </CommandGroup>
            </CommandList>
          </Command>
        )}
        {canCreate &&
          revision &&
          (creating ? (
            <CategoryCreation
              revision={revision}
              suggestedParentId={
                rows.some((row) => row.node.id === value?.categoryId)
                  ? value!.categoryId
                  : null
              }
              onCreated={(choice) => {
                onValueChange(choice);
                setCreating(false);
                setOpen(false);
              }}
              onCancel={() => setCreating(false)}
            />
          ) : (
            <div className="border-t p-1">
              <Button
                type="button"
                variant="ghost"
                size="sm"
                className="w-full justify-start"
                onClick={() => setCreating(true)}
              >
                <Plus className="mr-2 h-4 w-4" /> Create category
              </Button>
            </div>
          ))}
      </PopoverContent>
    </Popover>
  );
});
