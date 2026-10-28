import { useMemo, useRef, useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router";
import { MoreHorizontal, Plus } from "lucide-react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import type { components } from "@/generated/api";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { extractErrorMessage } from "@/lib/problem-details";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import { usePermission } from "@/hooks/usePermission";
import { usePageTitle } from "@/hooks/usePageTitle";
import { CategoryTree } from "@/components/categories/CategoryTree";
import { PageHead } from "@/components/primitives";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

type Revision = components["schemas"]["TaxonomyRevisionResponse"];
type Node = components["schemas"]["TaxonomyNodeResponse"];
type Change = components["schemas"]["TaxonomyChange"];
type Request = components["schemas"]["TaxonomyChangeRequest"];
type Action = Exclude<Change["kind"], "define">;
const titles: Record<Action, string> = {
  create: "New category",
  rename: "Rename category",
  move: "Move category",
  archive: "Archive category",
  restore: "Restore category",
};

// Iterative traversal keeps paths and subtree scope independent of hierarchy depth.
function project(nodes: Node[]) {
  const byId = new Map(nodes.map((node) => [node.id, node]));
  return nodes
    .map((node) => {
      const ancestors: Node[] = [];
      const seen = new Set([node.id]);
      let parent = node.parentId ? byId.get(node.parentId) : undefined;
      while (parent && !seen.has(parent.id)) {
        seen.add(parent.id);
        ancestors.push(parent);
        parent = parent.parentId ? byId.get(parent.parentId) : undefined;
      }
      return {
        ...node,
        path: [...ancestors.reverse().map((p) => p.name), node.name].join(
          " / ",
        ),
        ancestors: seen,
      };
    })
    .sort((a, b) => a.path.localeCompare(b.path) || a.id.localeCompare(b.id));
}

interface Editor {
  action: Action;
  node?: Node;
  parentId?: string;
  revision: Revision;
}

function TaxonomyEditor({
  editor,
  onClose,
  onCreated,
  onRestoreFocus,
}: {
  editor: Editor;
  onClose: () => void;
  onCreated: (categoryId: string, revision: Revision) => void;
  onRestoreFocus: () => void;
}) {
  const { isAdmin } = usePermission();
  const cache = useQueryClient();
  const { action, node } = editor;
  const [revision, setRevision] = useState(editor.revision);
  const [refreshed, setRefreshed] = useState(false);
  const [name, setName] = useState(
    action === "rename" ? (node?.name ?? "") : "",
  );
  const [parentId, setParentId] = useState(
    action === "move" ? (node?.parentId ?? "") : (editor.parentId ?? ""),
  );
  const [reason, setReason] = useState("");
  const [request, setRequest] = useState<Request>();
  const [refreshError, setRefreshError] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const rows = useMemo(() => project(revision.nodes), [revision.nodes]);
  const subtree = rows.filter((row) => node && row.ancestors.has(node.id));
  const scope =
    action === "create"
      ? 1
      : action === "archive"
        ? subtree.filter((row) => !row.archived).length
        : action === "move" || action === "rename"
          ? subtree.length
          : 1;
  const mutation = useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async (body: Request) => {
      const { data, error, response } = await client.POST(
        "/api/taxonomy/changes",
        { ...localErrorPolicy.request, body },
      );
      if (error || !response.ok)
        throw {
          status: response.status,
          detail:
            extractErrorMessage(error) ??
            "The change could not be saved. Try again.",
        };
      return data!;
    },
    onError: (error) => {
      const status = (error as { status?: number }).status;
      if (status && status >= 400 && status < 500 && status !== 409)
        setRequest(undefined);
    },
    onSuccess: async (data, body) => {
      // A background read may predate this change, including one started while
      // the change was saving. Cancel it before publishing the saved revision.
      await cache.cancelQueries({
        queryKey: ["taxonomy", "current"],
        exact: true,
      });
      cache.setQueryData(["taxonomy", "current"], data);
      await cache.cancelQueries({
        queryKey: ["dashboard", "spending-by-category", "currentHierarchy"],
      });
      await cache.invalidateQueries({
        queryKey: ["dashboard", "spending-by-category", "currentHierarchy"],
      });
      const change = body.changes[0];
      if (change.kind === "create") onCreated(change.categoryId, data);
      onClose();
    },
  });
  const conflict =
    (mutation.error as { status?: number } | null)?.status === 409;
  function submit(event: FormEvent) {
    event.preventDefault();
    if (!isAdmin() || conflict) return;
    const change: Change = {
      kind: action,
      categoryId: node?.id ?? crypto.randomUUID(),
    };
    if (action === "create" || action === "rename") change.name = name;
    if (action === "create" || action === "move")
      change.parentId = parentId || null;
    const body = request ?? {
      expectedRevisionId: revision.id,
      operationId: crypto.randomUUID(),
      reason,
      changes: [change],
    };
    setRequest(body);
    mutation.mutate(body);
  }
  async function refresh() {
    setRefreshing(true);
    setRefreshError(false);
    try {
      const latest = await cache.fetchQuery({
        queryKey: ["taxonomy", "current"],
        staleTime: 0,
        queryFn: async ({ signal }) => {
          const { data, error } = await client.GET("/api/taxonomy", {
            ...localErrorPolicy.request,
            signal,
          });
          if (error || !data) throw error ?? new Error("No taxonomy returned");
          return data;
        },
      });
      setRevision(latest);
      setRequest(undefined);
      setRefreshed(true);
      mutation.reset();
    } catch {
      setRefreshError(true);
    } finally {
      setRefreshing(false);
    }
  }
  const locked = mutation.isPending || !!request;
  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !mutation.isPending) onClose();
      }}
    >
      <DialogContent
        className="max-h-[90dvh] overflow-y-auto"
        onCloseAutoFocus={(event) => {
          // The editor has no DialogTrigger. Restore after Radix releases its
          // focus scope, using the current page refs if the mutation hid a row.
          event.preventDefault();
          onRestoreFocus();
        }}
      >
        <DialogHeader>
          <DialogTitle>{titles[action]}</DialogTitle>
          <DialogDescription>
            {node
              ? rows.find((row) => row.id === node.id)?.path
              : editor.parentId
                ? `Add a child category under ${rows.find((row) => row.id === editor.parentId)?.path ?? "the selected category"}.`
                : "Choose a place in the hierarchy."}
          </DialogDescription>
        </DialogHeader>
        <form onSubmit={submit} className="space-y-4">
          <p className="text-sm" aria-live="polite">
            {scope} {scope === 1 ? "category" : "categories"} affected.{" "}
            {action === "archive"
              ? "The entire subtree will be archived. Existing purchase history is retained."
              : action === "restore"
                ? "Only this category is restored; its ancestors must be active."
                : "Existing purchase assignments are unchanged."}
          </p>
          {refreshed && (
            <p role="status" className="text-sm">
              Tree refreshed. Your draft is preserved. Review the updated scope
              before saving.
            </p>
          )}
          <fieldset disabled={locked || !isAdmin()} className="space-y-4">
            {(action === "create" || action === "rename") && (
              <div className="space-y-2">
                <Label htmlFor="taxonomy-name">Name</Label>
                <Input
                  id="taxonomy-name"
                  required
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                />
              </div>
            )}
            {(action === "create" || action === "move") && (
              <div className="space-y-2">
                <Label htmlFor="taxonomy-parent">Parent</Label>
                <select
                  id="taxonomy-parent"
                  className="w-full rounded-md border bg-background p-2"
                  value={parentId}
                  onChange={(e) => setParentId(e.target.value)}
                >
                  <option value="">Top level (no parent)</option>
                  {rows
                    .filter(
                      (row) =>
                        !row.archived &&
                        !(
                          action === "move" &&
                          node &&
                          row.ancestors.has(node.id)
                        ),
                    )
                    .map((row) => (
                      <option key={row.id} value={row.id}>
                        {row.path}
                      </option>
                    ))}
                </select>
              </div>
            )}
            <div className="space-y-2">
              <Label htmlFor="taxonomy-reason">Reason</Label>
              <Input
                id="taxonomy-reason"
                required
                value={reason}
                onChange={(e) => setReason(e.target.value)}
              />
            </div>
          </fieldset>
          {mutation.error && (
            <div role="alert" className="space-y-2 text-sm">
              <p>
                {extractErrorMessage(mutation.error) ??
                  "The change could not be saved. Retry the same change."}
              </p>
              {conflict && (
                <>
                  <p>
                    Refresh the tree, then review your draft against its current
                    scope.
                  </p>
                  <Button
                    type="button"
                    variant="outline"
                    disabled={refreshing}
                    onClick={() => void refresh()}
                  >
                    {refreshing ? "Refreshing…" : "Refresh tree"}
                  </Button>
                </>
              )}
              {refreshError && <p>Refresh failed. Try again.</p>}
            </div>
          )}
          <div className="flex flex-wrap justify-end gap-2">
            <Button
              type="button"
              variant="outline"
              disabled={mutation.isPending}
              onClick={onClose}
            >
              Cancel
            </Button>
            <Button
              type="submit"
              disabled={
                !isAdmin() ||
                conflict ||
                mutation.isPending ||
                !reason.trim() ||
                ((action === "create" || action === "rename") &&
                  (!name || name !== name.trim()))
              }
            >
              {mutation.isPending
                ? "Saving…"
                : request
                  ? "Retry change"
                  : titles[action]}
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

export default function Taxonomy() {
  usePageTitle("Categories");
  const { isAdmin } = usePermission();
  const [params] = useSearchParams();
  const historicalId = params.get("revision");
  const [search, setSearch] = useState("");
  const [showArchived, setShowArchived] = useState(false);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [editor, setEditor] = useState<Editor>();
  const editorOpener = useRef<HTMLButtonElement>(null);
  const categoryActionButtons = useRef(new Map<string, HTMLButtonElement>());
  const newCategoryButton = useRef<HTMLButtonElement>(null);
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["taxonomy", historicalId ?? "current"],
    queryFn: async ({ signal }) => {
      const result = historicalId
        ? await client.GET("/api/taxonomy/revisions/{id}", {
            ...localErrorPolicy.request,
            params: { path: { id: historicalId } },
            signal,
          })
        : await client.GET("/api/taxonomy", {
            ...localErrorPolicy.request,
            signal,
          });
      if (result.error || !result.data)
        throw result.error ?? new Error("No taxonomy returned");
      return result.data;
    },
  });
  const editable = isAdmin() && !historicalId;
  return (
    <>
      <nav
        aria-label="Classification workspace"
        className="mb-4 flex flex-wrap gap-3 text-sm"
      >
        <Link className="underline" to="/classification">
          Suggestions
        </Link>
        <span aria-current="page" className="font-semibold">
          Browse categories
        </span>
        <Link className="underline" to="/categories/cleanup">
          Clean up history
        </Link>
      </nav>
      <PageHead
        title="Categories"
        actions={
          editable &&
          query.data && (
            <Button
              ref={newCategoryButton}
              onClick={(event) => {
                editorOpener.current = event.currentTarget;
                setEditor({ action: "create", revision: query.data! });
              }}
            >
              New category
            </Button>
          )
        }
      />
      <p className="mb-4 max-w-2xl text-sm text-muted-foreground">
        Start with broad categories, then add child categories wherever more
        detail helps. Expand a category to see what belongs inside it.
      </p>
      {historicalId && (
        <p className="mb-4">
          Historical revision ·{" "}
          <Link className="underline" to="/categories">
            Back to categories
          </Link>
        </p>
      )}
      <div className="mb-4 flex flex-wrap items-center gap-4">
        <div className="min-w-0 flex-1">
          <Label htmlFor="taxonomy-search" className="sr-only">
            Search category paths
          </Label>
          <Input
            id="taxonomy-search"
            placeholder="Search category paths…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </div>
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={showArchived}
            onChange={(e) => setShowArchived(e.target.checked)}
          />
          Show archived
        </label>
      </div>
      {query.isPending && <p role="status">Loading category tree…</p>}
      {query.isError && (
        <div role="alert">
          <p>
            {extractErrorMessage(query.error) ?? "Could not load categories."}
          </p>
          <Button variant="outline" onClick={() => void query.refetch()}>
            Retry
          </Button>
        </div>
      )}
      {query.data && (
        <>
          <details className="mb-4 rounded-md border p-3">
            <summary className="cursor-pointer">
              Revision details · {query.data.sequence}
            </summary>
            <dl className="mt-2 space-y-2 break-words text-sm">
              <dt>Revision</dt>
              <dd>{query.data.id}</dd>
              <dt>Recorded</dt>
              <dd>{query.data.recordedAt}</dd>
              <dt>Actor</dt>
              <dd>{query.data.actor}</dd>
              <dt>Reason</dt>
              <dd>{query.data.reason}</dd>
            </dl>
            {query.data.previousRevisionId && (
              <Link
                className="mt-2 inline-block text-sm underline"
                to={`/categories?revision=${query.data.previousRevisionId}`}
              >
                Previous revision
              </Link>
            )}
          </details>
          <CategoryTree
            nodes={query.data.nodes}
            search={search}
            showArchived={showArchived}
            expansion={
              historicalId
                ? undefined
                : { ids: expanded, onChange: setExpanded }
            }
            renderActions={
              editable
                ? (node, path, focused) => (
                    <div className="flex shrink-0 items-center gap-1 text-muted-foreground">
                      {!node.archived && (
                        <Button
                          variant="ghost"
                          size="icon"
                          className="size-9"
                          tabIndex={focused ? 0 : -1}
                          aria-label={`Add child category: ${path}`}
                          title={`Add child category to ${node.name}`}
                          onClick={(event) => {
                            editorOpener.current = event.currentTarget;
                            setEditor({
                              action: "create",
                              parentId: node.id,
                              revision: query.data!,
                            });
                          }}
                        >
                          <Plus aria-hidden="true" className="size-4" />
                        </Button>
                      )}
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <Button
                            variant="ghost"
                            size="icon"
                            className="size-9"
                            tabIndex={focused ? 0 : -1}
                            aria-label={`Category actions: ${path}`}
                            title={`Actions for ${node.name}`}
                            ref={(element) => {
                              if (element)
                                categoryActionButtons.current.set(
                                  node.id,
                                  element,
                                );
                              else
                                categoryActionButtons.current.delete(node.id);
                            }}
                          >
                            <MoreHorizontal
                              aria-hidden="true"
                              className="size-4"
                            />
                          </Button>
                        </DropdownMenuTrigger>
                        <DropdownMenuContent
                          align="end"
                          onCloseAutoFocus={(event) => {
                            if (editor) event.preventDefault();
                          }}
                        >
                          {(node.archived
                            ? (["restore"] as const)
                            : (["rename", "move", "archive"] as const)
                          ).map((action) => (
                            <DropdownMenuItem
                              key={action}
                              className="min-h-9"
                              variant={
                                action === "archive" ? "destructive" : "default"
                              }
                              aria-label={`${titles[action]}: ${path}`}
                              onSelect={() => {
                                // Pointer opening need not focus a Radix menu trigger.
                                // Retain the trigger itself before its menu unmounts.
                                editorOpener.current =
                                  categoryActionButtons.current.get(node.id) ??
                                  null;
                                setEditor({
                                  action,
                                  node,
                                  revision: query.data!,
                                });
                              }}
                            >
                              {action[0].toUpperCase() + action.slice(1)}
                            </DropdownMenuItem>
                          ))}
                        </DropdownMenuContent>
                      </DropdownMenu>
                    </div>
                  )
                : undefined
            }
          />
        </>
      )}
      {editor && editable && (
        <TaxonomyEditor
          editor={editor}
          onClose={() => setEditor(undefined)}
          onCreated={(categoryId, revision) => {
            if (!editable) return;
            const created = project(revision.nodes).find(
              (row) => row.id === categoryId,
            );
            if (!created || created.archived) return;
            setExpanded((previous) => {
              const next = new Set(previous);
              for (const id of created.ancestors) {
                if (id !== categoryId) next.add(id);
              }
              return next;
            });
            // Keep a matching search, but do not let an unrelated filter hide
            // the category the user has just successfully created.
            if (
              !created.path
                .toLocaleLowerCase()
                .includes(search.trim().toLocaleLowerCase())
            ) {
              setSearch("");
            }
          }}
          onRestoreFocus={() => {
            const opener = editorOpener.current;
            (opener?.isConnected ? opener : newCategoryButton.current)?.focus();
          }}
        />
      )}
    </>
  );
}
