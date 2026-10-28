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
  type CategoryPath,
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
  const groups = useMemo(() => {
    const grouped = new Map<string, { name: string; rows: CategoryPath[] }>();
    for (const row of rows) {
      const root = row.path[0];
      const group = grouped.get(root.id) ?? { name: root.name, rows: [] };
      group.rows.push(row);
      grouped.set(root.id, group);
    }
    return Array.from(grouped.entries());
  }, [rows]);
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
          title={label}
          disabled={disabled || !revision || loading || error}
          className={cn(
            "h-auto min-h-9 w-full min-w-0 justify-between py-1.5 text-left font-normal",
            className,
          )}
        >
          <span className="min-w-0">
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
          <ChevronsUpDown className="ml-2 h-4 w-4 shrink-0 opacity-50" />
        </Button>
      </PopoverTrigger>
      <PopoverContent
        className="w-[var(--radix-popover-trigger-width)] min-w-[min(24rem,calc(100vw-2rem))] max-w-[calc(100vw-2rem)] max-h-[var(--radix-popover-content-available-height)] overflow-y-auto p-0 shadow-lg"
        style={{
          backgroundColor: "var(--popover)",
          color: "var(--popover-foreground)",
        }}
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
              </CommandGroup>
              {groups.map(([rootId, group]) => (
                <CommandGroup key={rootId} heading={group.name}>
                  {group.rows.map((row) => (
                    <CommandItem
                      key={row.node.id}
                      value={row.node.id}
                      keywords={[row.label]}
                      aria-label={row.label}
                      title={row.label}
                      className="items-start py-2"
                      onSelect={() => select(row.node.id)}
                    >
                      <Check
                        className={cn(
                          "mt-0.5 h-4 w-4 shrink-0",
                          value?.categoryId === row.node.id
                            ? "opacity-100"
                            : "opacity-0",
                        )}
                      />
                      <span
                        className="min-w-0 flex-1"
                        style={{
                          paddingInlineStart: `${Math.min(row.path.length - 1, 3) * 0.5}rem`,
                        }}
                      >
                        <span className="block break-words font-medium">
                          {row.node.name}
                        </span>
                        <span className="block truncate text-xs text-muted-foreground">
                          {row.path.length === 1
                            ? "General · use this category"
                            : compactAncestry(row.path)}
                        </span>
                      </span>
                    </CommandItem>
                  ))}
                </CommandGroup>
              ))}
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
