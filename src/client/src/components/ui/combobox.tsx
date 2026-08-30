import * as React from "react";
import { Check, ChevronsUpDown } from "lucide-react";
import { cn } from "@/lib/utils";
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

export interface ComboboxOption {
  value: string;
  label: string;
  sublabel?: string;
}

interface ComboboxProps
  extends Omit<React.ComponentProps<"button">, "value" | "onChange"> {
  options: ComboboxOption[];
  value: string;
  onValueChange: (value: string) => void;
  placeholder?: string;
  searchPlaceholder?: string;
  emptyMessage?: string;
  disabled?: boolean;
  loading?: boolean;
  allowCustom?: boolean;
  className?: string;
}

const Combobox = React.forwardRef<HTMLButtonElement, ComboboxProps>(
  function Combobox(
    {
      options,
      value,
      onValueChange,
      placeholder = "Select...",
      searchPlaceholder = "Search...",
      emptyMessage = "No results found.",
      disabled = false,
      loading = false,
      allowCustom = false,
      className,
      ...rest
    },
    ref,
  ) {
    const [open, setOpen] = React.useState(false);
    const [search, setSearch] = React.useState("");

    const selected = options.find((o) => o.value === value);

    return (
      <Popover
        open={open}
        onOpenChange={(isOpen) => {
          setOpen(isOpen);
          if (!isOpen) setSearch("");
        }}
      >
        <PopoverTrigger asChild>
          <Button
            ref={ref}
            variant="outline"
            role="combobox"
            aria-expanded={open}
            aria-busy={loading || undefined}
            disabled={disabled}
            className={cn(
              // min-w-0 so the trigger can shrink to its container and let the
              // truncate below do its job, instead of forcing its min-content
              // width on whatever row it sits in.
              "h-9 w-full min-w-0 justify-between font-normal",
              !selected && !value && "text-muted-foreground",
              className,
            )}
            {...rest}
          >
            <span className="truncate">
              {loading
                ? "Loading..."
                : selected
                  ? selected.label
                  : value
                    ? value
                    : placeholder}
            </span>
            <ChevronsUpDown className="ml-2 h-4 w-4 shrink-0 opacity-50" />
          </Button>
        </PopoverTrigger>
        <PopoverContent
          className="w-[--radix-popover-trigger-width] p-0"
          align="start"
          aria-describedby={undefined}
        >
          <Command>
            <CommandInput
              placeholder={searchPlaceholder}
              value={search}
              onValueChange={setSearch}
            />
            <CommandList>
              <CommandEmpty>
                {allowCustom && search ? (
                  <button
                    type="button"
                    className="w-full px-2 py-1.5 text-left text-sm"
                    onClick={() => {
                      onValueChange(search);
                      setOpen(false);
                      setSearch("");
                    }}
                  >
                    Use &quot;{search}&quot;
                  </button>
                ) : loading ? (
                  // Consumers can supply a domain-specific loading message via
                  // emptyMessage (e.g. "Loading adjustment types…"). Fall back
                  // to a generic string only when nothing was provided.
                  emptyMessage && emptyMessage !== "No results found." ? (
                    emptyMessage
                  ) : (
                    "Loading..."
                  )
                ) : (
                  emptyMessage
                )}
              </CommandEmpty>
              <CommandGroup>
                {options.map((option) => (
                  <CommandItem
                    key={option.value}
                    value={
                      option.sublabel
                        ? `${option.label} ${option.sublabel}`
                        : option.label
                    }
                    onSelect={() => {
                      onValueChange(option.value);
                      setOpen(false);
                      setSearch("");
                    }}
                  >
                    <Check
                      className={cn(
                        "mr-2 h-4 w-4",
                        value === option.value ? "opacity-100" : "opacity-0",
                      )}
                    />
                    <div className="flex flex-col">
                      <span>{option.label}</span>
                      {option.sublabel && (
                        <span className="text-xs text-muted-foreground">
                          {option.sublabel}
                        </span>
                      )}
                    </div>
                  </CommandItem>
                ))}
              </CommandGroup>
            </CommandList>
          </Command>
        </PopoverContent>
      </Popover>
    );
  },
);

export { Combobox };
