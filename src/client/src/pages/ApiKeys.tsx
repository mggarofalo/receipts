import { useState, useCallback } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import { usePageTitle } from "@/hooks/usePageTitle";
import { usePermission } from "@/hooks/usePermission";
import { useAuth } from "@/hooks/useAuth";
import { useOpenNewItem } from "@/hooks/useOpenNewItem";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import client from "@/lib/api-client";
import { showSuccess, showError } from "@/lib/toast";
import { capitalize } from "@/lib/format";
import { Button } from "@/components/ui/button";
import { Icon, PageHead } from "@/components/primitives";
import { Checkbox } from "@/components/ui/checkbox";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import { Input } from "@/components/ui/input";
import { DateInput } from "@/components/ui/date-input";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { useListKeyboardNav } from "@/hooks/useListKeyboardNav";
import { useLocalTable } from "@/hooks/useLocalTable";
import { Pagination } from "@/components/Pagination";
import { SortableTableHead } from "@/components/SortableTableHead";
import { Combobox } from "@/components/ui/combobox";
import type { components } from "@/generated/api";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { TableSkeleton } from "@/components/ui/table-skeleton";
import { Spinner } from "@/components/ui/spinner";

const createKeySchema = z.object({
  name: z.string().min(1, "Name is required"),
  expiresAt: z.string().optional(),
  bypassRateLimit: z.boolean(),
});

type CreateKeyFormValues = z.infer<typeof createKeySchema>;
type ApiKey = components["schemas"]["ApiKeyResponse"];

const EMPTY_API_KEYS: ApiKey[] = [];
const STATUS_OPTIONS = [
  { value: "all", label: "All statuses" },
  { value: "active", label: "Active" },
  { value: "expired", label: "Expired" },
  { value: "revoked", label: "Revoked" },
];
const SORT_COLUMNS = [
  { column: "name", label: "Name" },
  { column: "createdAt", label: "Created" },
  { column: "lastUsedAt", label: "Last Used" },
  { column: "expiresAt", label: "Expires" },
  { column: "status", label: "Status" },
];

function getKeyStatus(key: {
  isRevoked?: boolean;
  expiresAt?: string | null;
}): "active" | "expired" | "revoked" {
  if (key.isRevoked) return "revoked";
  if (key.expiresAt && new Date(key.expiresAt) < new Date()) return "expired";
  return "active";
}

function statusBadgeVariant(
  status: "active" | "expired" | "revoked",
): "default" | "secondary" | "destructive" | "outline" {
  switch (status) {
    case "active":
      return "default";
    case "expired":
      return "secondary";
    case "revoked":
      return "destructive";
  }
}

function formatDate(dateStr: string | null | undefined): string {
  if (!dateStr) return "-";
  return new Date(dateStr).toLocaleDateString();
}

function ApiKeys() {
  usePageTitle("API Keys");
  const queryClient = useQueryClient();
  const { isAdmin } = usePermission();
  const { user } = useAuth();
  const [createOpen, setCreateOpen] = useState(false);
  const [createdKey, setCreatedKey] = useState<string | null>(null);
  const [revokeId, setRevokeId] = useState<string | null>(null);
  const [nameFilter, setNameFilter] = useState("");
  const [statusFilter, setStatusFilter] = useState("all");
  const [sort, setSort] = useState<{
    column: string;
    direction: "asc" | "desc";
  }>({
    column: "name",
    direction: "asc",
  });

  const anyDialogOpen = createOpen || createdKey !== null || revokeId !== null;

  const { data: apiKeys = EMPTY_API_KEYS, isLoading } = useQuery({
    queryKey: ["apiKeys", user?.userId],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/apikeys");
      if (error) throw error;
      return data ?? [];
    },
  });

  const filterKey = useCallback(
    (key: ApiKey) =>
      key.name
        .toLocaleLowerCase()
        .includes(nameFilter.trim().toLocaleLowerCase()) &&
      (statusFilter === "all" || getKeyStatus(key) === statusFilter),
    [nameFilter, statusFilter],
  );
  const compareKeys = useCallback(
    (left: ApiKey, right: ApiKey) => {
      const direction = sort.direction === "asc" ? 1 : -1;
      if (sort.column === "status") {
        return (
          direction * getKeyStatus(left).localeCompare(getKeyStatus(right))
        );
      }
      if (
        sort.column === "createdAt" ||
        sort.column === "lastUsedAt" ||
        sort.column === "expiresAt"
      ) {
        const leftDate = left[sort.column]
          ? Date.parse(left[sort.column]!)
          : NaN;
        const rightDate = right[sort.column]
          ? Date.parse(right[sort.column]!)
          : NaN;
        // Missing dates remain last in either direction (never used / no expiry).
        if (!Number.isFinite(leftDate))
          return Number.isFinite(rightDate) ? 1 : 0;
        if (!Number.isFinite(rightDate)) return -1;
        return direction * (leftDate - rightDate);
      }
      return direction * left.name.localeCompare(right.name);
    },
    [sort.column, sort.direction],
  );
  const table = useLocalTable({
    tableId: "api-keys",
    rows: apiKeys,
    filter: filterKey,
    compare: compareKeys,
    resetKey: JSON.stringify([
      nameFilter,
      statusFilter,
      sort.column,
      sort.direction,
    ]),
  });
  const toggleSort = useCallback((column: string) => {
    setSort((previous) => ({
      column,
      direction:
        previous.column === column && previous.direction === "asc"
          ? "desc"
          : "asc",
    }));
  }, []);
  const hasFilters = nameFilter !== "" || statusFilter !== "all";

  const createMutation = useSessionMutation({
    mutationFn: async (values: CreateKeyFormValues) => {
      const { data, error } = await client.POST("/api/apikeys", {
        body: {
          name: values.name,
          expiresAt: values.expiresAt || undefined,
          bypassRateLimit: values.bypassRateLimit,
        },
      });
      if (error) throw error;
      return data;
    },
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ["apiKeys"] });
      if (data) {
        setCreatedKey(data.rawKey);
      }
      setCreateOpen(false);
    },
    onError: () => {
      showError("Failed to create API key.");
    },
  });

  const revokeMutation = useSessionMutation({
    mutationFn: async (id: string) => {
      const { error } = await client.DELETE("/api/apikeys/{id}", {
        params: { path: { id } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["apiKeys"] });
      showSuccess("API key revoked.");
      setRevokeId(null);
    },
    onError: () => {
      showError("Failed to revoke API key.");
    },
  });

  const createForm = useForm<CreateKeyFormValues>({
    resolver: zodResolver(createKeySchema),
    defaultValues: { name: "", expiresAt: "", bypassRateLimit: false },
  });

  const handleCreateOpen = useCallback(() => {
    createForm.reset();
    setCreateOpen(true);
  }, [createForm]);

  useOpenNewItem(handleCreateOpen);

  function handleCreateSubmit(values: CreateKeyFormValues) {
    createMutation.mutate(values);
  }

  async function handleCopyKey() {
    if (!createdKey) return;
    try {
      await navigator.clipboard.writeText(createdKey);
      showSuccess("API key copied to clipboard.");
    } catch {
      showError("Failed to copy to clipboard.");
    }
  }

  const { focusedId, setFocusedIndex, tableRef, containerProps, getRowProps } =
    useListKeyboardNav({
      items: table.items,
      getId: (k) => k.id,
      listId: "api-keys",
      enabled: !anyDialogOpen && table.items.length > 0,
    });

  return (
    <>
      <PageHead
        title="API keys"
        sub="Manage API keys for programmatic access"
        actions={
          <button
            type="button"
            className="btn primary"
            onClick={handleCreateOpen}
          >
            <Icon.Plus /> New API key
          </button>
        }
      />
      <div className="space-y-6">
        <Card>
          <CardHeader>
            <CardTitle>Your API Keys</CardTitle>
            <CardDescription>
              API keys allow external applications to authenticate with your
              account.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <div className="mb-4 flex flex-wrap items-center gap-3">
              <Input
                aria-label="Search API keys"
                placeholder="Search API keys..."
                value={nameFilter}
                onChange={(event) => setNameFilter(event.target.value)}
                className="w-full sm:w-64"
              />
              <Combobox
                aria-label="Filter API keys by status"
                options={STATUS_OPTIONS}
                value={statusFilter}
                onValueChange={setStatusFilter}
                className="w-full sm:w-44"
              />
              {hasFilters && (
                <Button
                  variant="outline"
                  onClick={() => {
                    setNameFilter("");
                    setStatusFilter("all");
                  }}
                >
                  Clear filters
                </Button>
              )}
            </div>
            {isLoading ? (
              <TableSkeleton columns={5} rows={3} showToolbar={false} />
            ) : apiKeys.length === 0 ? (
              <p className="text-muted-foreground">
                No API keys yet. Create one to get started.
              </p>
            ) : table.total === 0 ? (
              <p className="text-muted-foreground">
                No API keys match your filters. Clear filters to see all keys.
              </p>
            ) : (
              <div ref={tableRef} {...containerProps}>
                <Table>
                  <TableHeader>
                    <TableRow>
                      {SORT_COLUMNS.map(({ column, label }) => (
                        <SortableTableHead
                          key={column}
                          column={column}
                          label={label}
                          currentSortBy={sort.column}
                          currentSortDirection={sort.direction}
                          onToggleSort={toggleSort}
                        />
                      ))}
                      <TableHead className="text-right">Actions</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {table.items.map((key, index) => {
                      const status = getKeyStatus(key);
                      return (
                        <TableRow
                          key={key.id}
                          {...getRowProps(key.id)}
                          className={`cursor-pointer ${focusedId === key.id ? "bg-accent" : ""}`}
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
                          <TableCell className="font-medium">
                            {key.name}
                          </TableCell>
                          <TableCell>{formatDate(key.createdAt)}</TableCell>
                          <TableCell>{formatDate(key.lastUsedAt)}</TableCell>
                          <TableCell>{formatDate(key.expiresAt)}</TableCell>
                          <TableCell>
                            <Badge variant={statusBadgeVariant(status)}>
                              {capitalize(status)}
                            </Badge>
                          </TableCell>
                          <TableCell className="text-right">
                            {status === "active" && (
                              <Button
                                variant="destructive"
                                size="sm"
                                onClick={() => setRevokeId(key.id)}
                              >
                                Revoke
                              </Button>
                            )}
                          </TableCell>
                        </TableRow>
                      );
                    })}
                  </TableBody>
                </Table>
              </div>
            )}
            {!isLoading && (
              <div className="mt-4">
                <Pagination
                  currentPage={table.currentPage}
                  totalItems={table.total}
                  pageSize={table.pageSize}
                  totalPages={table.totalPages}
                  onPageChange={table.setPage}
                  onPageSizeChange={table.setPageSize}
                />
              </div>
            )}
          </CardContent>
        </Card>

        {/* Create API Key Dialog */}
        <Dialog open={createOpen} onOpenChange={setCreateOpen}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>Create API Key</DialogTitle>
              <DialogDescription>
                Generate a new API key for programmatic access.
              </DialogDescription>
            </DialogHeader>
            <Form {...createForm}>
              <form
                onSubmit={createForm.handleSubmit(handleCreateSubmit)}
                className="space-y-4"
              >
                <FormField
                  control={createForm.control}
                  name="name"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel required>Name</FormLabel>
                      <FormControl>
                        <Input
                          placeholder="e.g. Paperless Integration"
                          {...field}
                        />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={createForm.control}
                  name="expiresAt"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>Expiration Date (optional)</FormLabel>
                      <FormControl>
                        <DateInput {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                {isAdmin() && (
                  <FormField
                    control={createForm.control}
                    name="bypassRateLimit"
                    render={({ field }) => (
                      <FormItem className="flex items-center gap-2">
                        <FormControl>
                          <Checkbox
                            checked={field.value}
                            onCheckedChange={field.onChange}
                          />
                        </FormControl>
                        <FormLabel className="!mt-0">
                          Bypass rate limiting
                        </FormLabel>
                      </FormItem>
                    )}
                  />
                )}
                <Button
                  type="submit"
                  className="w-full"
                  disabled={createMutation.isPending}
                >
                  {createMutation.isPending && <Spinner size="sm" />}
                  {createMutation.isPending ? "Creating..." : "Create Key"}
                </Button>
              </form>
            </Form>
          </DialogContent>
        </Dialog>

        {/* Created Key Display Dialog */}
        <Dialog
          open={createdKey !== null}
          onOpenChange={(open) => {
            if (!open) setCreatedKey(null);
          }}
        >
          <DialogContent>
            <DialogHeader>
              <DialogTitle>API Key Created</DialogTitle>
            </DialogHeader>
            <Alert>
              <AlertDescription>
                Save this key now. You won&apos;t be able to see it again.
              </AlertDescription>
            </Alert>
            <Input
              readOnly
              value={createdKey ?? ""}
              onClick={(e) => (e.target as HTMLInputElement).select()}
              className="font-mono text-sm"
            />
            <Button onClick={handleCopyKey} className="w-full">
              Copy to Clipboard
            </Button>
          </DialogContent>
        </Dialog>

        {/* Revoke Confirmation Dialog */}
        <AlertDialog
          open={revokeId !== null}
          onOpenChange={(open) => {
            if (!open) setRevokeId(null);
          }}
        >
          <AlertDialogContent>
            <AlertDialogHeader>
              <AlertDialogTitle>Revoke API Key</AlertDialogTitle>
              <AlertDialogDescription>
                This action cannot be undone. The API key will be immediately
                invalidated.
              </AlertDialogDescription>
            </AlertDialogHeader>
            <AlertDialogFooter>
              <AlertDialogCancel onClick={() => setRevokeId(null)}>
                Cancel
              </AlertDialogCancel>
              <Button
                variant="destructive"
                disabled={revokeMutation.isPending}
                onClick={() => {
                  if (revokeId) revokeMutation.mutate(revokeId);
                }}
              >
                {revokeMutation.isPending && <Spinner size="sm" />}
                {revokeMutation.isPending ? "Revoking..." : "Revoke"}
              </Button>
            </AlertDialogFooter>
          </AlertDialogContent>
        </AlertDialog>
      </div>
    </>
  );
}

export default ApiKeys;
