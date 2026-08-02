import { useState, useMemo } from "react";
import { useNavigate } from "react-router";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import {
  useUncategorizedItemsReport,
  type UncategorizedItemsParams,
} from "@/hooks/useUncategorizedItemsReport";
import { useAllCategories } from "@/hooks/useCategories";
import { useAllSubcategoriesByCategoryId } from "@/hooks/useSubcategories";
import { useCsvExport } from "@/hooks/useCsvExport";
import { useReportSearchParams } from "@/hooks/useReportSearchParams";
import { csvFilename } from "@/lib/export-csv";
import { fetchAllReportPages } from "@/lib/fetch-all-report-pages";
import { formatCurrency } from "@/lib/format";
import { parseEnumParam, parsePositiveIntParam } from "@/lib/report-params";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Skeleton } from "@/components/ui/skeleton";
import { Combobox, type ComboboxOption } from "@/components/ui/combobox";
import { SortableTableHead } from "@/components/SortableTableHead";
import client from "@/lib/api-client";
import { toast } from "sonner";

type SortColumn = "description" | "total" | "itemCode";
type SortDirection = "asc" | "desc";

const SORT_COLUMNS = ["description", "total", "itemCode"] as const;
const SORT_DIRECTIONS = ["asc", "desc"] as const;

interface UncategorizedItemsUrlParams {
  sortBy: SortColumn;
  sortDirection: SortDirection;
  page: number;
}

function parseUncategorizedItemsParams(
  searchParams: URLSearchParams,
): UncategorizedItemsUrlParams {
  return {
    sortBy: parseEnumParam(
      searchParams.get("sortBy"),
      SORT_COLUMNS,
      "description",
    ),
    sortDirection: parseEnumParam(
      searchParams.get("sortDirection"),
      SORT_DIRECTIONS,
      "asc",
    ),
    page: parsePositiveIntParam(searchParams.get("page"), 1),
  };
}

interface UncategorizedItemData {
  id: string;
  receiptId: string;
  receiptItemCode?: string | null;
  description: string;
  quantity: number;
  unitPrice: number;
  totalAmount: number;
  category: string;
  subcategory?: string | null;
}

export default function UncategorizedItems() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [urlParams, updateParams] = useReportSearchParams(
    parseUncategorizedItemsParams,
  );
  const { sortBy, sortDirection, page } = urlParams;
  const pageSize = 50;
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [selectedCategory, setSelectedCategory] = useState("");
  const [selectedSubcategory, setSelectedSubcategory] = useState("");

  const params: UncategorizedItemsParams = {
    sortBy,
    sortDirection,
    page,
    pageSize,
  };

  const { data, isLoading, isError } = useUncategorizedItemsReport(params);
  const { exportCsv, isExporting } = useCsvExport();
  const { data: categories, isLoading: categoriesLoading } = useAllCategories(true);

  function handleExport() {
    exportCsv({
      filename: csvFilename("uncategorized-items"),
      headers: [
        "Description",
        "Item Code",
        "Quantity",
        "Unit Price",
        "Total",
        "Subcategory",
        "Receipt ID",
      ],
      rows: async () => {
        const items = await fetchAllReportPages(
          async (exportPage, exportPageSize) => {
            const { data: pageData, error } = await client.GET(
              "/api/reports/uncategorized-items",
              {
                params: {
                  query: {
                    sortBy,
                    sortDirection,
                    page: exportPage,
                    pageSize: exportPageSize,
                  },
                },
              },
            );
            if (error) throw error;
            return {
              items: pageData?.items ?? [],
              totalCount: Number(pageData?.totalCount ?? 0),
            };
          },
        );
        return items.map((item) => [
          item.description,
          item.receiptItemCode,
          item.quantity,
          item.unitPrice,
          item.totalAmount,
          item.subcategory,
          item.receiptId,
        ]);
      },
    });
  }

  const categoryOptions: ComboboxOption[] = useMemo(
    () =>
      (categories ?? [])
        .filter((c) => c.name !== "Uncategorized")
        .map((c) => ({ value: c.name, label: c.name })),
    [categories],
  );

  const selectedCategoryId = useMemo(() => {
    const found = (categories ?? []).find((c) => c.name === selectedCategory);
    return found?.id ?? null;
  }, [categories, selectedCategory]);

  const { data: subcategories, isLoading: subcategoriesLoading } =
    useAllSubcategoriesByCategoryId(selectedCategoryId, true);

  const subcategoryOptions: ComboboxOption[] = useMemo(
    () =>
      (subcategories ?? []).map((s) => ({
        value: s.name,
        label: s.name,
      })),
    [subcategories],
  );

  const bulkUpdateMutation = useMutation({
    mutationFn: async ({
      items,
      category,
      subcategory,
    }: {
      items: UncategorizedItemData[];
      category: string;
      subcategory: string | null;
    }) => {
      const grouped = new Map<string, UncategorizedItemData[]>();
      for (const item of items) {
        const group = grouped.get(item.receiptId) ?? [];
        group.push(item);
        grouped.set(item.receiptId, group);
      }

      const promises = Array.from(grouped.values()).map((groupItems) =>
        client.PUT("/api/receipt-items/batch", {
          body: groupItems.map((item) => ({
            id: item.id,
            receiptItemCode: item.receiptItemCode ?? null,
            description: item.description,
            quantity: item.quantity,
            unitPrice: item.unitPrice,
            category,
            subcategory: subcategory || null,
          })),
        }),
      );

      const results = await Promise.all(promises);
      for (const result of results) {
        if (result.error) throw result.error;
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: ["reports", "uncategorized-items"],
      });
      queryClient.invalidateQueries({ queryKey: ["receipt-items"] });
      setSelectedIds(new Set());
      setSelectedCategory("");
      setSelectedSubcategory("");
      toast.success("Items categorized successfully");
    },
    onError: () => {
      toast.error("Failed to update items");
    },
  });

  function handleSort(column: string) {
    const nextColumn = column as SortColumn;
    if (sortBy === nextColumn) {
      updateParams({
        sortDirection: sortDirection === "asc" ? "desc" : "asc",
        page: 1,
      });
    } else {
      updateParams({ sortBy: nextColumn, sortDirection: "asc", page: 1 });
    }
  }

  function handleReceiptClick(e: React.MouseEvent, receiptId: string) {
    e.stopPropagation();
    navigate(`/receipts/${receiptId}`);
  }

  function handleToggleItem(id: string) {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }

  function handleToggleAll() {
    if (!data?.items) return;
    setSelectedIds((prev) => {
      const allOnPage = data.items.map((item) => item.id);
      const allSelected = allOnPage.every((id) => prev.has(id));
      if (allSelected) {
        const next = new Set(prev);
        for (const id of allOnPage) next.delete(id);
        return next;
      } else {
        const next = new Set(prev);
        for (const id of allOnPage) next.add(id);
        return next;
      }
    });
  }

  function handleApply() {
    if (!data?.items || selectedIds.size === 0 || !selectedCategory) return;

    const itemsToUpdate = data.items.filter((item) =>
      selectedIds.has(item.id),
    ) as UncategorizedItemData[];

    bulkUpdateMutation.mutate({
      items: itemsToUpdate,
      category: selectedCategory,
      subcategory: selectedSubcategory || null,
    });
  }

  function handleCategoryChange(value: string) {
    setSelectedCategory(value);
    setSelectedSubcategory("");
  }

  const totalPages = data ? Math.ceil(Number(data.totalCount ?? 0) / pageSize) : 0;
  const allOnPageSelected =
    data?.items &&
    data.items.length > 0 &&
    data.items.every((item) => selectedIds.has(item.id));

  if (isLoading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-20 w-full rounded-lg" />
        <Skeleton className="h-64 w-full rounded-lg" />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="rounded-lg border border-destructive p-6 text-center">
        <p className="text-destructive">
          Failed to load uncategorized items report.
        </p>
      </div>
    );
  }

  if (!data || data.totalCount === 0) {
    return (
      <div className="rounded-lg border p-6 text-center">
        <h2 className="card-title">All Categorized</h2>
        <p className="mt-2 text-muted-foreground">
          All receipt items have been categorized. No uncategorized items found.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-6 rounded-lg border p-4">
        <div>
          <p className="card-sub">Uncategorized Items</p>
          <p className="money-med">{data.totalCount}</p>
        </div>
        <Button
          variant="outline"
          size="sm"
          className="ml-auto"
          disabled={isExporting}
          onClick={handleExport}
        >
          {isExporting ? "Exporting..." : "Export CSV"}
        </Button>
      </div>

      {selectedIds.size > 0 && (
        <div className="flex items-center gap-3 rounded-lg border bg-muted/50 p-3">
          <span className="text-sm font-medium">
            {selectedIds.size} selected
          </span>
          <Combobox
            options={categoryOptions}
            value={selectedCategory}
            onValueChange={handleCategoryChange}
            placeholder="Select category..."
            searchPlaceholder="Search categories..."
            loading={categoriesLoading}
            className="w-48"
          />
          <Combobox
            options={subcategoryOptions}
            value={selectedSubcategory}
            onValueChange={setSelectedSubcategory}
            placeholder="Subcategory (optional)"
            searchPlaceholder="Search subcategories..."
            disabled={!selectedCategory}
            loading={subcategoriesLoading}
            className="w-48"
          />
          <Button
            size="sm"
            disabled={
              !selectedCategory ||
              selectedIds.size === 0 ||
              bulkUpdateMutation.isPending
            }
            onClick={handleApply}
          >
            {bulkUpdateMutation.isPending ? "Applying..." : "Apply to Selected"}
          </Button>
        </div>
      )}

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead className="w-10">
              <Checkbox
                checked={!!allOnPageSelected}
                onCheckedChange={handleToggleAll}
                aria-label="Select all items on this page"
              />
            </TableHead>
            <SortableTableHead column="description" label="Description" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
            <SortableTableHead column="itemCode" label="Item Code" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
            <TableHead>Receipt</TableHead>
            <SortableTableHead column="total" label="Total" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} className="text-right" align="right" />
            <TableHead>Subcategory</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {data.items.map((item) => (
            <TableRow key={item.id}>
              <TableCell>
                <Checkbox
                  checked={selectedIds.has(item.id)}
                  onCheckedChange={() => handleToggleItem(item.id)}
                  aria-label={`Select ${item.description}`}
                />
              </TableCell>
              <TableCell>{item.description}</TableCell>
              <TableCell>{item.receiptItemCode ?? "-"}</TableCell>
              <TableCell>
                <button
                  type="button"
                  className="text-primary underline-offset-4 hover:underline"
                  onClick={(e) => handleReceiptClick(e, item.receiptId)}
                >
                  View
                </button>
              </TableCell>
              <TableCell className="text-right money">
                {formatCurrency(Number(item.totalAmount ?? 0))}
              </TableCell>
              <TableCell className="text-muted-foreground">
                {item.subcategory ?? "-"}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>

      {totalPages > 1 && (
        <div className="flex items-center justify-between">
          <p className="text-sm text-muted-foreground">
            Page {page} of {totalPages}
          </p>
          <div className="flex gap-2">
            <Button
              variant="outline"
              size="sm"
              disabled={page <= 1}
              onClick={() => updateParams({ page: page - 1 })}
            >
              Previous
            </Button>
            <Button
              variant="outline"
              size="sm"
              disabled={page >= totalPages}
              onClick={() => updateParams({ page: page + 1 })}
            >
              Next
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
