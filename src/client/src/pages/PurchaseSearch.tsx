import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Link, useSearchParams } from "react-router";
import { usePageTitle } from "@/hooks/usePageTitle";
import { usePurchaseSearch } from "@/hooks/usePurchaseSearch";
import { PageHead } from "@/components/primitives";
import { RequestFailure } from "@/components/RequestFailure";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { formatDate } from "@/lib/format";
import { formatPurchasePrice } from "@/lib/purchase-search";

const PAGE_SIZE = 25;
const searchSchema = z.object({
  q: z
    .string()
    .trim()
    .min(2, "Enter at least 2 characters.")
    .max(200, "Use at most 200 characters."),
});

function PurchaseQueryForm({
  initialQuery,
  onSearch,
}: {
  initialQuery: string;
  onSearch: (query: string) => void;
}) {
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<z.infer<typeof searchSchema>>({
    resolver: zodResolver(searchSchema),
    defaultValues: { q: initialQuery },
  });
  return (
    <form
      className="space-y-2"
      noValidate
      onSubmit={handleSubmit(({ q }) => onSearch(q))}
    >
      <div className="flex gap-2">
        <Input
          {...register("q")}
          aria-label="Search purchases"
          aria-invalid={!!errors.q}
          aria-describedby={errors.q ? "purchase-query-error" : undefined}
          placeholder="Item, item code, normalized name, or store"
        />
        <Button type="submit">Search</Button>
      </div>
      {errors.q && (
        <p
          id="purchase-query-error"
          role="alert"
          className="text-sm text-destructive"
        >
          {errors.q.message}
        </p>
      )}
    </form>
  );
}

export default function PurchaseSearch() {
  usePageTitle("Purchase history");
  const [params, setParams] = useSearchParams();
  const q = (params.get("q") ?? "").trim();
  const rawOffset = params.get("offset") ?? "0";
  const offset =
    /^\d+$/.test(rawOffset) &&
    Number.isSafeInteger(Number(rawOffset)) &&
    Number(rawOffset) <= 2147483647
      ? Number(rawOffset)
      : 0;
  const valid = q.length >= 2 && q.length <= 200;
  const { data, isPending, isError, isFetching, refetch } = usePurchaseSearch(
    q,
    offset,
    PAGE_SIZE,
  );
  const page = (nextOffset: number) =>
    setParams({ q, offset: String(nextOffset) });

  return (
    <div className="space-y-6">
      <PageHead
        title="Purchase history"
        sub="All time · Newest purchases first"
      />
      <PurchaseQueryForm
        key={q}
        initialQuery={q}
        onSearch={(value) => setParams({ q: value })}
      />
      <p className="text-sm text-muted-foreground">
        Find what you paid, where, and when. Search matches a phrase in item
        names, item codes, normalized names, or stores. Prices are recorded per
        item; package sizes and measurement units are not standardized.
      </p>
      {!valid ? (
        <p>
          Enter between 2 and 200 characters to search your purchase history.
        </p>
      ) : isError ? (
        <RequestFailure
          message="Purchase history could not be loaded."
          retry={() => void refetch()}
          isRetrying={isFetching}
        />
      ) : isPending ? (
        <p role="status">Searching purchases…</p>
      ) : (
        data && (
          <>
            <p role="status">
              {data.total.toLocaleString()} matching purchases for “{q}”
            </p>
            {data.items.length === 0 ? (
              <p>
                {offset > 0
                  ? "No purchases on this page. Return to the first page to see current results."
                  : "No purchases match this phrase. Try a shorter item name, code, or store."}
              </p>
            ) : (
              <div className="overflow-x-auto rounded-lg border">
                <table
                  className="w-full text-left text-sm"
                  aria-label="Purchase results"
                >
                  <thead className="bg-muted">
                    <tr>
                      {[
                        "Receipt date",
                        "Item",
                        "Store",
                        "Unit price",
                        "Quantity",
                        "Line total",
                        "Receipt",
                      ].map((heading) => (
                        <th
                          key={heading}
                          scope="col"
                          className="whitespace-nowrap px-4 py-3 font-medium"
                        >
                          {heading}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {data.items.map((item) => (
                      <tr key={item.id} className="border-t">
                        <td className="whitespace-nowrap px-4 py-3">
                          {formatDate(item.date)}
                        </td>
                        <td className="px-4 py-3">
                          <span className="font-medium">
                            {item.description}
                          </span>
                          {item.receiptItemCode && (
                            <div className="text-xs text-muted-foreground">
                              Code: {item.receiptItemCode}
                            </div>
                          )}
                          {item.normalizedDescription && (
                            <div className="text-xs text-muted-foreground">
                              Normalized: {item.normalizedDescription}
                            </div>
                          )}
                        </td>
                        <td className="px-4 py-3">{item.location}</td>
                        <td className="whitespace-nowrap px-4 py-3 font-mono">
                          {formatPurchasePrice(
                            item.unitPrice,
                            item.unitPriceCurrency,
                            true,
                          )}
                        </td>
                        <td className="px-4 py-3">{item.quantity}</td>
                        <td className="whitespace-nowrap px-4 py-3 font-mono">
                          {formatPurchasePrice(
                            item.totalAmount,
                            item.totalAmountCurrency,
                          )}
                        </td>
                        <td className="whitespace-nowrap px-4 py-3">
                          <Link
                            className="underline"
                            to={`/receipts/${item.receiptId}`}
                          >
                            View receipt
                          </Link>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            <nav
              aria-label="Purchase result pages"
              className="flex items-center gap-3"
            >
              {offset > 0 && (
                <Button variant="outline" onClick={() => page(0)}>
                  First page
                </Button>
              )}
              <Button
                variant="outline"
                disabled={offset === 0 || isFetching}
                onClick={() => page(Math.max(0, offset - PAGE_SIZE))}
              >
                Previous
              </Button>
              <span>
                {data.items.length > 0
                  ? `${offset + 1}–${offset + data.items.length} of ${data.total}`
                  : "No rows"}
              </span>
              <Button
                variant="outline"
                disabled={offset + PAGE_SIZE >= data.total || isFetching}
                onClick={() => page(offset + PAGE_SIZE)}
              >
                Next
              </Button>
            </nav>
          </>
        )
      )}
    </div>
  );
}
