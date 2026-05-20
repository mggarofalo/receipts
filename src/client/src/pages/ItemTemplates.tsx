import { useState, useMemo, useCallback } from "react";
import {
  useItemTemplates,
  useCreateItemTemplate,
  useUpdateItemTemplate,
  useDeleteItemTemplates,
  useHideItemTemplate,
} from "@/hooks/useItemTemplates";
import { usePermission } from "@/hooks/usePermission";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useOpenNewItem } from "@/hooks/useOpenNewItem";
import { useFuzzySearch } from "@/hooks/useFuzzySearch";
import { useSavedFilters } from "@/hooks/useSavedFilters";
import { useServerPagination } from "@/hooks/useServerPagination";
import { useServerSort } from "@/hooks/useServerSort";
import { useListKeyboardNav } from "@/hooks/useListKeyboardNav";
import type { FuseSearchConfig } from "@/lib/search";
import { ItemTemplateForm } from "@/components/ItemTemplateForm";
import { FuzzySearchInput } from "@/components/FuzzySearchInput";
import { SearchHighlight } from "@/components/SearchHighlight";
import { getMatchIndices } from "@/lib/search-highlight";
import { SortableTableHead } from "@/components/SortableTableHead";
import { NoResults } from "@/components/NoResults";
import { Pagination } from "@/components/Pagination";
import { formatCurrency } from "@/lib/format";
import { Button } from "@/components/ui/button";
import { Icon, PageHead } from "@/components/primitives";
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
import { Checkbox } from "@/components/ui/checkbox";
import { Spinner } from "@/components/ui/spinner";
import { Pencil } from "lucide-react";

interface ItemTemplateResponse {
  id: string;
  name: string;
  description?: string | null;
  defaultCategory?: string | null;
  defaultSubcategory?: string | null;
  defaultUnitPrice?: number | null;
  defaultUnitPriceCurrency?: string | null;
  defaultItemCode?: string | null;
}

const SEARCH_CONFIG: FuseSearchConfig<ItemTemplateResponse> = {
  keys: [
    { name: "name", weight: 3 },
    { name: "description", weight: 1 },
    { name: "defaultCategory", weight: 1 },
    { name: "defaultItemCode", weight: 1 },
  ],
};

function ItemTemplates() {
  usePageTitle("Item Templates");
  const { sortBy, sortDirection, toggleSort } = useServerSort({ defaultSortBy: "name", defaultSortDirection: "asc" });
  const { offset, limit, currentPage, pageSize, totalPages, setPage, setPageSize, resetPage } = useServerPagination({ sortBy, sortDirection });
  const { data: itemTemplatesData, total: serverTotal, isLoading } = useItemTemplates(offset, limit, sortBy, sortDirection);
  const createItemTemplate = useCreateItemTemplate();
  const updateItemTemplate = useUpdateItemTemplate();
  const deleteItemTemplates = useDeleteItemTemplates();
  const hideItemTemplate = useHideItemTemplate();
  const { isAdmin } = usePermission();

  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [createOpen, setCreateOpen] = useState(false);
  const [editTemplate, setEditTemplate] = useState<ItemTemplateResponse | null>(
    null,
  );
  const [deleteOpen, setDeleteOpen] = useState(false);

  const anyDialogOpen = createOpen || editTemplate !== null || deleteOpen;

  const openCreate = useCallback(() => setCreateOpen(true), []);
  useOpenNewItem(openCreate);

  const handleSort = useCallback((column: string) => {
    toggleSort(column);
    resetPage();
  }, [toggleSort, resetPage]);

  const data = (itemTemplatesData as ItemTemplateResponse[] | undefined) ?? [];
  useSavedFilters("itemTemplates");

  const { search, setSearch, results, totalCount, clearSearch } =
    useFuzzySearch({ data, config: SEARCH_CONFIG });

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

  function toggleSelect(id: string) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  function toggleAll() {
    if (selected.size === filteredResults.length) {
      setSelected(new Set());
    } else {
      setSelected(new Set(filteredResults.map((a) => a.id)));
    }
  }

  const { focusedId, setFocusedIndex, tableRef, containerProps, getRowProps } = useListKeyboardNav({
    items: filteredResults,
    getId: (a) => a.id,
    listId: "item-templates",
    enabled: !anyDialogOpen,
    onOpen: (a) => setEditTemplate(a),
    onDelete: () => setDeleteOpen(true),
    onSelectAll: () => setSelected(new Set(filteredResults.map((a) => a.id))),
    onDeselectAll: () => setSelected(new Set()),
    onToggleSelect: (a) => toggleSelect(a.id),
    selected,
  });

  if (isLoading) {
    return <TableSkeleton columns={6} />;
  }

  return (
    <>
      <PageHead
        title="Item templates"
        sub={`${serverTotal} total`}
        actions={
          <>
            {selected.size > 0 && (
              <button
                type="button"
                className="btn danger"
                onClick={() => setDeleteOpen(true)}
              >
                <Icon.Trash /> Delete ({selected.size})
              </button>
            )}
            <button
              type="button"
              className="btn primary"
              onClick={() => setCreateOpen(true)}
            >
              <Icon.Plus /> New template
            </button>
          </>
        }
      />
      <div className="filter-strip">
        <div style={{ flex: 1, minWidth: 240 }}>
          <FuzzySearchInput
            aria-label="Search item templates"
            value={search}
            onChange={setSearch}
            placeholder="Search templates…"
            resultCount={filteredResults.length}
            totalCount={totalCount}
          />
        </div>
      </div>

      {filteredResults.length === 0 ? (
        search ? (
          <NoResults
            searchTerm={search}
            onClearSearch={clearSearch}
            onSelectSuggestion={setSearch}
            entityName="item templates"
          />
        ) : (
          <div role="status" className="py-12 text-center text-muted-foreground">
            No item templates yet. Create one to get started.
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
                  <TableHead className="w-12">
                    <Checkbox
                      aria-label="Select all rows"
                      checked={
                        selected.size === filteredResults.length &&
                        filteredResults.length > 0
                      }
                      onCheckedChange={toggleAll}
                    />
                  </TableHead>
                  <SortableTableHead column="name" label="Name" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
                  <TableHead>Category</TableHead>
                  <TableHead>Subcategory</TableHead>
                  <TableHead>Unit Price</TableHead>
                  <TableHead className="w-24">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {filteredResults.map((template, index) => {
                  const result = matchMap.get(template.id);
                  const matches = result?.matches;
                  return (
                    <TableRow
                      key={template.id}
                      {...getRowProps(template.id)}
                      className={`cursor-pointer ${focusedId === template.id ? "bg-accent" : ""}`}
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
                        <Checkbox
                          aria-label={`Select ${template.name}`}
                          checked={selected.has(template.id)}
                          onCheckedChange={() => toggleSelect(template.id)}
                        />
                      </TableCell>
                      <TableCell>
                        <SearchHighlight
                          text={template.name}
                          indices={getMatchIndices(matches, "name")}
                        />
                      </TableCell>
                      <TableCell className="text-muted-foreground">
                        {template.defaultCategory ? (
                          <SearchHighlight
                            text={template.defaultCategory}
                            indices={getMatchIndices(
                              matches,
                              "defaultCategory",
                            )}
                          />
                        ) : (
                          <span className="italic">--</span>
                        )}
                      </TableCell>
                      <TableCell className="text-muted-foreground">
                        {template.defaultSubcategory ?? (
                          <span className="italic">--</span>
                        )}
                      </TableCell>
                      <TableCell className="text-muted-foreground">
                        {template.defaultUnitPrice != null ? (
                          formatCurrency(template.defaultUnitPrice)
                        ) : (
                          <span className="italic">--</span>
                        )}
                      </TableCell>
                      <TableCell>
                        <Button
                          variant="ghost"
                          size="icon"
                          aria-label="Edit"
                          onClick={() => setEditTemplate(template)}
                        >
                          <Pencil className="h-4 w-4" />
                        </Button>
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
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>Create Item Template</DialogTitle>
          </DialogHeader>
          <ItemTemplateForm
            mode="create"
            isSubmitting={createItemTemplate.isPending}
            onCancel={() => setCreateOpen(false)}
            onSubmit={(values) => {
              createItemTemplate.mutate(
                {
                  name: values.name,
                  description: values.description || null,
                  defaultCategory: values.defaultCategory || null,
                  defaultSubcategory: values.defaultSubcategory || null,
                  defaultUnitPrice: values.defaultUnitPrice ?? null,
                  defaultItemCode: values.defaultItemCode || null,
                },
                { onSuccess: () => setCreateOpen(false) },
              );
            }}
          />
        </DialogContent>
      </Dialog>

      {/* Edit Dialog */}
      <Dialog
        open={editTemplate !== null}
        onOpenChange={(open) => !open && setEditTemplate(null)}
      >
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>Edit Item Template</DialogTitle>
          </DialogHeader>
          {editTemplate && (
            <ItemTemplateForm
              mode="edit"
              defaultValues={{
                name: editTemplate.name,
                description: editTemplate.description ?? "",
                defaultCategory: editTemplate.defaultCategory ?? "",
                defaultSubcategory: editTemplate.defaultSubcategory ?? "",
                defaultUnitPrice: editTemplate.defaultUnitPrice ?? undefined,
                defaultItemCode: editTemplate.defaultItemCode ?? "",
              }}
              isSubmitting={updateItemTemplate.isPending}
              onCancel={() => setEditTemplate(null)}
              onSubmit={(values) => {
                updateItemTemplate.mutate(
                  {
                    id: editTemplate.id,
                    name: values.name,
                    description: values.description || null,
                    defaultCategory: values.defaultCategory || null,
                    defaultSubcategory: values.defaultSubcategory || null,
                    defaultUnitPrice: values.defaultUnitPrice ?? null,
                    defaultItemCode: values.defaultItemCode || null,
                  },
                  { onSuccess: () => setEditTemplate(null) },
                );
              }}
              {...(isAdmin() ? {
                onHide: () => {
                  hideItemTemplate.mutate(editTemplate.id, {
                    onSuccess: () => setEditTemplate(null),
                  });
                },
                isHiding: hideItemTemplate.isPending,
              } : {})}
            />
          )}
        </DialogContent>
      </Dialog>

      {/* Delete Confirmation Dialog */}
      <Dialog open={deleteOpen} onOpenChange={setDeleteOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete Item Templates</DialogTitle>
          </DialogHeader>
          <p className="text-sm text-muted-foreground">
            Are you sure you want to delete {selected.size} item template(s)?
            This action can be undone by restoring.
          </p>
          <div className="flex justify-end gap-2 pt-4">
            <Button variant="outline" onClick={() => setDeleteOpen(false)}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              disabled={deleteItemTemplates.isPending}
              onClick={() => {
                const ids = [...selected];
                setSelected(new Set());
                setDeleteOpen(false);
                deleteItemTemplates.mutate(ids);
              }}
            >
              {deleteItemTemplates.isPending && <Spinner size="sm" />}
              {deleteItemTemplates.isPending ? "Deleting..." : "Delete"}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}

export default ItemTemplates;
