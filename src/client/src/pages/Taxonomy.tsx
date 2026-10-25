import { useMemo, useRef, useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import type { components } from "@/generated/api";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { extractErrorMessage } from "@/lib/problem-details";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import { usePermission } from "@/hooks/usePermission";
import { usePageTitle } from "@/hooks/usePageTitle";
import { PageHead } from "@/components/primitives";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
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
  revision: Revision;
}

function TaxonomyEditor({
  editor,
  onClose,
  onRestoreFocus,
}: {
  editor: Editor;
  onClose: () => void;
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
    action === "move" ? (node?.parentId ?? "") : "",
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
    onSuccess: (data) => {
      cache.setQueryData(["taxonomy", "current"], data);
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
                  <option value="">Root</option>
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
  usePageTitle("Category tree");
  const { isAdmin } = usePermission();
  const [params] = useSearchParams();
  const historicalId = params.get("revision");
  const [search, setSearch] = useState("");
  const [showArchived, setShowArchived] = useState(false);
  const [editor, setEditor] = useState<Editor>();
  const editorOpener = useRef<HTMLButtonElement>(null);
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
  const rows = useMemo(
    () => project(query.data?.nodes ?? []),
    [query.data?.nodes],
  );
  const visible = rows.filter(
    (row) =>
      (showArchived || !row.archived) &&
      row.path.toLocaleLowerCase().includes(search.toLocaleLowerCase()),
  );
  const editable = isAdmin() && !historicalId;
  return (
    <>
      <PageHead
        title="Category tree"
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
      <p className="mb-4 text-sm text-muted-foreground">
        Receipt categories are still available in{" "}
        <Link className="underline" to="/categories">
          Categories
        </Link>{" "}
        and{" "}
        <Link className="underline" to="/subcategories">
          Subcategories
        </Link>{" "}
        while legacy data is migrated.
      </p>
      {historicalId && (
        <p className="mb-4">
          Historical revision ·{" "}
          <Link className="underline" to="/taxonomy">
            Back to current tree
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
            {extractErrorMessage(query.error) ??
              "Could not load the category tree."}
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
                to={`/taxonomy?revision=${query.data.previousRevisionId}`}
              >
                Previous revision
              </Link>
            )}
          </details>
          {!visible.length && (
            <p>
              {rows.length
                ? "No matching categories."
                : "The category tree is empty. Legacy receipt categories remain available above."}
            </p>
          )}
          <ul aria-label="Category paths" className="space-y-2">
            {visible.map((node) => (
              <li
                key={node.id}
                className="flex flex-col gap-3 rounded-md border p-3 sm:flex-row sm:items-center sm:justify-between"
              >
                <div className="min-w-0">
                  <p className="break-words font-medium">
                    {node.path}
                    {node.archived && (
                      <span className="ml-2 text-sm text-muted-foreground">
                        Archived
                      </span>
                    )}
                  </p>
                  {node.definition && (
                    <p className="mt-1 break-words text-sm text-muted-foreground">
                      {node.definition}
                    </p>
                  )}
                  <details className="mt-1 text-sm text-muted-foreground">
                    <summary className="cursor-pointer">
                      Category details
                    </summary>
                    <p className="break-all">{node.id}</p>
                  </details>
                </div>
                {editable && (
                  <div className="flex shrink-0 flex-wrap gap-1">
                    {(node.archived
                      ? (["restore"] as const)
                      : (["rename", "move", "archive"] as const)
                    ).map((action) => (
                      <Button
                        key={action}
                        variant="outline"
                        size="sm"
                        onClick={(event) => {
                          editorOpener.current = event.currentTarget;
                          setEditor({ action, node, revision: query.data! });
                        }}
                        aria-label={`${titles[action]}: ${node.path}`}
                      >
                        {action[0].toUpperCase() + action.slice(1)}
                      </Button>
                    ))}
                  </div>
                )}
              </li>
            ))}
          </ul>
        </>
      )}
      {editor && editable && (
        <TaxonomyEditor
          editor={editor}
          onClose={() => setEditor(undefined)}
          onRestoreFocus={() => {
            const opener = editorOpener.current;
            (opener?.isConnected ? opener : newCategoryButton.current)?.focus();
          }}
        />
      )}
    </>
  );
}
