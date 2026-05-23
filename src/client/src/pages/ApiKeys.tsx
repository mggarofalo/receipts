import { useState, useCallback } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { usePageTitle } from "@/hooks/usePageTitle";
import { usePermission } from "@/hooks/usePermission";
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
  const [createOpen, setCreateOpen] = useState(false);
  const [createdKey, setCreatedKey] = useState<string | null>(null);
  const [revokeId, setRevokeId] = useState<string | null>(null);

  const anyDialogOpen = createOpen || createdKey !== null || revokeId !== null;

  const { data: apiKeys = [], isLoading } = useQuery({
    queryKey: ["apiKeys"],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/apikeys");
      if (error) throw error;
      return data ?? [];
    },
  });

  const createMutation = useMutation({
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

  const revokeMutation = useMutation({
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

  const { focusedId, setFocusedIndex, tableRef } = useListKeyboardNav({
    items: apiKeys as { id: string }[],
    getId: (k) => k.id,
    enabled: !anyDialogOpen && apiKeys.length > 0,
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
          {isLoading ? (
            <TableSkeleton columns={5} rows={3} showToolbar={false} />
          ) : apiKeys.length === 0 ? (
            <p className="text-muted-foreground">
              No API keys yet. Create one to get started.
            </p>
          ) : (
            <div ref={tableRef}>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Name</TableHead>
                    <TableHead>Created</TableHead>
                    <TableHead>Last Used</TableHead>
                    <TableHead>Expires</TableHead>
                    <TableHead>Status</TableHead>
                    <TableHead className="text-right">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {apiKeys.map((key, index) => {
                    const status = getKeyStatus(key);
                    return (
                      <TableRow
                        key={key.id}
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
