import { useEffect } from "react";
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
import { extractErrorMessage } from "@/lib/problem-details";

import { Pagination } from "@/components/Pagination";
import { SortableTableHead } from "@/components/SortableTableHead";
import { PurchaseFilters } from "@/components/PurchaseFilters";
import { CategoryPathPicker } from "@/components/categories/CategoryPathPicker";
import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import { categoryPaths } from "@/lib/category-paths";
import { parseCategoryViewParam } from "@/lib/report-params";
import { usePersistedTablePageSize } from "@/hooks/usePersistedTablePageSize";
import { isPageSize } from "@/lib/page-size";
import {
  PURCHASE_SORT_COLUMNS,
  purchaseFiltersSchema,
  readPurchaseFilters,
  purchaseFilterOptions,
  type PurchaseSortColumn,
} from "@/lib/purchase-table";
const searchSchema = z.object({
  q: z.string().trim().max(200, "Use at most 200 characters."),
});

function PurchaseQueryForm({
  initialQuery,
  onSearch,
  allowEmpty,
}: {
  initialQuery: string;
  onSearch: (query: string) => void;
  allowEmpty: boolean;
}) {
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<z.infer<typeof searchSchema>>({
    resolver: zodResolver(searchSchema),
    defaultValues: { q: initialQuery },
  });
  return (
    <form
      className="space-y-2"
      noValidate
      onSubmit={handleSubmit(({ q }) => {
        if (!allowEmpty && q.length < 2) {
          setError("q", { message: "Enter at least 2 characters." });
          return;
        }
        onSearch(q);
      })}
    >
      <div className="flex gap-2">
        <Input
          {...register("q")}
          aria-label="Search purchases"
          aria-invalid={!!errors.q}
          aria-describedby={errors.q ? "purchase-query-error" : undefined}
          placeholder="Search item names, codes, or stores"
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
  const normalizedDescriptionId =
    params.get("normalizedDescriptionId") ?? undefined;
  const taxonomy = useCategoryTaxonomy();
  const categoryView = parseCategoryViewParam(params);
  const categoryId = params.get("categoryId") || undefined;
  const selectedCategory = categoryPaths(taxonomy.data?.nodes ?? []).find(
    (row) => row.node.id === categoryId,
  );
  const rawCutoff = params.get("knowledgeCutoff");
  const knowledgeCutoff =
    rawCutoff &&
    /^\d{4}-\d{2}-\d{2}T.*Z$/.test(rawCutoff) &&
    Number.isFinite(Date.parse(rawCutoff))
      ? new Date(rawCutoff).toISOString()
      : undefined;
  const capturedCurrentRevisionId =
    categoryView === "currentHierarchy"
      ? params.get("capturedCurrentRevisionId") || undefined
      : undefined;
  const { pageSize: preferredPageSize, setPageSize } =
    usePersistedTablePageSize("purchase-history");
  const requestedSize = Number(params.get("limit"));
  const pageSize = isPageSize(requestedSize)
    ? requestedSize
    : preferredPageSize;
  const rawOffset = params.get("offset") ?? "0";
  const parsedOffset =
    /^\d+$/.test(rawOffset) &&
    Number.isSafeInteger(Number(rawOffset)) &&
    Number(rawOffset) <= 2147483647
      ? Number(rawOffset)
      : 0;
  const offset = Math.floor(parsedOffset / pageSize) * pageSize;
  const rawSort = params.get("sortBy");
  const sortBy: PurchaseSortColumn = PURCHASE_SORT_COLUMNS.includes(
    rawSort as PurchaseSortColumn,
  )
    ? (rawSort as PurchaseSortColumn)
    : "date";
  const sortDirection = params.get("sortDirection") === "asc" ? "asc" : "desc";
  const filters = readPurchaseFilters(params);
  const parsedFilters = purchaseFiltersSchema.safeParse(filters);
  const valid =
    (q.length === 0 && !!(normalizedDescriptionId || categoryId)) ||
    (q.length >= 2 && q.length <= 200);
  const { data, error, isPending, isError, isFetching, refetch } =
    usePurchaseSearch(q, offset, pageSize, valid && parsedFilters.success, {
      ...purchaseFilterOptions(
        parsedFilters.success
          ? parsedFilters.data
          : {
              location: "",
              startDate: "",
              endDate: "",
              minUnitPrice: "",
              maxUnitPrice: "",
            },
      ),
      sortBy,
      sortDirection,
      includeUnreviewed: params.get("includeUnreviewed") === "true",
      normalizedDescriptionId,
      categoryId,
      categoryView,
      knowledgeCutoff,
      capturedCurrentRevisionId,
    });
  // Pin the taxonomy used by a loaded current-hierarchy page so later pages
  // retain the same meaning if the hierarchy changes in another session.
  useEffect(() => {
    if (
      categoryView !== "currentHierarchy" ||
      capturedCurrentRevisionId ||
      !data?.currentRevisionId ||
      isPending ||
      isError
    )
      return;
    setParams(
      (previous) => {
        if (previous.get("capturedCurrentRevisionId")) return previous;
        const next = new URLSearchParams(previous);
        next.set("capturedCurrentRevisionId", data.currentRevisionId!);
        return next;
      },
      { replace: true },
    );
  }, [
    categoryView,
    capturedCurrentRevisionId,
    data?.currentRevisionId,
    isPending,
    isError,
    setParams,
  ]);
  function update(updates: Record<string, string>, resetPage = true) {
    setParams((previous) => {
      const next = new URLSearchParams(previous);
      if (resetPage) next.delete("offset");
      for (const [key, value] of Object.entries(updates)) {
        if (value) next.set(key, value);
        else next.delete(key);
      }
      return next;
    });
  }
  const page = (nextOffset: number) =>
    update({ offset: nextOffset ? String(nextOffset) : "" }, false);
  function toggleSort(column: string) {
    update({
      sortBy: column,
      sortDirection:
        column === sortBy && sortDirection === "asc" ? "desc" : "asc",
    });
  }

  return (
    <div className="space-y-6">
      <PageHead title="Purchase history" />
      <PurchaseQueryForm
        key={q}
        initialQuery={q}
        allowEmpty={!!(categoryId || normalizedDescriptionId)}
        onSearch={(value) =>
          update({
            q: value,
            normalizedDescriptionId: value
              ? ""
              : (normalizedDescriptionId ?? ""),
          })
        }
      />
      <section
        aria-label="Category search"
        className="space-y-3 rounded-lg border bg-card p-4"
      >
        <div className="grid gap-3 sm:grid-cols-2">
          <div className="space-y-1">
            <label className="text-sm font-medium" htmlFor="purchase-category">
              Category
            </label>
            <CategoryPathPicker
              id="purchase-category"
              aria-label="Category"
              revision={taxonomy.data}
              loading={taxonomy.isLoading}
              error={taxonomy.isError}
              value={
                selectedCategory && taxonomy.data
                  ? {
                      categoryId: selectedCategory.node.id,
                      taxonomyRevisionId: taxonomy.data.id,
                      path: selectedCategory.path,
                    }
                  : undefined
              }
              onValueChange={(choice) =>
                update({
                  categoryId: choice.categoryId ?? "",
                  capturedCurrentRevisionId: "",
                })
              }
            />
            {categoryId &&
              !taxonomy.isLoading &&
              !taxonomy.isError &&
              !selectedCategory && (
                <p role="status" className="text-sm text-muted-foreground">
                  Selected category is no longer in the current taxonomy.
                  Results still use its saved ID.
                </p>
              )}
            {taxonomy.isError && (
              <p role="status" className="text-sm text-muted-foreground">
                Categories could not be loaded. Existing search filters still
                work.
              </p>
            )}
            <p className="text-xs text-muted-foreground">
              Includes subcategories.
            </p>
            {categoryId && (
              <Button
                variant="link"
                className="h-auto p-0"
                onClick={() =>
                  update({ categoryId: "", capturedCurrentRevisionId: "" })
                }
              >
                Clear category
              </Button>
            )}
          </div>
          <div className="space-y-1">
            <label
              htmlFor="purchase-category-view"
              className="text-sm font-medium"
            >
              Category view
            </label>
            <select
              id="purchase-category-view"
              className="flex h-9 w-full rounded-md border bg-background px-3 text-sm"
              value={categoryView}
              onChange={(event) =>
                update({
                  categoryView: event.target.value,
                  capturedCurrentRevisionId: "",
                })
              }
            >
              <option value="asClassified">As classified at the time</option>
              <option value="currentHierarchy">Current hierarchy</option>
            </select>
          </div>
        </div>
        <details>
          <summary className="cursor-pointer text-sm">
            Historical assignment cutoff
          </summary>
          <div className="mt-2 flex flex-wrap items-end gap-2">
            <label
              htmlFor="purchase-knowledge-cutoff"
              className="space-y-1 text-sm"
            >
              Assignments known by (UTC)
              <Input
                id="purchase-knowledge-cutoff"
                type="datetime-local"
                value={knowledgeCutoff?.slice(0, 16) ?? ""}
                onChange={(event) =>
                  update({
                    knowledgeCutoff: event.target.value
                      ? new Date(`${event.target.value}Z`).toISOString()
                      : "",
                  })
                }
              />
            </label>
            {knowledgeCutoff && (
              <Button
                variant="outline"
                onClick={() => update({ knowledgeCutoff: "" })}
              >
                Clear cutoff
              </Button>
            )}
          </div>
        </details>
      </section>
      {normalizedDescriptionId && (
        <p className="text-sm">
          Filtered to one product identity.{" "}
          <Button
            variant="link"
            onClick={() => update({ normalizedDescriptionId: "" })}
          >
            Clear identity filter
          </Button>
        </p>
      )}
      <p className="text-sm text-muted-foreground">
        Prices are recorded per item; package sizes and measurement units are
        not standardized.
      </p>
      <label className="flex items-center gap-2 text-sm">
        <input
          type="checkbox"
          checked={params.get("includeUnreviewed") === "true"}
          onChange={(event) =>
            update({ includeUnreviewed: event.target.checked ? "true" : "" })
          }
        />
        Include unreviewed product associations
      </label>
      <PurchaseFilters
        key={JSON.stringify(filters)}
        initialValues={filters}
        onApply={(values) => update(values)}
      />
      {!parsedFilters.success && (
        <p role="alert">
          Some filters are invalid. Adjust them and apply again.
        </p>
      )}
      {!valid || !parsedFilters.success ? null : isError ? (
        <RequestFailure
          message={
            extractErrorMessage(error) ??
            "Purchase history could not be loaded."
          }
          retry={() => void refetch()}
          isRetrying={isFetching}
        />
      ) : isPending ? (
        <p role="status">Searching purchases…</p>
      ) : (
        data && (
          <>
            <p role="status">
              {data.total.toLocaleString()} matching purchases
              {q
                ? ` for “${q}”`
                : categoryId
                  ? " in this category"
                  : " for this product identity"}
            </p>
            {data.items.length === 0 ? (
              <p>
                {offset > 0
                  ? "No purchases on this page. Return to the first page to see current results."
                  : "No purchases match these filters. Try another category, item name, code, or store."}
              </p>
            ) : (
              <div className="overflow-x-auto rounded-lg border">
                <table
                  className="w-full text-left text-sm"
                  aria-label="Purchase results"
                >
                  <thead className="bg-muted">
                    <tr>
                      {(
                        [
                          ["date", "Receipt date"],
                          ["description", "Item"],
                          ["location", "Store"],
                          ["unitPrice", "Unit price"],
                          ["quantity", "Quantity"],
                          ["totalAmount", "Line total"],
                        ] as const
                      ).map(([column, label]) => (
                        <SortableTableHead
                          key={column}
                          column={column}
                          label={label}
                          currentSortBy={sortBy}
                          currentSortDirection={sortDirection}
                          onToggleSort={toggleSort}
                          className="px-4 py-3"
                        />
                      ))}
                      <th scope="col" className="px-4 py-3 font-medium">
                        Receipt
                      </th>
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
                          <div className="text-xs text-muted-foreground">
                            {item.normalizationReviewState === "verified"
                              ? "Verified"
                              : item.normalizationReviewState === "excluded"
                                ? "Excluded"
                                : "Needs review"}
                          </div>
                          {item.normalizedDescription && (
                            <div className="text-xs text-muted-foreground">
                              {item.normalizationReviewState === "verified"
                                ? "Verified product"
                                : "Unreviewed association"}
                              : {item.normalizedDescription}
                            </div>
                          )}
                          <div className="text-xs text-muted-foreground">
                            {item.categoryState === "classified" &&
                            item.categoryPath.length > 0
                              ? item.categoryPath
                                  .map((node) => node.name)
                                  .join(" / ")
                              : item.categoryState === "unclassified"
                                ? "Unclassified"
                                : item.categoryState === "missingAssignment"
                                  ? "No category assignment"
                                  : item.categoryState ===
                                      "beforeMigrationHistory"
                                    ? "Category history unavailable before migration"
                                    : item.categoryState === "unresolvedLegacy"
                                      ? "Legacy category unresolved"
                                      : "Category path unresolved"}
                          </div>
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
            <nav aria-label="Purchase result pages" className="space-y-3">
              {offset > 0 && (
                <Button variant="outline" onClick={() => page(0)}>
                  First page
                </Button>
              )}
              <Pagination
                currentPage={Math.floor(offset / pageSize) + 1}
                totalItems={data.total}
                pageSize={pageSize}
                totalPages={Math.max(1, Math.ceil(data.total / pageSize))}
                disabled={isFetching}
                onPageChange={(nextPage) => page((nextPage - 1) * pageSize)}
                onPageSizeChange={(size) => {
                  setPageSize(size);
                  update({ limit: String(size) });
                }}
              />
            </nav>
          </>
        )
      )}
    </div>
  );
}
