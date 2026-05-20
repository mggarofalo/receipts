import { Fragment, useState, useMemo, useCallback } from "react";
import { generateId } from "@/lib/id";
import { Link } from "react-router";
import {
  useSubcategories,
  useSubcategoriesByCategoryId,
  useCreateSubcategory,
  useUpdateSubcategory,
  useDeleteSubcategory,
} from "@/hooks/useSubcategories";
import type { AffectedReceipt } from "@/hooks/useSubcategories";
import { usePermission } from "@/hooks/usePermission";
import { useAllCategories } from "@/hooks/useCategories";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useEntityLinkParams } from "@/hooks/useEntityLinkParams";
import { useOpenNewItem } from "@/hooks/useOpenNewItem";
import { useFuzzySearch } from "@/hooks/useFuzzySearch";
import { useSavedFilters } from "@/hooks/useSavedFilters";
import { useServerPagination } from "@/hooks/useServerPagination";
import { useServerSort } from "@/hooks/useServerSort";
import { useListKeyboardNav } from "@/hooks/useListKeyboardNav";
import type { FuseSearchConfig, FilterDefinition } from "@/lib/search";
import { applyFilters } from "@/lib/search";
import type { FilterValues } from "@/components/FilterPanel";
import { SubcategoryForm } from "@/components/SubcategoryForm";
import { FuzzySearchInput } from "@/components/FuzzySearchInput";
import { FilterPanel } from "@/components/FilterPanel";
import type { FilterField } from "@/components/FilterPanel";
import { SearchHighlight } from "@/components/SearchHighlight";
import { getMatchIndices } from "@/lib/search-highlight";
import { ActiveFilterBanner } from "@/components/ActiveFilterBanner";
import { SortableTableHead } from "@/components/SortableTableHead";
import { NoResults } from "@/components/NoResults";
import { Pagination } from "@/components/Pagination";
import { Button } from "@/components/ui/button";
import { Icon, PageHead } from "@/components/primitives";
import { Badge } from "@/components/ui/badge";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { TableSkeleton } from "@/components/ui/table-skeleton";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog";
import { ChevronDown, ChevronRight, Pencil, Trash2 } from "lucide-react";

interface SubcategoryResponse {
  id: string;
  name: string;
  categoryId: string;
  description?: string | null;
  isActive: boolean;
}

interface CategoryResponse {
  id: string;
  name: string;
}

const SEARCH_CONFIG: FuseSearchConfig<SubcategoryResponse> = {
  keys: [
    { name: "name", weight: 2 },
    { name: "description", weight: 1 },
  ],
};

const STATUS_STORAGE_KEY = "subcategories-status-filter";
type StatusFilter = "all" | "true" | "false";

const FILTER_PARAMS = ["categoryId"] as const;

function Subcategories() {
  usePageTitle("Subcategories");
  const { params: linkParams, clearParams, hasActiveFilter } = useEntityLinkParams(FILTER_PARAMS);
  const { sortBy, sortDirection, toggleSort } = useServerSort({ defaultSortBy: "name", defaultSortDirection: "asc" });
  const { offset, limit, currentPage, pageSize, totalPages, setPage, setPageSize, resetPage } = useServerPagination({ sortBy, sortDirection });
  const [statusFilter, setStatusFilter] = useState<StatusFilter>(() => {
    const saved = localStorage.getItem(STATUS_STORAGE_KEY);
    return saved === "all" || saved === "true" || saved === "false" ? saved : "true";
  });
  const isActiveParam = statusFilter === "all" ? undefined : statusFilter === "true";
  const allSubcatQuery = useSubcategories(offset, limit, sortBy, sortDirection, isActiveParam);
  const filteredSubcatQuery = useSubcategoriesByCategoryId(linkParams.categoryId ?? null, offset, limit, sortBy, sortDirection, isActiveParam);
  const activeSubcatQuery = linkParams.categoryId ? filteredSubcatQuery : allSubcatQuery;
  const { data: subcategoriesData, total: serverTotal, isLoading: subcategoriesLoading } = activeSubcatQuery;
  const { data: categoriesData, isLoading: categoriesLoading } = useAllCategories();
  const createSubcategory = useCreateSubcategory();
  const updateSubcategory = useUpdateSubcategory();
  const deleteSubcategory = useDeleteSubcategory();
  const { isAdmin } = usePermission();
  const isLoading = subcategoriesLoading || categoriesLoading;

  const [createOpen, setCreateOpen] = useState(false);
  const [editSubcategory, setEditSubcategory] =
    useState<SubcategoryResponse | null>(null);
  const [conflictData, setConflictData] = useState<{
    subcategoryName: string;
    receiptItemCount: number;
    affectedReceipts: AffectedReceipt[];
  } | null>(null);
  const [filterValues, setFilterValues] = useState<FilterValues>({
    categoryId: "all",
  });
  const [expandedCategories, setExpandedCategories] = useState<Set<string>>(
    new Set(),
  );

  const anyDialogOpen = createOpen || editSubcategory !== null || conflictData !== null;

  const openCreate = useCallback(() => setCreateOpen(true), []);
  useOpenNewItem(openCreate);

  const handleSort = useCallback((column: string) => {
    toggleSort(column);
    resetPage();
  }, [toggleSort, resetPage]);

  const data = (subcategoriesData as SubcategoryResponse[] | undefined) ?? [];

  const categoryList = useMemo(
    () => (categoriesData as CategoryResponse[] | undefined) ?? [],
    [categoriesData],
  );

  const categoryMap = useMemo(() => {
    const map = new Map<string, string>();
    for (const c of categoryList) {
      map.set(c.id, c.name);
    }
    return map;
  }, [categoryList]);

  const categoryFilterOptions = useMemo(
    () => categoryList.map((c) => c.name),
    [categoryList],
  );

  const filterFields: FilterField[] = useMemo(
    () => [
      {
        type: "select",
        key: "categoryId",
        label: "Category",
        options: categoryFilterOptions,
      },
    ],
    [categoryFilterOptions],
  );

  const filterDefs: FilterDefinition[] = useMemo(
    () => [{ key: "categoryId", type: "select", field: "categoryName" }],
    [],
  );

  const {
    filters: savedFilters,
    save: saveFilter,
    remove: removeFilter,
  } = useSavedFilters("subcategories");

  const { search, setSearch, results, totalCount, clearSearch } =
    useFuzzySearch({ data, config: SEARCH_CONFIG });

  function handleStatusChange(value: string) {
    const v = value as StatusFilter;
    setStatusFilter(v);
    localStorage.setItem(STATUS_STORAGE_KEY, v);
    resetPage();
  }

  const filteredResults = useMemo(() => {
    const items = results.map((r) => ({
      ...r.item,
      categoryName: categoryMap.get(r.item.categoryId) ?? "",
    }));
    return applyFilters(items, filterDefs, filterValues);
  }, [results, filterValues, categoryMap, filterDefs]);

  const matchMap = useMemo(() => {
    const map = new Map<string, (typeof results)[number]>();
    for (const r of results) {
      map.set(r.item.id, r);
    }
    return map;
  }, [results]);

  const groupedByCategory = useMemo(() => {
    const groups = new Map<string, SubcategoryResponse[]>();
    for (const item of filteredResults) {
      const existing = groups.get(item.categoryId);
      if (existing) {
        existing.push(item);
      } else {
        groups.set(item.categoryId, [item]);
      }
    }
    const sorted = [...groups.entries()].sort((a, b) => {
      const nameA = categoryMap.get(a[0]) ?? "";
      const nameB = categoryMap.get(b[0]) ?? "";
      return nameA.localeCompare(nameB);
    });
    return sorted;
  }, [filteredResults, categoryMap]);

  const visibleSubcategories = useMemo(
    () =>
      groupedByCategory.flatMap(([categoryId, items]) =>
        expandedCategories.has(categoryId) ? items : [],
      ),
    [groupedByCategory, expandedCategories],
  );

  function toggleCategory(categoryId: string) {
    setExpandedCategories((prev) => {
      const next = new Set(prev);
      if (next.has(categoryId)) next.delete(categoryId);
      else next.add(categoryId);
      return next;
    });
  }

  function expandAll() {
    setExpandedCategories(
      new Set(groupedByCategory.map(([categoryId]) => categoryId)),
    );
  }

  function collapseAll() {
    setExpandedCategories(new Set());
  }

  const { focusedId, setFocusedIndex, tableRef, containerProps, getRowProps } = useListKeyboardNav({
    items: visibleSubcategories,
    getId: (a) => a.id,
    listId: "subcategories",
    enabled: !anyDialogOpen,
    onOpen: (a) => setEditSubcategory(a),
  });

  if (isLoading) {
    return <TableSkeleton columns={5} />;
  }

  return (
    <>
      <PageHead
        title="Subcategories"
        sub={`${serverTotal} total${statusFilter === "all" ? "" : ` · ${statusFilter === "true" ? "active" : "inactive"}`}`}
        actions={
          <>
            <button type="button" className="btn sm" onClick={expandAll}>
              Expand all
            </button>
            <button type="button" className="btn sm" onClick={collapseAll}>
              Collapse all
            </button>
            <button
              type="button"
              className="btn primary"
              onClick={() => setCreateOpen(true)}
            >
              <Icon.Plus /> New subcategory
            </button>
          </>
        }
      />
      <div className="filter-strip">
        <div style={{ flex: 1, minWidth: 240 }}>
          <FuzzySearchInput
            aria-label="Search subcategories"
            value={search}
            onChange={setSearch}
            placeholder="Search subcategories…"
            resultCount={filteredResults.length}
            totalCount={totalCount}
          />
        </div>
      </div>

      <Tabs value={statusFilter} onValueChange={handleStatusChange}>
        <TabsList>
          <TabsTrigger value="true">Active</TabsTrigger>
          <TabsTrigger value="false">Inactive</TabsTrigger>
          <TabsTrigger value="all">All</TabsTrigger>
        </TabsList>
      </Tabs>

      <FilterPanel
        fields={filterFields}
        values={filterValues}
        onChange={setFilterValues}
        savedFilters={savedFilters}
        onSaveFilter={(name) =>
          saveFilter({
            id: generateId(),
            name,
            entityType: "subcategories",
            values: filterValues,
            createdAt: new Date().toISOString(),
          })
        }
        onDeleteFilter={removeFilter}
        onLoadFilter={(preset) =>
          setFilterValues(preset.values as FilterValues)
        }
      />

      {hasActiveFilter && (
        <ActiveFilterBanner
          message={`Showing subcategories for category: ${categoryMap.get(linkParams.categoryId!) ?? "Unknown category"}`}
          onClear={clearParams}
        />
      )}

      {filteredResults.length === 0 ? (
        search ? (
          <NoResults
            searchTerm={search}
            onClearSearch={clearSearch}
            onSelectSuggestion={setSearch}
            entityName="subcategories"
          />
        ) : (
          <div role="status" className="py-12 text-center text-muted-foreground">
            No subcategories yet. Create one to get started.
          </div>
        )
      ) : (
        <>
          <Pagination
            currentPage={currentPage}
            totalItems={serverTotal}
            pageSize={pageSize}
            totalPages={totalPages(serverTotal)}
            onPageChange={(page) => setPage(page, serverTotal)}
            onPageSizeChange={setPageSize}
          />
          <div className="rounded-md border" ref={tableRef} {...containerProps}>
            <Table>
              <TableHeader>
                <TableRow>
                  <SortableTableHead column="name" label="Name" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
                  <TableHead>Category</TableHead>
                  <TableHead>Description</TableHead>
                  <SortableTableHead column="isActive" label="Status" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
                  <TableHead className="w-24">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {groupedByCategory.map(([categoryId, items]) => {
                  const isExpanded = expandedCategories.has(categoryId);
                  const categoryName =
                    categoryMap.get(categoryId) ?? "Unknown";
                  return (
                    <Fragment key={categoryId}>
                      <TableRow
                        className="cursor-pointer bg-muted/50 hover:bg-muted"
                        onClick={() => toggleCategory(categoryId)}
                        data-testid={`category-header-${categoryId}`}
                      >
                        <TableCell colSpan={5}>
                          <div className="flex items-center gap-2 font-medium">
                            {isExpanded ? (
                              <ChevronDown className="h-4 w-4" />
                            ) : (
                              <ChevronRight className="h-4 w-4" />
                            )}
                            {categoryName}
                            <span className="ml-1 text-xs text-muted-foreground">
                              ({items.length})
                            </span>
                          </div>
                        </TableCell>
                      </TableRow>
                      {isExpanded &&
                        items.map((subcategory) => {
                          const visibleIndex =
                            visibleSubcategories.indexOf(subcategory);
                          const result = matchMap.get(subcategory.id);
                          const matches = result?.matches;
                          return (
                            <TableRow
                              key={subcategory.id}
                              {...getRowProps(subcategory.id)}
                              className={`cursor-pointer ${focusedId === subcategory.id ? "bg-accent" : ""}`}
                              onClick={(e) => {
                                if (
                                  (e.target as HTMLElement).closest(
                                    "button, input, a, [role='button']",
                                  )
                                )
                                  return;
                                setFocusedIndex(visibleIndex);
                              }}
                            >
                              <TableCell>
                                <SearchHighlight
                                  text={subcategory.name}
                                  indices={getMatchIndices(matches, "name")}
                                />
                              </TableCell>
                              <TableCell>
                                <Link to={`/categories?highlight=${subcategory.categoryId}`} className="text-primary hover:underline">
                                  {categoryMap.get(subcategory.categoryId) ?? "Unknown"}
                                </Link>
                              </TableCell>
                              <TableCell className="text-muted-foreground">
                                {subcategory.description ? (
                                  <SearchHighlight
                                    text={subcategory.description}
                                    indices={getMatchIndices(
                                      matches,
                                      "description",
                                    )}
                                  />
                                ) : (
                                  <span className="italic">--</span>
                                )}
                              </TableCell>
                              <TableCell>
                                <Badge
                                  variant={subcategory.isActive ? "default" : "secondary"}
                                >
                                  {subcategory.isActive ? "Active" : "Inactive"}
                                </Badge>
                              </TableCell>
                              <TableCell>
                                <div className="flex gap-1">
                                  <Button
                                    variant="ghost"
                                    size="icon"
                                    aria-label="Edit"
                                    onClick={() =>
                                      setEditSubcategory(subcategory)
                                    }
                                  >
                                    <Pencil className="h-4 w-4" />
                                  </Button>
                                  {isAdmin() && (
                                    <AlertDialog>
                                      <AlertDialogTrigger asChild>
                                        <Button
                                          variant="ghost"
                                          size="icon"
                                          aria-label="Delete"
                                        >
                                          <Trash2 className="h-4 w-4" />
                                        </Button>
                                      </AlertDialogTrigger>
                                      <AlertDialogContent>
                                        <AlertDialogHeader>
                                          <AlertDialogTitle>Delete Subcategory?</AlertDialogTitle>
                                          <AlertDialogDescription>
                                            This will soft-delete this subcategory. You can restore it from the Recycle Bin.
                                          </AlertDialogDescription>
                                        </AlertDialogHeader>
                                        <AlertDialogFooter>
                                          <AlertDialogCancel>Cancel</AlertDialogCancel>
                                          <AlertDialogAction
                                            variant="destructive"
                                            onClick={() => deleteSubcategory.mutate(subcategory.id, {
                                              onError: (error: unknown) => {
                                                const err = error as { conflict?: boolean; message?: string; receiptItemCount?: number; affectedReceipts?: AffectedReceipt[] };
                                                if (err.conflict && err.affectedReceipts) {
                                                  setConflictData({
                                                    subcategoryName: subcategory.name,
                                                    receiptItemCount: err.receiptItemCount ?? 0,
                                                    affectedReceipts: err.affectedReceipts,
                                                  });
                                                }
                                              },
                                            })}
                                          >
                                            Delete
                                          </AlertDialogAction>
                                        </AlertDialogFooter>
                                      </AlertDialogContent>
                                    </AlertDialog>
                                  )}
                                </div>
                              </TableCell>
                            </TableRow>
                          );
                        })}
                    </Fragment>
                  );
                })}
              </TableBody>
            </Table>
          </div>
          <Pagination
            currentPage={currentPage}
            totalItems={serverTotal}
            pageSize={pageSize}
            totalPages={totalPages(serverTotal)}
            onPageChange={(page) => setPage(page, serverTotal)}
            onPageSizeChange={setPageSize}
          />
        </>
      )}

      {/* Create Dialog */}
      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Create Subcategory</DialogTitle>
          </DialogHeader>
          <SubcategoryForm
            mode="create"
            isSubmitting={createSubcategory.isPending}
            onCancel={() => setCreateOpen(false)}
            onSubmit={(values) => {
              createSubcategory.mutate(values, {
                onSuccess: () => setCreateOpen(false),
              });
            }}
          />
        </DialogContent>
      </Dialog>

      {/* Edit Dialog */}
      <Dialog
        open={editSubcategory !== null}
        onOpenChange={(open) => !open && setEditSubcategory(null)}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Edit Subcategory</DialogTitle>
          </DialogHeader>
          {editSubcategory && (
            <SubcategoryForm
              mode="edit"
              defaultValues={{
                name: editSubcategory.name,
                categoryId: editSubcategory.categoryId,
                description: editSubcategory.description ?? "",
                isActive: editSubcategory.isActive,
              }}
              isSubmitting={updateSubcategory.isPending}
              onCancel={() => setEditSubcategory(null)}
              onSubmit={(values) => {
                updateSubcategory.mutate(
                  { id: editSubcategory.id, ...values },
                  { onSuccess: () => setEditSubcategory(null) },
                );
              }}
            />
          )}
        </DialogContent>
      </Dialog>

      {/* Conflict Dialog — shown when deletion is blocked by receipt items */}
      <Dialog
        open={conflictData !== null}
        onOpenChange={(open) => !open && setConflictData(null)}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Cannot Delete Subcategory</DialogTitle>
          </DialogHeader>
          {conflictData && (
            <div className="space-y-4">
              <p className="text-sm text-muted-foreground">
                <strong>{conflictData.subcategoryName}</strong> cannot be deleted because{" "}
                {conflictData.receiptItemCount} receipt item(s) use this subcategory.
                Re-categorize the items on these receipts first:
              </p>
              <ul className="max-h-64 space-y-1 overflow-y-auto text-sm">
                {conflictData.affectedReceipts.map((receipt) => (
                  <li key={receipt.id}>
                    <Link
                      to={`/receipts/${receipt.id}`}
                      className="text-primary hover:underline"
                      onClick={() => setConflictData(null)}
                    >
                      {receipt.date} &mdash; {receipt.location}
                    </Link>
                  </li>
                ))}
              </ul>
              {conflictData.receiptItemCount > conflictData.affectedReceipts.length && (
                <p className="text-xs text-muted-foreground">
                  and {conflictData.receiptItemCount - conflictData.affectedReceipts.length} more receipt(s)
                </p>
              )}
              <div className="flex justify-end">
                <Button variant="outline" onClick={() => setConflictData(null)}>
                  Close
                </Button>
              </div>
            </div>
          )}
        </DialogContent>
      </Dialog>

    </>
  );
}

export default Subcategories;
