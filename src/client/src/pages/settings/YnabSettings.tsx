import { useMemo } from "react";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useAccounts } from "@/hooks/useAccounts";
import {
  useYnabBudgets,
  useSelectedYnabBudget,
  useSelectYnabBudget,
  useYnabAccounts,
  useYnabAccountMappings,
  useCreateYnabAccountMapping,
  useUpdateYnabAccountMapping,
  useDeleteYnabAccountMapping,
  useYnabCategories,
  useDistinctReceiptItemCategories,
  useYnabCategoryMappings,
  useUnmappedCategories,
  useCreateYnabCategoryMapping,
  useUpdateYnabCategoryMapping,
  useDeleteYnabCategoryMapping,
} from "@/hooks/useYnab";
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
import { Badge } from "@/components/ui/badge";

const UNMAPPED_VALUE = "__unmapped__";

export default function YnabSettings() {
  usePageTitle("YNAB Settings");

  const { budgets, isLoading: budgetsLoading, isError: budgetsError } = useYnabBudgets();
  const { selectedBudgetId, isLoading: settingsLoading } = useSelectedYnabBudget();
  const selectBudget = useSelectYnabBudget();

  // Only fetch YNAB data when configured (budgets query succeeded)
  const ynabReady = !budgetsLoading && !budgetsError;

  const { data: receiptsAccounts, isLoading: accountsLoading } = useAccounts(0, 200);
  const { accounts: ynabAccounts, isLoading: ynabAccountsLoading } = useYnabAccounts(ynabReady);
  const { mappings: accountMappings, isLoading: accountMappingsLoading } = useYnabAccountMappings(ynabReady);
  const createAccountMapping = useCreateYnabAccountMapping();
  const updateAccountMapping = useUpdateYnabAccountMapping();
  const deleteAccountMapping = useDeleteYnabAccountMapping();

  const { categories: ynabCategories, isLoading: ynabCatsLoading } = useYnabCategories(ynabReady);
  const { categories: receiptCategories, isLoading: receiptCatsLoading } = useDistinctReceiptItemCategories(ynabReady);
  const { mappings: categoryMappings, isLoading: categoryMappingsLoading } = useYnabCategoryMappings(ynabReady);
  const { unmappedCategories } = useUnmappedCategories(ynabReady);

  const createCategoryMapping = useCreateYnabCategoryMapping();
  const updateCategoryMapping = useUpdateYnabCategoryMapping();
  const deleteCategoryMapping = useDeleteYnabCategoryMapping();

  const isLoading = budgetsLoading || settingsLoading;
  const notConfigured = budgetsError;
  const mappingSectionLoading = accountsLoading || ynabAccountsLoading || accountMappingsLoading;

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

  // Build a lookup from receiptsCategory to existing mapping
  const mappingsByCategory = useMemo(() => {
    const map = new Map<string, (typeof categoryMappings)[number]>();
    for (const m of categoryMappings) {
      map.set(m.receiptsCategory, m);
    }
    return map;
  }, [categoryMappings]);

  // Build a set of unmapped categories for quick lookup
  const unmappedSet = useMemo(
    () => new Set(unmappedCategories),
    [unmappedCategories],
  );

  function handleBudgetChange(budgetId: string) {
    selectBudget.mutate(budgetId);
  }

  function handleYnabAccountChange(receiptsAccountId: string, ynabAccountId: string) {
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

  function handleCategoryMappingChange(receiptsCategory: string, ynabCategoryId: string) {
    const ynabCat = ynabCategories.find((c) => c.id === ynabCategoryId);
    if (!ynabCat || !selectedBudgetId) return;

    const existingMapping = mappingsByCategory.get(receiptsCategory);
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
        receiptsCategory,
        ynabCategoryId: ynabCat.id,
        ynabCategoryName: ynabCat.name,
        ynabCategoryGroupName: ynabCat.categoryGroupName,
        ynabBudgetId: selectedBudgetId,
      });
    }
  }

  function handleDeleteMapping(receiptsCategory: string) {
    const existingMapping = mappingsByCategory.get(receiptsCategory);
    if (existingMapping) {
      deleteCategoryMapping.mutate(existingMapping.id);
    }
  }

  const categoryMappingLoading = ynabCatsLoading || receiptCatsLoading || categoryMappingsLoading;

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold tracking-tight">YNAB Settings</h1>
        <p className="text-muted-foreground">
          Configure your YNAB integration for transaction sync.
        </p>
      </div>

      {notConfigured && (
        <Alert variant="destructive">
          <AlertDescription>
            YNAB is not configured. Set the <code>YNAB_PAT</code> environment
            variable with your YNAB personal access token to enable the
            integration.
          </AlertDescription>
        </Alert>
      )}

      <Card>
        <CardHeader>
          <CardTitle>Budget Selection</CardTitle>
          <CardDescription>
            Select the YNAB budget to use for syncing transactions.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {isLoading ? (
            <div className="flex items-center gap-2">
              <Spinner className="h-4 w-4" />
              <span className="text-sm text-muted-foreground">Loading budgets...</span>
            </div>
          ) : notConfigured ? (
            <p className="text-sm text-muted-foreground">
              Configure YNAB to see available budgets.
            </p>
          ) : (
            <Select
              value={selectedBudgetId ?? ""}
              onValueChange={handleBudgetChange}
              disabled={selectBudget.isPending}
            >
              <SelectTrigger className="w-full max-w-sm">
                <SelectValue placeholder="Select a budget" />
              </SelectTrigger>
              <SelectContent>
                {budgets.map((budget) => (
                  <SelectItem key={budget.id} value={budget.id}>
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
            Map your receipts accounts to YNAB accounts for transaction sync.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {mappingSectionLoading ? (
            <div className="flex items-center gap-2">
              <Spinner className="h-4 w-4" />
              <span className="text-sm text-muted-foreground">Loading accounts...</span>
            </div>
          ) : notConfigured || !selectedBudgetId ? (
            <p className="text-sm text-muted-foreground">
              {notConfigured
                ? "Configure YNAB to map accounts."
                : "Select a budget above to map accounts."}
            </p>
          ) : !receiptsAccounts || receiptsAccounts.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No receipts accounts found. Create accounts first.
            </p>
          ) : (
            <div className="space-y-4">
              {receiptsAccounts.map((account) => {
                const mapping = accountMappings.find(
                  (m) => m.receiptsAccountId === account.id,
                );
                const currentYnabAccountId = mapping?.ynabAccountId ?? UNMAPPED_VALUE;

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
                        createAccountMapping.isPending ||
                        updateAccountMapping.isPending ||
                        deleteAccountMapping.isPending
                      }
                    >
                      <SelectTrigger className="w-full max-w-sm">
                        <SelectValue placeholder="Select a YNAB account" />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value={UNMAPPED_VALUE}>
                          <span className="text-muted-foreground">Not mapped</span>
                        </SelectItem>
                        {ynabAccounts.map((ynabAccount) => (
                          <SelectItem
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
                        onClick={() => deleteAccountMapping.mutate(mapping.id)}
                        disabled={deleteAccountMapping.isPending}
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
          {categoryMappingLoading ? (
            <div className="flex items-center gap-2">
              <Spinner className="h-4 w-4" />
              <span className="text-sm text-muted-foreground">
                Loading categories...
              </span>
            </div>
          ) : notConfigured || !selectedBudgetId ? (
            <p className="text-sm text-muted-foreground">
              {notConfigured
                ? "Configure YNAB to map categories."
                : "Select a budget above to map categories."}
            </p>
          ) : receiptCategories.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No receipt item categories found. Create some receipts first.
            </p>
          ) : (
            <div className="space-y-3">
              {receiptCategories.map((category) => {
                const mapping = mappingsByCategory.get(category);
                const isUnmapped = unmappedSet.has(category);

                return (
                  <div
                    key={category}
                    className="flex items-center gap-3"
                  >
                    <div className="flex items-center gap-2 min-w-[200px]">
                      <span className="text-sm font-medium">{category}</span>
                      {isUnmapped && (
                        <Badge variant="outline" className="text-amber-600 border-amber-300">
                          Unmapped
                        </Badge>
                      )}
                    </div>

                    <Select
                      value={mapping?.ynabCategoryId ?? ""}
                      onValueChange={(value) =>
                        handleCategoryMappingChange(category, value)
                      }
                    >
                      <SelectTrigger className="w-full max-w-sm">
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

                    {mapping && (
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => handleDeleteMapping(category)}
                        className="text-muted-foreground hover:text-destructive"
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
    </div>
  );
}
