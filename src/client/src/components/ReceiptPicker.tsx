import { forwardRef, useState, type ComponentProps } from "react";
import { Check, ChevronsUpDown } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { RequestFailure } from "@/components/RequestFailure";
import { useReceiptPicker, type PickerReceipt } from "@/hooks/useReceiptPicker";
import { queryKeys } from "@/lib/query-invalidation";
import { receiptToOption } from "@/lib/combobox-options";
import { cn } from "@/lib/utils";
import type { components } from "@/generated/api";

type Receipt = components["schemas"]["ReceiptResponse"];

interface ReceiptPickerProps extends Omit<ComponentProps<"button">, "value" | "onChange"> {
  value: string;
  selectedReceipt?: Receipt;
  onValueChange: (value: string) => void;
}

/** A bounded receipt browser with local matching and remote Location search. */
export const ReceiptPicker = forwardRef<HTMLButtonElement, ReceiptPickerProps>(function ReceiptPicker({
  value,
  selectedReceipt,
  onValueChange,
  disabled = false,
  className,
  ...triggerProps
}, ref) {
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState("");
  const picker = useReceiptPicker(open, search);
  const selected = selectedReceipt ? receiptToOption(selectedReceipt) : undefined;

  function choose(receipt: PickerReceipt) {
    const detail: Receipt = {
      id: receipt.id,
      location: receipt.location,
      date: receipt.date,
      taxAmount: receipt.taxAmount,
    };
    const detailKey = [...queryKeys.receipts, receipt.id];
    // A list row is useful as an immediate label/location, but it is not allowed
    // to replace a newer independently cached detail. Mark a new seed stale so
    // useReceipt verifies it instead of inheriting the app's five-minute freshness.
    if (queryClient.getQueryData(detailKey) === undefined) {
      queryClient.setQueryData(detailKey, detail, { updatedAt: 0 });
    }
    onValueChange(receipt.id);
    setOpen(false);
    setSearch("");
  }

  return (
    <Popover
      open={open}
      onOpenChange={(next) => {
        setOpen(next);
        if (!next) setSearch("");
      }}
    >
      <PopoverTrigger asChild>
        <Button
          ref={ref}
          type="button"
          variant="outline"
          role="combobox"
          aria-expanded={open}
          disabled={disabled}
          className={cn("h-9 w-full min-w-0 justify-between font-normal", !value && "text-muted-foreground", className)}
          {...triggerProps}
        >
          <span className="truncate">{selected?.label ?? (value || "Select a receipt...")}</span>
          <ChevronsUpDown className="ml-2 h-4 w-4 shrink-0 opacity-50" />
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-[--radix-popover-trigger-width] p-0" align="start" aria-describedby={undefined}>
        <Command shouldFilter={false}>
          <CommandInput
            placeholder="Search receipts..."
            value={search}
            onValueChange={setSearch}
          />
          <CommandList
            onScroll={(event) => {
              const list = event.currentTarget;
              if (list.scrollHeight - list.scrollTop - list.clientHeight < 48) picker.loadMore();
            }}
          >
            <CommandEmpty>
              {picker.isLoading
                ? "Loading receipts…"
                : picker.isSuccess
                  ? search
                    ? picker.complete
                      ? "No matching receipts."
                      : "No matching receipts in the loaded history."
                    : "No receipts found."
                  : "Receipts are unavailable."}
            </CommandEmpty>
            <CommandGroup>
              {picker.receipts.map((receipt) => {
                const option = receiptToOption(receipt);
                return (
                  <CommandItem key={receipt.id} value={receipt.id} onSelect={() => choose(receipt)}>
                    <Check className={cn("mr-2 h-4 w-4", value === receipt.id ? "opacity-100" : "opacity-0")} />
                    <div className="flex flex-col">
                      <span>{option.label}</span>
                      <span className="text-xs text-muted-foreground">{option.sublabel}</span>
                    </div>
                  </CommandItem>
                );
              })}
            </CommandGroup>
          </CommandList>
        </Command>
        {picker.hasMore && !picker.nextPageFailed && (
          <Button type="button" variant="ghost" className="w-full" disabled={picker.isFetching} onClick={picker.loadMore}>
            {picker.isFetching ? "Loading more…" : "Load more receipts"}
          </Button>
        )}
        {(picker.isError || picker.nextPageFailed) && (
          <RequestFailure
            message={picker.nextPageFailed ? "More receipts could not be loaded. Loaded receipts remain available." : "Receipts are unavailable."}
            retry={picker.retry}
            isRetrying={picker.isFetching}
          />
        )}
        {picker.browseError && (
          <RequestFailure message="Loaded receipt history is unavailable. Location search may still work." retry={picker.retryBrowse} isRetrying={picker.browseFetching} />
        )}
        {search && !picker.complete && (
          <p role="status" className="px-3 py-2 text-xs text-muted-foreground">
            Searching loaded receipt dates and all receipt locations.
          </p>
        )}
      </PopoverContent>
    </Popover>
  );
});
