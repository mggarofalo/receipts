import { useState, useMemo, useCallback } from "react";
import { Link } from "react-router";
import {
  useCategories,
  useCreateCategory,
  useUpdateCategory,
  useDeleteCategory,
} from "@/hooks/useCategories";
import { usePermission } from "@/hooks/usePermission";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useEntityLinkParams } from "@/hooks/useEntityLinkParams";
import { useOpenNewItem } from "@/hooks/useOpenNewItem";
import { useFuzzySearch } from "@/hooks/useFuzzySearch";
import { useSavedFilters } from "@/hooks/useSavedFilters";
import { useServerPagination } from "@/hooks/useServerPagination";
import { useServerSort } from "@/hooks/useServerSort";
import { useListKeyboardNav } from "@/hooks/useListKeyboardNav";
import type { FuseSearchConfig } from "@/lib/search";
import { CategoryForm } from "@/components/CategoryForm";
import { FuzzySearchInput } from "@/components/FuzzySearchInput";
import { SearchHighlight } from "@/components/SearchHighlight";
import { getMatchIndices } from "@/lib/search-highlight";
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
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Info, Pencil, Trash2 } from "lucide-react";

interface CategoryResponse {
  id: string;
  name: string;
  description?: string | null;
  isActive: boolean;
}

const SEARCH_CONFIG: FuseSearchConfig<CategoryResponse> = {
  keys: [
    { name: "name", weight: 2 },
    { name: "description", weight: 1 },
  ],
};

const STATUS_STORAGE_KEY = "categories-status-filter";
type StatusFilter = "all" | "true" | "false";

const HIGHLIGHT_PARAMS = ["highlight"] as const;

function Categories() {
  usePageTitle("Categories");
  const { params: linkParams } = useEntityLinkParams(HIGHLIGHT_PARAMS);
  const { sortBy, sortDirection, toggleSort } = useServerSort({ defaultSortBy: "name", defaultSortDirection: "asc" });
  const { offset, limit, currentPage, pageSize, totalPages, setPage, setPageSize, resetPage } = useServerPagination({ sortBy, sortDirection });
  const [statusFilter, setStatusFilter] = useState<StatusFilter>(() => {
    const saved = localStorage.getItem(STATUS_STORAGE_KEY);
    return saved === "all" || saved === "true" || saved === "false" ? saved : "true";
  });
  const isActiveParam = statusFilter === "all" ? undefined : statusFilter === "true";
  const { data: categoriesData, total: serverTotal, isLoading } = useCategories(offset, limit, sortBy, sortDirection, isActiveParam);
  const createCategory = useCreateCategory();
  const updateCategory = useUpdateCategory();
  const deleteCategory = useDeleteCategory();
  const { isAdmin } = usePermission();
  const [createOpen, setCreateOpen] = useState(false);
  const [editCategory, setEditCategory] = useState<CategoryResponse | null>(
    null,
  );

  const anyDialogOpen = createOpen || editCategory !== null;

  const openCreate = useCallback(() => setCreateOpen(true), []);
  useOpenNewItem(openCreate);

  const handleSort = useCallback((column: string) => {
    toggleSort(column);
    resetPage();
  }, [toggleSort, resetPage]);

  const data = (categoriesData as CategoryResponse[] | undefined) ?? [];
  useSavedFilters("categories");

  const { search, setSearch, results, totalCount, clearSearch } =
    useFuzzySearch({ data, config: SEARCH_CONFIG });

  function handleStatusChange(value: string) {
    const v = value as StatusFilter;
    setStatusFilter(v);
    localStorage.setItem(STATUS_STORAGE_KEY, v);
    resetPage();
  }

  const filteredResults = useMemo(() => {
    return results.map((r) => r.item);
  }, [results]);

  const matchMap = useMemo(() => {
    const map = new Map<string, (typeof results)[number]>();
    for (const r of results) {
      map.set(r.item.id, r);
    }
    return map;
  }, [results]);

  const highlightMissing =
    linkParams.highlight && data.length > 0 && !data.some((c) => c.id === linkParams.highlight);

  const { focusedId, setFocusedIndex, tableRef, containerProps, getRowProps } = useListKeyboardNav({
    items: filteredResults,
    getId: (a) => a.id,
    listId: "categories",
    enabled: !anyDialogOpen,
    onOpen: (a) => setEditCategory(a),
  });

  if (isLoading) {
    return <TableSkeleton columns={4} />;
  }

  return (
    <>
      <PageHead
        title="Categories"
        sub={`${serverTotal} total${statusFilter === "all" ? "" : ` · ${statusFilter === "true" ? "active" : "inactive"}`}`}
        actions={
          <button
            type="button"
            className="btn primary"
            onClick={() => setCreateOpen(true)}
          >
            <Icon.Plus /> New category
          </button>
        }
      />
      <div className="filter-strip">
        <div style={{ flex: 1, minWidth: 240 }}>
          <FuzzySearchInput
            aria-label="Search categories"
            value={search}
            onChange={setSearch}
            placeholder="Search categories…"
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

      {highlightMissing && (
        <Alert>
          <Info className="h-4 w-4" />
          <AlertDescription>The highlighted item is not on this page.</AlertDescription>
        </Alert>
      )}

      {filteredResults.length === 0 ? (
        search ? (
          <NoResults
            searchTerm={search}
            onClearSearch={clearSearch}
            onSelectSuggestion={setSearch}
            entityName="categories"
          />
        ) : (
          <div role="status" className="py-12 text-center text-muted-foreground">
            No categories yet. Create one to get started.
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
                  <TableHead>Description</TableHead>
                  <SortableTableHead column="isActive" label="Status" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
                  <TableHead>Related</TableHead>
                  <TableHead className="w-24">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {filteredResults.map((category, index) => {
                  const result = matchMap.get(category.id);
                  const matches = result?.matches;
                  return (
                    <TableRow
                      key={category.id}
                      {...getRowProps(category.id)}
                      className={`cursor-pointer ${focusedId === category.id ? "bg-accent" : ""} ${linkParams.highlight === category.id ? "ring-2 ring-primary" : ""}`}
                      onClick={(e) => {
                        if (
                          (e.target as HTMLElement).closest(
                            "button, input, a, [role='button']",
                          )
                        )
                          return;
                        setFocusedIndex(index);
                      }}
                    >
                      <TableCell>
                        <SearchHighlight
                          text={category.name}
                          indices={getMatchIndices(matches, "name")}
                        />
                      </TableCell>
                      <TableCell className="text-muted-foreground">
                        {category.description ? (
                          <SearchHighlight
                            text={category.description}
                            indices={getMatchIndices(matches, "description")}
                          />
                        ) : (
                          <span className="italic">--</span>
                        )}
                      </TableCell>
                      <TableCell>
                        <Badge
                          variant={category.isActive ? "default" : "secondary"}
                        >
                          {category.isActive ? "Active" : "Inactive"}
                        </Badge>
                      </TableCell>
                      <TableCell>
                        <Link to={`/subcategories?categoryId=${category.id}`} className="text-sm text-primary hover:underline">
                          Subcategories
                        </Link>
                      </TableCell>
                      <TableCell>
                        <div className="flex gap-1">
                          <Button
                            variant="ghost"
                            size="icon"
                            aria-label="Edit"
                            onClick={() => setEditCategory(category)}
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
                                  <AlertDialogTitle>Delete Category?</AlertDialogTitle>
                                  <AlertDialogDescription>
                                    This will soft-delete this category and all its subcategories. You can restore it from the Recycle Bin.
                                  </AlertDialogDescription>
                                </AlertDialogHeader>
                                <AlertDialogFooter>
                                  <AlertDialogCancel>Cancel</AlertDialogCancel>
                                  <AlertDialogAction
                                    variant="destructive"
                                    onClick={() => deleteCategory.mutate(category.id)}
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
            <DialogTitle>Create Category</DialogTitle>
          </DialogHeader>
          <CategoryForm
            mode="create"
            isSubmitting={createCategory.isPending}
            onCancel={() => setCreateOpen(false)}
            onSubmit={(values) => {
              createCategory.mutate(values, {
                onSuccess: () => setCreateOpen(false),
              });
            }}
          />
        </DialogContent>
      </Dialog>

      {/* Edit Dialog */}
      <Dialog
        open={editCategory !== null}
        onOpenChange={(open) => !open && setEditCategory(null)}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Edit Category</DialogTitle>
          </DialogHeader>
          {editCategory && (
            <CategoryForm
              mode="edit"
              defaultValues={{
                name: editCategory.name,
                description: editCategory.description ?? "",
                isActive: editCategory.isActive,
              }}
              isSubmitting={updateCategory.isPending}
              onCancel={() => setEditCategory(null)}
              onSubmit={(values) => {
                updateCategory.mutate(
                  { id: editCategory.id, ...values },
                  { onSuccess: () => setEditCategory(null) },
                );
              }}
            />
          )}
        </DialogContent>
      </Dialog>

    </>
  );
}

export default Categories;
