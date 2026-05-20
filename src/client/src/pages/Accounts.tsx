import { Fragment, useState, useMemo, useCallback } from "react";
import { Link } from "react-router";
import {
  useAccounts,
  useAccountCards,
  useCreateAccount,
  useUpdateAccount,
  useDeleteAccount,
} from "@/hooks/useAccounts";
import { usePermission } from "@/hooks/usePermission";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useEntityLinkParams } from "@/hooks/useEntityLinkParams";
import { useOpenNewItem } from "@/hooks/useOpenNewItem";
import { useFuzzySearch } from "@/hooks/useFuzzySearch";
import { useServerPagination } from "@/hooks/useServerPagination";
import { useServerSort } from "@/hooks/useServerSort";
import { useListKeyboardNav } from "@/hooks/useListKeyboardNav";
import type { FuseSearchConfig } from "@/lib/search";
import { AccountForm } from "@/components/AccountForm";
import { FuzzySearchInput } from "@/components/FuzzySearchInput";
import { SearchHighlight } from "@/components/SearchHighlight";
import { getMatchIndices } from "@/lib/search-highlight";
import { SortableTableHead } from "@/components/SortableTableHead";
import { NoResults } from "@/components/NoResults";
import { Pagination } from "@/components/Pagination";
import { Button } from "@/components/ui/button";
import { Icon, PageHead } from "@/components/primitives";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
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
import { Alert, AlertDescription } from "@/components/ui/alert";
import { ChevronDown, ChevronRight, Info, Pencil } from "lucide-react";

interface AccountRow {
  id: string;
  name: string;
  isActive: boolean;
}

const SEARCH_CONFIG: FuseSearchConfig<AccountRow> = {
  keys: [{ name: "name", weight: 1 }],
};

const STATUS_STORAGE_KEY = "accounts-status-filter";
type StatusFilter = "all" | "true" | "false";

const HIGHLIGHT_PARAMS = ["highlight"] as const;

const getAccountId = (a: AccountRow) => a.id;

function AccountCardsRow({ accountId }: { accountId: string }) {
  const { data, isLoading } = useAccountCards(accountId);

  if (isLoading) {
    return (
      <div className="text-sm text-muted-foreground">Loading cards…</div>
    );
  }

  if (!data || data.length === 0) {
    return (
      <div className="text-sm text-muted-foreground italic">
        No cards linked to this account yet.
      </div>
    );
  }

  return (
    <div className="space-y-1">
      <div className="text-xs font-medium text-muted-foreground">
        Cards ({data.length})
      </div>
      <ul className="space-y-1">
        {data.map((card) => (
          <li
            key={card.id}
            className="flex items-center gap-3 rounded border px-3 py-1.5 text-sm"
          >
            <span className="font-mono text-xs text-muted-foreground">
              {card.cardCode}
            </span>
            <span>{card.name}</span>
            {!card.isActive && (
              <Badge variant="secondary" className="ml-auto">
                Inactive
              </Badge>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}

function Accounts() {
  usePageTitle("Accounts");
  const { params: linkParams } = useEntityLinkParams(HIGHLIGHT_PARAMS);
  const { sortBy, sortDirection, toggleSort } = useServerSort({ defaultSortBy: "name", defaultSortDirection: "asc" });
  const { offset, limit, currentPage, pageSize, totalPages, setPage, setPageSize, resetPage } = useServerPagination({ sortBy, sortDirection });
  const [statusFilter, setStatusFilter] = useState<StatusFilter>(() => {
    const saved = localStorage.getItem(STATUS_STORAGE_KEY);
    return saved === "all" || saved === "true" || saved === "false" ? saved : "true";
  });
  const isActiveParam = statusFilter === "all" ? undefined : statusFilter === "true";
  const { data: accountsData, total: serverTotal, isLoading } = useAccounts(offset, limit, sortBy, sortDirection, isActiveParam);
  const createAccount = useCreateAccount();
  const updateAccount = useUpdateAccount();
  const { mutate: mutateUpdateAccount } = updateAccount;
  const deleteAccount = useDeleteAccount();
  const { isAdmin } = usePermission();
  const [createOpen, setCreateOpen] = useState(false);
  const [editAccount, setEditAccount] = useState<AccountRow | null>(null);
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set());

  const anyDialogOpen = createOpen || editAccount !== null;

  const toggleExpanded = useCallback((id: string) => {
    setExpandedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }, []);

  const openCreate = useCallback(() => setCreateOpen(true), []);
  useOpenNewItem(openCreate);

  const handleSort = useCallback((column: string) => {
    toggleSort(column);
    resetPage();
  }, [toggleSort, resetPage]);

  const handleToggleActive = useCallback((account: AccountRow, checked: boolean) => {
    mutateUpdateAccount({
      id: account.id,
      name: account.name,
      isActive: checked,
    });
  }, [mutateUpdateAccount]);

  const handleOpen = useCallback((a: AccountRow) => setEditAccount(a), []);

  const data = useMemo(() => (accountsData as AccountRow[] | undefined) ?? [], [accountsData]);

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
    linkParams.highlight && data.length > 0 && !data.some((a) => a.id === linkParams.highlight);

  const { focusedId, setFocusedIndex, tableRef, containerProps, getRowProps } = useListKeyboardNav({
    items: filteredResults,
    getId: getAccountId,
    listId: "accounts",
    enabled: !anyDialogOpen,
    onOpen: handleOpen,
  });

  if (isLoading) {
    return <TableSkeleton columns={4} />;
  }

  return (
    <>
      <PageHead
        title="Accounts"
        sub={`${serverTotal} total${statusFilter === "all" ? "" : ` · ${statusFilter === "true" ? "active" : "inactive"}`}`}
        actions={
          <button
            type="button"
            className="btn primary"
            onClick={() => setCreateOpen(true)}
          >
            <Icon.Plus /> New account
          </button>
        }
      />
      <div className="filter-strip">
        <div style={{ flex: 1, minWidth: 240 }}>
          <FuzzySearchInput
            aria-label="Search accounts"
            value={search}
            onChange={setSearch}
            placeholder="Search accounts…"
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
            entityName="accounts"
          />
        ) : (
          <div role="status" className="py-12 text-center text-muted-foreground">
            No accounts yet. Create one to get started.
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
                  <TableHead className="w-10" />
                  <SortableTableHead column="name" label="Name" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
                  <SortableTableHead column="isActive" label="Status" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
                  <TableHead>Related</TableHead>
                  <TableHead className="w-24">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {filteredResults.map((account, index) => {
                  const result = matchMap.get(account.id);
                  const matches = result?.matches;
                  const isExpanded = expandedIds.has(account.id);
                  return (
                    <Fragment key={account.id}>
                      <TableRow
                        {...getRowProps(account.id)}
                        className={`cursor-pointer ${focusedId === account.id ? "bg-accent" : ""} ${linkParams.highlight === account.id ? "ring-2 ring-primary" : ""}`}
                        onClick={(e) => {
                          if ((e.target as HTMLElement).closest("button, input, a, [role='button']")) return;
                          setFocusedIndex(index);
                        }}
                      >
                        <TableCell>
                          <Button
                            variant="ghost"
                            size="icon"
                            aria-label={isExpanded ? "Collapse cards" : "Expand cards"}
                            aria-expanded={isExpanded}
                            onClick={() => toggleExpanded(account.id)}
                          >
                            {isExpanded ? (
                              <ChevronDown className="h-4 w-4" />
                            ) : (
                              <ChevronRight className="h-4 w-4" />
                            )}
                          </Button>
                        </TableCell>
                        <TableCell>
                          <SearchHighlight
                            text={account.name}
                            indices={getMatchIndices(matches, "name")}
                          />
                        </TableCell>
                        <TableCell>
                          <div className="flex items-center gap-2">
                            <Switch
                              checked={account.isActive}
                              onCheckedChange={(checked) => handleToggleActive(account, checked)}
                              aria-label={`Toggle ${account.name} active status`}
                            />
                            <Badge
                              variant={account.isActive ? "default" : "secondary"}
                            >
                              {account.isActive ? "Active" : "Inactive"}
                            </Badge>
                          </div>
                        </TableCell>
                        <TableCell>
                          <Link
                            to={`/receipts?accountId=${account.id}`}
                            className="text-sm text-primary hover:underline"
                            aria-label={`View receipts for ${account.name}`}
                          >
                            Receipts
                          </Link>
                        </TableCell>
                        <TableCell>
                          <Button
                            variant="ghost"
                            size="icon"
                            aria-label="Edit"
                            onClick={() => setEditAccount(account)}
                          >
                            <Pencil className="h-4 w-4" />
                          </Button>
                        </TableCell>
                      </TableRow>
                      {isExpanded && (
                        <TableRow
                          className="bg-muted/30 hover:bg-muted/30"
                        >
                          <TableCell />
                          <TableCell colSpan={4} className="py-3">
                            <AccountCardsRow accountId={account.id} />
                          </TableCell>
                        </TableRow>
                      )}
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
            <DialogTitle>Create Account</DialogTitle>
          </DialogHeader>
          <AccountForm
            mode="create"
            isSubmitting={createAccount.isPending}
            onCancel={() => setCreateOpen(false)}
            onSubmit={(values) => {
              createAccount.mutate(values, {
                onSuccess: () => setCreateOpen(false),
              });
            }}
          />
        </DialogContent>
      </Dialog>

      {/* Edit Dialog */}
      <Dialog
        open={editAccount !== null}
        onOpenChange={(open) => !open && setEditAccount(null)}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Edit Account</DialogTitle>
          </DialogHeader>
          {editAccount && (
            <AccountForm
              mode="edit"
              defaultValues={{
                name: editAccount.name,
                isActive: editAccount.isActive,
              }}
              isSubmitting={updateAccount.isPending}
              onCancel={() => setEditAccount(null)}
              onSubmit={(values) => {
                updateAccount.mutate(
                  { id: editAccount.id, ...values },
                  { onSuccess: () => setEditAccount(null) },
                );
              }}
              isAdmin={isAdmin()}
              isDeleting={deleteAccount.isPending}
              onDelete={() => {
                deleteAccount.mutate(editAccount.id, {
                  onSuccess: () => setEditAccount(null),
                });
              }}
            />
          )}
        </DialogContent>
      </Dialog>

    </>
  );
}

export default Accounts;
