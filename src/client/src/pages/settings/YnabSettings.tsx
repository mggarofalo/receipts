import { RequestFailure } from "@/components/RequestFailure";
import { useMemo, useState } from "react";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useAllAccounts } from "@/hooks/useAccounts";
import {
  useYnabConnectionStatus,
  useYnabBudgets,
  useSelectedYnabBudget,
  useSelectYnabBudget,
  useYnabAccounts,
  useYnabAccountMappings,
  useCreateYnabAccountMapping,
  useUpdateYnabAccountMapping,
  useDeleteYnabAccountMapping,
  useYnabCategories,
  useYnabCategoryMappings,
  useCreateYnabCategoryMapping,
  useUpdateYnabCategoryMapping,
  useDeleteYnabCategoryMapping,
  useYnabRateLimitStatus,
  useStaleMappings,
  useClearStaleMappings,
} from "@/hooks/useYnab";
import { YnabBulkSyncCard } from "@/components/YnabBulkSyncCard";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { PageHead } from "@/components/primitives";
import { Badge } from "@/components/ui/badge";
import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import { CategoryPathPicker } from "@/components/categories/CategoryPathPicker";
import { categoryPaths, type CategoryChoice } from "@/lib/category-paths";

const UNMAPPED_VALUE = "__unmapped__";

function formatRelativeTime(dateStr: string): string {
  const date = new Date(dateStr);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMin = Math.floor(diffMs / 60000);
  const diffHrs = Math.floor(diffMs / 3600000);
  const diffDays = Math.floor(diffMs / 86400000);

  if (diffMin < 1) return "just now";
  if (diffMin < 60) return `${diffMin}m ago`;
  if (diffHrs < 24) return `${diffHrs}h ago`;
  return `${diffDays}d ago`;
}

export default function YnabSettings() {
  usePageTitle("YNAB Settings");
  const [categoryChoice, setCategoryChoice] = useState<CategoryChoice>();

  const {
    isConfigured: connectionConfigured,
    isConnected,
    lastSuccessfulSyncUtc,
    isLoading: connectionLoading,
    isError: connectionError,
    refetch: retryConnection,
    isFetching: connectionFetching,
  } = useYnabConnectionStatus();

  const budgetsQuery = useYnabBudgets();
  const {
    budgets,
    isLoading: budgetsLoading,
    isError: budgetsError,
    refetch: retryBudgets,
    isFetching: budgetsFetching,
  } = budgetsQuery;
  const selectedBudgetQuery = useSelectedYnabBudget();
  const {
    selectedBudgetId,
    isLoading: settingsLoading,
    isError: settingsError,
    refetch: retrySettings,
    isFetching: settingsFetching,
  } = selectedBudgetQuery;
  const selectBudget = useSelectYnabBudget();

  const selectedBudgetExists =
    !!selectedBudgetId &&
    budgets.some((budget) => budget.id === selectedBudgetId);
  const selectedBudgetVerified =
    budgetsQuery.isSuccess &&
    selectedBudgetQuery.isSuccess &&
    selectedBudgetExists &&
    !budgetsFetching &&
    !settingsFetching &&
    !selectBudget.isPending;
  const ynabReady = connectionConfigured && budgetsQuery.isSuccess;
  const mappingReadsEnabled = ynabReady && selectedBudgetVerified;

  const receiptsAccountsQuery = useAllAccounts();
  const { data: receiptsAccounts, isLoading: accountsLoading } =
    receiptsAccountsQuery;
  const receiptAccountsUnavailable =
    !receiptsAccountsQuery.isSuccess || receiptsAccountsQuery.isFetching;
  const ynabAccountsQuery = useYnabAccounts(
    mappingReadsEnabled,
    selectedBudgetId,
  );
  const { accounts: ynabAccounts, isLoading: ynabAccountsLoading } =
    ynabAccountsQuery;
  const accountMappingsQuery = useYnabAccountMappings(
    mappingReadsEnabled,
    selectedBudgetId,
  );
  const { mappings: accountMappings, isLoading: accountMappingsLoading } =
    accountMappingsQuery;
  const createAccountMapping = useCreateYnabAccountMapping();
  const updateAccountMapping = useUpdateYnabAccountMapping();
  const deleteAccountMapping = useDeleteYnabAccountMapping();

  const ynabCategoriesQuery = useYnabCategories(
    mappingReadsEnabled,
    selectedBudgetId,
  );
  const { categories: ynabCategories, isLoading: ynabCatsLoading } =
    ynabCategoriesQuery;
  const taxonomyQuery = useCategoryTaxonomy(mappingReadsEnabled);
  const categoryMappingsQuery = useYnabCategoryMappings(
    mappingReadsEnabled,
    selectedBudgetId,
  );
  const { mappings: categoryMappings, isLoading: categoryMappingsLoading } =
    categoryMappingsQuery;

  const createCategoryMapping = useCreateYnabCategoryMapping();
  const updateCategoryMapping = useUpdateYnabCategoryMapping();
  const deleteCategoryMapping = useDeleteYnabCategoryMapping();

  const {
    rateLimitStatus,
    isError: rateLimitError,
    refetch: retryRateLimit,
    isFetching: rateLimitFetching,
  } = useYnabRateLimitStatus(connectionConfigured);
  const staleMappingsQuery = useStaleMappings(
    mappingReadsEnabled,
    selectedBudgetId,
  );
  const {
    staleAccountMappingCount,
    staleCategoryMappingCount,
    hasStaleMappings,
    isError: staleMappingsError,
    isFetching: staleMappingsFetching,
    refetch: retryStaleMappings,
  } = staleMappingsQuery;
  const clearStaleMappings = useClearStaleMappings();

  const isLoading = budgetsLoading || settingsLoading;
  const notConfigured =
    !connectionLoading && !connectionError && !connectionConfigured;
  const mappingSectionLoading =
    accountsLoading || ynabAccountsLoading || accountMappingsLoading;
  const accountReadError =
    ynabAccountsQuery.isError || accountMappingsQuery.isError;
  const accountReadUnavailable =
    !selectedBudgetVerified ||
    !ynabAccountsQuery.isSuccess ||
    ynabAccountsQuery.isFetching ||
    !accountMappingsQuery.isSuccess ||
    accountMappingsQuery.isFetching;
  const accountReadHasNoProof =
    !ynabAccountsQuery.data || !accountMappingsQuery.data;
  const accountMutationPending =
    createAccountMapping.isPending ||
    updateAccountMapping.isPending ||
    deleteAccountMapping.isPending;

  const categoryReadError =
    ynabCategoriesQuery.isError ||
    taxonomyQuery.isError ||
    categoryMappingsQuery.isError;
  const categoryReadUnavailable =
    !selectedBudgetVerified ||
    !ynabCategoriesQuery.isSuccess ||
    ynabCategoriesQuery.isFetching ||
    !taxonomyQuery.isSuccess ||
    taxonomyQuery.isFetching ||
    !categoryMappingsQuery.isSuccess ||
    categoryMappingsQuery.isFetching;
  const categoryReadHasNoProof =
    !ynabCategoriesQuery.data ||
    !taxonomyQuery.data ||
    !categoryMappingsQuery.data;
  const categoryMutationPending =
    createCategoryMapping.isPending ||
    updateCategoryMapping.isPending ||
    deleteCategoryMapping.isPending;

  // Group YNAB categories by category group for grouped dropdown
  const groupedCategories = useMemo(() => {
    const groups: Record<string, { id: string; name: string }[]> = {};
    for (const cat of ynabCategories) {
      const groupName = cat.categoryGroupName ?? "Other";
      if (!groups[groupName]) {
        groups[groupName] = [];
      }
      groups[groupName].push({ id: cat.id, name: cat.name });
    }
    return groups;
  }, [ynabCategories]);

  const mappingsByCategoryId = useMemo(() => {
    const map = new Map<string, (typeof categoryMappings)[number]>();
    for (const m of categoryMappings) {
      if (m.receiptsCategoryId) map.set(m.receiptsCategoryId, m);
    }
    return map;
  }, [categoryMappings]);
  const categoryRows = useMemo(
    () => categoryPaths(taxonomyQuery.data?.nodes ?? []),
    [taxonomyQuery.data],
  );
  const inactiveCategoryMappings = categoryMappings.filter(
    (mapping) =>
      mapping.receiptsCategoryId &&
      !categoryRows.some((row) => row.node.id === mapping.receiptsCategoryId),
  );
  const selectedRow = categoryRows.find(
    (row) => row.node.id === categoryChoice?.categoryId,
  );
  const currentCategoryChoice: CategoryChoice | undefined =
    selectedRow && taxonomyQuery.data
      ? {
          categoryId: selectedRow.node.id,
          taxonomyRevisionId: taxonomyQuery.data.id,
          path: selectedRow.path,
        }
      : undefined;
  const selectedMapping = selectedRow
    ? mappingsByCategoryId.get(selectedRow.node.id)
    : undefined;
  const inheritedMapping = selectedRow?.path
    .slice(0, -1)
    .reverse()
    .map((node) => mappingsByCategoryId.get(node.id))
    .find((mapping) => mapping !== undefined);

  function handleBudgetChange(budgetId: string) {
    if (
      budgetsError ||
      budgetsQuery.isFetching ||
      settingsError ||
      settingsFetching ||
      selectBudget.isPending ||
      !budgets.some((budget) => budget.id === budgetId)
    )
      return;
    selectBudget.mutate(budgetId);
  }

  function handleYnabAccountChange(
    receiptsAccountId: string,
    ynabAccountId: string,
  ) {
    if (
      receiptAccountsUnavailable ||
      accountReadUnavailable ||
      accountMutationPending ||
      !receiptsAccounts?.some((account) => account.id === receiptsAccountId)
    )
      return;
    const existingMapping = accountMappings.find(
      (m) => m.receiptsAccountId === receiptsAccountId,
    );

    if (ynabAccountId === UNMAPPED_VALUE) {
      if (existingMapping) {
        deleteAccountMapping.mutate(existingMapping.id);
      }
      return;
    }

    const ynabAccount = ynabAccounts.find((a) => a.id === ynabAccountId);
    if (!ynabAccount || !selectedBudgetId) return;

    if (existingMapping) {
      updateAccountMapping.mutate({
        id: existingMapping.id,
        ynabAccountId: ynabAccount.id,
        ynabAccountName: ynabAccount.name,
        ynabBudgetId: selectedBudgetId,
      });
    } else {
      createAccountMapping.mutate({
        receiptsAccountId,
        ynabAccountId: ynabAccount.id,
        ynabAccountName: ynabAccount.name,
        ynabBudgetId: selectedBudgetId,
      });
    }
  }

  function handleCategoryMappingChange(ynabCategoryId: string) {
    if (categoryReadUnavailable || categoryMutationPending) return;
    const ynabCat = ynabCategories.find((c) => c.id === ynabCategoryId);
    if (!ynabCat || !selectedBudgetId) return;

    if (!selectedRow) return;
    const existingMapping = mappingsByCategoryId.get(selectedRow.node.id);
    if (existingMapping) {
      updateCategoryMapping.mutate({
        id: existingMapping.id,
        ynabCategoryId: ynabCat.id,
        ynabCategoryName: ynabCat.name,
        ynabCategoryGroupName: ynabCat.categoryGroupName,
        ynabBudgetId: selectedBudgetId,
      });
    } else {
      createCategoryMapping.mutate({
        receiptsCategory: selectedRow.node.name,
        receiptsCategoryId: selectedRow.node.id,
        ynabCategoryId: ynabCat.id,
        ynabCategoryName: ynabCat.name,
        ynabCategoryGroupName: ynabCat.categoryGroupName,
        ynabBudgetId: selectedBudgetId,
      });
    }
  }

  function handleDeleteMapping(mappingId: string) {
    if (categoryReadUnavailable || categoryMutationPending) return;
    deleteCategoryMapping.mutate(mappingId);
  }

  const categoryMappingLoading =
    ynabCatsLoading || taxonomyQuery.isLoading || categoryMappingsLoading;

  return (
    <>
      <PageHead title="YNAB" />
      <div className="space-y-6">
        <Card>
          <CardHeader>
            <CardTitle>Connection Status</CardTitle>
            <CardDescription>
              Current status of your YNAB integration.
            </CardDescription>
          </CardHeader>
          <CardContent>
            {connectionError ? (
              <RequestFailure
                message="YNAB connection status is unavailable."
                retry={() => {
                  void retryConnection();
                }}
                isRetrying={connectionFetching}
              />
            ) : connectionLoading ? (
              <div className="flex items-center gap-2">
                <Spinner className="h-4 w-4" />
                <span className="text-sm text-muted-foreground">
                  Checking connection...
                </span>
              </div>
            ) : connectionConfigured && isConnected ? (
              <div className="flex items-center gap-3">
                <Badge className="bg-green-100 text-green-800 hover:bg-green-100 border-green-300">
                  Connected
                </Badge>
                <span className="text-sm text-muted-foreground">
                  {lastSuccessfulSyncUtc
                    ? `Last sync: ${formatRelativeTime(lastSuccessfulSyncUtc)}`
                    : "No syncs yet"}
                </span>
              </div>
            ) : connectionConfigured && !isConnected ? (
              <div className="flex items-center gap-3">
                <Badge variant="destructive">Disconnected</Badge>
                <span className="text-sm text-muted-foreground">
                  YNAB PAT is configured but the connection failed. Check your
                  token.
                </span>
              </div>
            ) : (
              <div className="flex items-center gap-3">
                <Badge variant="outline" className="text-muted-foreground">
                  Not Configured
                </Badge>
                <span className="text-sm text-muted-foreground">
                  Set the <code>YNAB_PAT</code> environment variable to enable
                  the integration.
                </span>
              </div>
            )}
          </CardContent>
        </Card>

        {/* The Connection Status card above is the single source of the
          "not configured" message; no separate banner is rendered, and the
          mapping cards below are hidden entirely until a PAT is set. */}

        {selectedBudgetId && staleMappingsError && (
          <RequestFailure
            message="Stale mapping status is unavailable. Any counts shown are last known."
            retry={() => {
              void retryStaleMappings();
            }}
            isRetrying={staleMappingsFetching}
          />
        )}

        {hasStaleMappings && (
          <Alert>
            <AlertDescription className="flex items-center justify-between">
              <span>
                Budget changed:{" "}
                {staleAccountMappingCount > 0 &&
                  `${staleAccountMappingCount} account mapping(s)`}
                {staleAccountMappingCount > 0 &&
                  staleCategoryMappingCount > 0 &&
                  " and "}
                {staleCategoryMappingCount > 0 &&
                  `${staleCategoryMappingCount} category mapping(s)`}{" "}
                belong to another budget. They are preserved and ignored while
                this budget is selected. Create mappings for the current budget
                before syncing; transactions exported elsewhere are eligible for
                a separate export here.
              </span>
              <Button
                variant="outline"
                size="sm"
                onClick={() => {
                  if (
                    selectedBudgetVerified &&
                    staleMappingsQuery.isSuccess &&
                    !staleMappingsQuery.isFetching &&
                    !clearStaleMappings.isPending
                  ) {
                    clearStaleMappings.mutate();
                  }
                }}
                disabled={
                  !selectedBudgetVerified ||
                  !staleMappingsQuery.isSuccess ||
                  staleMappingsQuery.isFetching ||
                  clearStaleMappings.isPending
                }
              >
                {clearStaleMappings.isPending
                  ? "Clearing..."
                  : "Delete previous-budget mappings"}
              </Button>
            </AlertDescription>
          </Alert>
        )}

        {!notConfigured && (
          <>
            <Card>
              <CardHeader>
                <CardTitle>Budget Selection</CardTitle>
                <CardDescription>
                  Mappings from previously selected budgets are preserved and
                  ignored while another destination is active. Switching to a
                  different budget requires mapping its accounts and categories;
                  transactions exported elsewhere can be re-exported to this
                  destination.
                </CardDescription>
              </CardHeader>
              <CardContent>
                {budgetsError && (
                  <RequestFailure
                    message="YNAB budgets are unavailable. Any choices shown are last known."
                    retry={() => {
                      void retryBudgets();
                    }}
                    isRetrying={budgetsQuery.isFetching}
                  />
                )}
                {settingsError && (
                  <RequestFailure
                    message="The selected YNAB budget is unavailable. Any selection shown is last known."
                    retry={() => {
                      void retrySettings();
                    }}
                    isRetrying={settingsFetching}
                  />
                )}
                {isLoading ? (
                  <div className="flex items-center gap-2">
                    <Spinner className="h-4 w-4" />
                    <span className="text-sm text-muted-foreground">
                      Loading budgets...
                    </span>
                  </div>
                ) : (budgetsError && !budgetsQuery.data) ||
                  (settingsError && !selectedBudgetQuery.data) ? null : (
                  <Select
                    value={selectedBudgetId ?? ""}
                    onValueChange={handleBudgetChange}
                    disabled={
                      selectBudget.isPending ||
                      budgetsError ||
                      budgetsQuery.isFetching ||
                      settingsError ||
                      settingsFetching
                    }
                  >
                    <SelectTrigger className="w-full max-w-sm">
                      <SelectValue placeholder="Select a budget" />
                    </SelectTrigger>
                    <SelectContent>
                      {budgets.map((budget) => (
                        <SelectItem
                          key={budget.id}
                          value={budget.id}
                          disabled={
                            selectBudget.isPending ||
                            budgetsError ||
                            budgetsQuery.isFetching ||
                            settingsError ||
                            settingsFetching
                          }
                        >
                          {budget.name}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                )}
              </CardContent>
            </Card>

            <Card>
              <CardHeader>
                <CardTitle>Account Mapping</CardTitle>
                <CardDescription>
                  Map your receipts accounts to YNAB accounts for transaction
                  sync.
                </CardDescription>
              </CardHeader>
              <CardContent>
                {receiptsAccountsQuery.isError && (
                  <RequestFailure
                    message="Receipts accounts are unavailable. Any accounts shown are last known."
                    retry={() => {
                      void receiptsAccountsQuery.refetch();
                    }}
                    isRetrying={receiptsAccountsQuery.isFetching}
                  />
                )}
                {accountReadError && (
                  <RequestFailure
                    message="YNAB account mappings are unavailable. Any mappings shown are last known."
                    retry={() => {
                      void Promise.all([
                        ynabAccountsQuery.refetch(),
                        accountMappingsQuery.refetch(),
                      ]);
                    }}
                    isRetrying={
                      ynabAccountsQuery.isFetching ||
                      accountMappingsQuery.isFetching
                    }
                  />
                )}
                {mappingSectionLoading ? (
                  <div className="flex items-center gap-2">
                    <Spinner className="h-4 w-4" />
                    <span className="text-sm text-muted-foreground">
                      Loading accounts...
                    </span>
                  </div>
                ) : settingsError || budgetsError ? (
                  <p className="text-sm text-muted-foreground">
                    Retry the selected budget above before changing mappings.
                  </p>
                ) : !selectedBudgetId ? (
                  <p className="text-sm text-muted-foreground">
                    Select a budget above to map accounts.
                  </p>
                ) : accountReadHasNoProof ||
                  (receiptsAccountsQuery.isError &&
                    !receiptsAccounts?.length) ? null : !receiptsAccounts ||
                  receiptsAccounts.length === 0 ? (
                  <p className="text-sm text-muted-foreground">
                    No receipts accounts found. Create accounts first.
                  </p>
                ) : (
                  <div className="space-y-4">
                    {receiptsAccounts.map((account) => {
                      const mapping = accountMappings.find(
                        (m) => m.receiptsAccountId === account.id,
                      );
                      const currentYnabAccountId =
                        mapping?.ynabAccountId ?? UNMAPPED_VALUE;

                      return (
                        <div
                          key={account.id}
                          className="flex items-center gap-4"
                        >
                          <span className="min-w-[200px] text-sm font-medium">
                            {account.name}
                          </span>
                          <Select
                            value={currentYnabAccountId}
                            onValueChange={(value) =>
                              handleYnabAccountChange(account.id!, value)
                            }
                            disabled={
                              receiptAccountsUnavailable ||
                              accountReadUnavailable ||
                              accountMutationPending
                            }
                          >
                            <SelectTrigger className="w-full max-w-sm">
                              <SelectValue placeholder="Select a YNAB account" />
                            </SelectTrigger>
                            <SelectContent>
                              <SelectItem
                                value={UNMAPPED_VALUE}
                                disabled={
                                  receiptAccountsUnavailable ||
                                  accountReadUnavailable ||
                                  accountMutationPending
                                }
                              >
                                <span className="text-muted-foreground">
                                  Not mapped
                                </span>
                              </SelectItem>
                              {ynabAccounts.map((ynabAccount) => (
                                <SelectItem
                                  disabled={
                                    receiptAccountsUnavailable ||
                                    accountReadUnavailable ||
                                    accountMutationPending
                                  }
                                  key={ynabAccount.id}
                                  value={ynabAccount.id}
                                >
                                  {ynabAccount.name}
                                </SelectItem>
                              ))}
                            </SelectContent>
                          </Select>
                          {mapping && (
                            <Button
                              variant="ghost"
                              size="sm"
                              onClick={() =>
                                handleYnabAccountChange(
                                  account.id!,
                                  UNMAPPED_VALUE,
                                )
                              }
                              disabled={
                                receiptAccountsUnavailable ||
                                accountReadUnavailable ||
                                accountMutationPending
                              }
                            >
                              Remove
                            </Button>
                          )}
                        </div>
                      );
                    })}
                  </div>
                )}
              </CardContent>
            </Card>

            <Card>
              <CardHeader>
                <CardTitle>Category Mapping</CardTitle>
                <CardDescription>
                  Map your receipt categories to YNAB categories for automatic
                  categorization during sync.
                </CardDescription>
              </CardHeader>
              <CardContent>
                {categoryReadError && (
                  <RequestFailure
                    message="YNAB category mappings are unavailable. Any mappings shown are last known."
                    retry={() => {
                      void Promise.all([
                        ynabCategoriesQuery.refetch(),
                        taxonomyQuery.refetch(),
                        categoryMappingsQuery.refetch(),
                      ]);
                    }}
                    isRetrying={
                      ynabCategoriesQuery.isFetching ||
                      taxonomyQuery.isFetching ||
                      categoryMappingsQuery.isFetching
                    }
                  />
                )}
                {categoryMappingLoading ? (
                  <div className="flex items-center gap-2">
                    <Spinner className="h-4 w-4" />
                    <span className="text-sm text-muted-foreground">
                      Loading categories...
                    </span>
                  </div>
                ) : settingsError || budgetsError ? (
                  <p className="text-sm text-muted-foreground">
                    Retry the selected budget above before changing mappings.
                  </p>
                ) : !selectedBudgetId ? (
                  <p className="text-sm text-muted-foreground">
                    Select a budget above to map categories.
                  </p>
                ) : categoryReadHasNoProof ? null : categoryRows.length === 0 &&
                  categoryMappings.length === 0 ? (
                  <p className="text-sm text-muted-foreground">
                    No active categories found. Create a category first.
                  </p>
                ) : (
                  <div className="space-y-3">
                    <div className="max-w-sm">
                      <span className="text-sm font-medium">
                        Receipt category
                      </span>
                      <CategoryPathPicker
                        revision={taxonomyQuery.data}
                        value={currentCategoryChoice}
                        onValueChange={setCategoryChoice}
                        disabled={
                          categoryReadUnavailable || categoryMutationPending
                        }
                        aria-label="Receipt category"
                      />
                    </div>
                    {selectedRow && (
                      <div className="space-y-2">
                        <p className="text-sm">
                          {selectedMapping
                            ? "Direct mapping"
                            : inheritedMapping
                              ? `Inherited from ${categoryRows.find((row) => row.node.id === inheritedMapping.receiptsCategoryId)?.label ?? "an ancestor"}: ${inheritedMapping.ynabCategoryGroupName} / ${inheritedMapping.ynabCategoryName}`
                              : "Unmapped"}
                        </p>
                        <div className="flex items-center gap-3">
                          <Select
                            value={selectedMapping?.ynabCategoryId ?? ""}
                            onValueChange={handleCategoryMappingChange}
                            disabled={
                              categoryReadUnavailable || categoryMutationPending
                            }
                          >
                            <SelectTrigger
                              className="w-full max-w-sm"
                              aria-label="YNAB category"
                            >
                              <SelectValue placeholder="Select YNAB category" />
                            </SelectTrigger>
                            <SelectContent>
                              {Object.entries(groupedCategories).map(
                                ([groupName, cats]) => (
                                  <SelectGroup key={groupName}>
                                    <SelectLabel>{groupName}</SelectLabel>
                                    {cats.map((cat) => (
                                      <SelectItem key={cat.id} value={cat.id}>
                                        {cat.name}
                                      </SelectItem>
                                    ))}
                                  </SelectGroup>
                                ),
                              )}
                            </SelectContent>
                          </Select>
                          {selectedMapping && (
                            <Button
                              variant="ghost"
                              size="sm"
                              onClick={() =>
                                handleDeleteMapping(selectedMapping.id)
                              }
                              disabled={
                                categoryReadUnavailable ||
                                categoryMutationPending
                              }
                            >
                              Remove
                            </Button>
                          )}
                        </div>
                      </div>
                    )}
                    {inactiveCategoryMappings.map((mapping) => {
                      const category = taxonomyQuery.data?.nodes.find(
                        (node) => node.id === mapping.receiptsCategoryId,
                      );
                      return (
                        <div
                          key={mapping.id}
                          className="space-y-2 rounded-md border p-3 text-sm"
                        >
                          <p className="font-medium">
                            {category?.name ?? mapping.receiptsCategory} —{" "}
                            {category
                              ? "Archived category"
                              : "Category absent from taxonomy"}
                          </p>
                          <p className="break-words text-muted-foreground">
                            Stable category ID: {mapping.receiptsCategoryId}.
                            Current YNAB mapping:{" "}
                            {mapping.ynabCategoryGroupName} /{" "}
                            {mapping.ynabCategoryName}.
                          </p>
                          <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
                            <Select
                              value={mapping.ynabCategoryId}
                              onValueChange={(ynabCategoryId) => {
                                if (
                                  categoryReadUnavailable ||
                                  categoryMutationPending
                                )
                                  return;
                                const ynabCat = ynabCategories.find(
                                  (candidate) =>
                                    candidate.id === ynabCategoryId,
                                );
                                if (
                                  !ynabCat ||
                                  ynabCat.id === mapping.ynabCategoryId
                                )
                                  return;
                                updateCategoryMapping.mutate({
                                  id: mapping.id,
                                  ynabCategoryId: ynabCat.id,
                                  ynabCategoryName: ynabCat.name,
                                  ynabCategoryGroupName:
                                    ynabCat.categoryGroupName,
                                  ynabBudgetId: selectedBudgetId!,
                                });
                              }}
                              disabled={
                                categoryReadUnavailable ||
                                categoryMutationPending
                              }
                            >
                              <SelectTrigger
                                className="w-full min-w-0 sm:max-w-sm"
                                aria-label={`YNAB category for ${category?.name ?? mapping.receiptsCategory}`}
                              >
                                <SelectValue placeholder="Select YNAB category" />
                              </SelectTrigger>
                              <SelectContent>
                                {Object.entries(groupedCategories).map(
                                  ([groupName, cats]) => (
                                    <SelectGroup key={groupName}>
                                      <SelectLabel>{groupName}</SelectLabel>
                                      {cats.map((cat) => (
                                        <SelectItem key={cat.id} value={cat.id}>
                                          {cat.name}
                                        </SelectItem>
                                      ))}
                                    </SelectGroup>
                                  ),
                                )}
                              </SelectContent>
                            </Select>
                            <Button
                              variant="ghost"
                              size="sm"
                              className="w-full sm:w-auto"
                              onClick={() => handleDeleteMapping(mapping.id)}
                              disabled={
                                categoryReadUnavailable ||
                                categoryMutationPending
                              }
                            >
                              Remove stable mapping
                            </Button>
                          </div>
                        </div>
                      );
                    })}
                    {categoryMappings
                      .filter((mapping) => !mapping.receiptsCategoryId)
                      .map((mapping) => (
                        <div
                          key={mapping.id}
                          className="space-y-2 rounded-md border p-3 text-sm"
                        >
                          <p className="break-words">
                            Legacy “{mapping.receiptsCategory}” —{" "}
                            {mapping.legacyResolution ?? "Unresolved"}.
                            {mapping.legacyCandidateCategoryIds?.length
                              ? ` ${mapping.legacyCandidateCategoryIds.length} possible category IDs.`
                              : " No stable category ID."}{" "}
                            Remove this mapping and create one for a selected
                            category path.
                          </p>
                          <p className="break-words text-muted-foreground">
                            Current YNAB mapping:{" "}
                            {mapping.ynabCategoryGroupName ?? "Other"} /{" "}
                            {mapping.ynabCategoryName}. Budget:{" "}
                            {budgets.find(
                              (budget) => budget.id === mapping.ynabBudgetId,
                            )?.name ?? mapping.ynabBudgetId}
                            .
                          </p>
                          <Button
                            variant="ghost"
                            size="sm"
                            className="w-full sm:w-auto"
                            onClick={() => handleDeleteMapping(mapping.id)}
                            disabled={
                              categoryReadUnavailable || categoryMutationPending
                            }
                          >
                            Remove legacy mapping
                          </Button>
                        </div>
                      ))}
                  </div>
                )}
              </CardContent>
            </Card>

            {selectedBudgetId && <YnabBulkSyncCard />}
          </>
        )}

        {connectionConfigured && (rateLimitStatus || rateLimitError) && (
          <Card>
            <CardHeader>
              <CardTitle>API Rate Limit</CardTitle>
              <CardDescription>
                YNAB enforces 200 requests per hour. This tracks your current
                usage.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <div className="space-y-3">
                {rateLimitError && (
                  <RequestFailure
                    message="YNAB rate-limit status is unavailable. Any usage shown is last known."
                    retry={() => {
                      void retryRateLimit();
                    }}
                    isRetrying={rateLimitFetching}
                  />
                )}
                {rateLimitStatus && (
                  <>
                    <div className="flex items-center justify-between text-sm">
                      <span className="text-muted-foreground">
                        {rateLimitStatus.requestsUsed} /{" "}
                        {rateLimitStatus.maxRequests} requests used
                      </span>
                      <span className="font-medium">
                        {rateLimitStatus.remainingRequests} remaining
                      </span>
                    </div>
                    <div className="h-2 rounded-full bg-muted overflow-hidden">
                      <div
                        role="progressbar"
                        aria-label="API rate limit usage"
                        aria-valuenow={rateLimitStatus.requestsUsed}
                        aria-valuemin={0}
                        aria-valuemax={rateLimitStatus.maxRequests}
                        className={`h-full rounded-full transition-all ${
                          rateLimitStatus.remainingRequests <= 20
                            ? "bg-destructive"
                            : rateLimitStatus.remainingRequests <= 50
                              ? "bg-amber-500"
                              : "bg-primary"
                        }`}
                        style={{
                          width: `${(rateLimitStatus.requestsUsed / rateLimitStatus.maxRequests) * 100}%`,
                        }}
                      />
                    </div>
                    {rateLimitStatus.oldestRequestAt &&
                      rateLimitStatus.windowResetAt && (
                        <p className="text-xs text-muted-foreground">
                          Window resets at{" "}
                          {new Date(
                            rateLimitStatus.windowResetAt,
                          ).toLocaleTimeString()}
                        </p>
                      )}
                    {rateLimitStatus.remainingRequests <= 20 && (
                      <Alert variant="destructive">
                        <AlertDescription>
                          API quota is running low. Bulk operations may be
                          blocked until the window resets.
                        </AlertDescription>
                      </Alert>
                    )}
                  </>
                )}
              </div>
            </CardContent>
          </Card>
        )}
      </div>
    </>
  );
}
