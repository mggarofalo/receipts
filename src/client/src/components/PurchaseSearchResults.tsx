import { useNavigate } from "react-router";
import { usePurchaseSearch } from "@/hooks/usePurchaseSearch";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { CommandGroup, CommandItem } from "@/components/ui/command";
import { RequestFailure } from "@/components/RequestFailure";
import { formatDate } from "@/lib/format";
import { formatPurchasePrice, purchaseSearchHref } from "@/lib/purchase-search";

export function PurchaseSearchResults({
  query,
  open,
  close,
}: {
  query: string;
  open: boolean;
  close: () => void;
}) {
  const navigate = useNavigate();
  const search = query.trim();
  const debounced = useDebouncedValue(search, 200);
  const current = search === debounced;
  const valid = search.length >= 2 && search.length <= 200;
  const { data, isPending, isError, isFetching, refetch } = usePurchaseSearch(
    debounced,
    0,
    5,
    open && current && valid,
  );
  if (!open || !valid) return null;
  const go = (href: string) => {
    close();
    navigate(href);
  };
  // cmdk orders forced items by registration. Register each result snapshot
  // together so asynchronous rows stay in server order ahead of the full-table action.
  return (
    <CommandGroup
      key={`${debounced}:${current && data && !isError ? data.items.map((item) => item.id).join(",") : "pending"}`}
      heading="Purchase history"
      forceMount
    >
      {!current || isPending ? (
        <p className="px-2 py-3 text-sm" role="status">
          Searching purchases…
        </p>
      ) : isError ? (
        <RequestFailure
          message="Purchase search unavailable."
          retry={() => void refetch()}
          isRetrying={isFetching}
        />
      ) : (
        data?.items.map((item) => (
          <CommandItem
            key={item.id}
            value={`purchase:${item.id}`}
            forceMount
            onSelect={() => go(`/receipts/${item.receiptId}`)}
          >
            <div className="min-w-0">
              <div className="truncate font-medium">{item.description}</div>
              <div className="text-xs text-muted-foreground">
                {formatPurchasePrice(
                  item.unitPrice,
                  item.unitPriceCurrency,
                  true,
                )}{" "}
                unit price · {formatDate(item.date)} · {item.location}
              </div>
            </div>
          </CommandItem>
        ))
      )}
      <CommandItem
        value="purchase-search-all"
        forceMount
        onSelect={() => go(purchaseSearchHref(search))}
      >
        {current && data && !isError
          ? `See all ${data.total.toLocaleString()} matching purchases`
          : "Search all purchase history"}
      </CommandItem>
    </CommandGroup>
  );
}
