import { useMemo, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import type { components } from "@/generated/api";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { extractErrorMessage } from "@/lib/problem-details";
import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import { usePageTitle } from "@/hooks/usePageTitle";
import { CategoryPathPicker } from "@/components/categories/CategoryPathPicker";
import type { CategoryChoice } from "@/lib/category-paths";
import { categoryPaths } from "@/lib/category-paths";
import { PageHead } from "@/components/primitives";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

type S = components["schemas"];
type Group = S["HistoricalGroupSummary"];
type Preview = S["HistoricalPreviewBody"];
type Disposition = S["HistoricalDispositionRequest"];
type Progress = S["HistoricalProgress"];
type Filter = S["HistoricalDispositionFilter"];
const groupId = (group: S["HistoricalGroupKey"]) => JSON.stringify(group);
const message = (error: unknown) =>
  extractErrorMessage(error) ?? "Request failed. Try again.";

export default function HistoricalCleanup() {
  usePageTitle("Clean up history");
  const cache = useQueryClient();
  const [params, setParams] = useSearchParams();
  const operationId = params.get("operation");
  const [search, setSearch] = useState("");
  const [queryText, setQueryText] = useState("");
  const [offset, setOffset] = useState(0);
  const [stateFilter, setStateFilter] = useState("All");
  const [dispositionFilter, setDispositionFilter] = useState<Filter>("visible");
  const [sort, setSort] = useState<"mostPurchases" | "largestAmount">(
    "mostPurchases",
  );
  const [selected, setSelected] = useState<Record<string, Group>>({});
  const [targets, setTargets] = useState<Record<string, CategoryChoice>>({});
  const [bulkTarget, setBulkTarget] = useState<CategoryChoice>();
  const [reason, setReason] = useState("");
  const [undoReason, setUndoReason] = useState("");
  const [itemOffset, setItemOffset] = useState<number | string>(0);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState("");
  const [previewIntent, setPreviewIntent] = useState<Preview>();
  const dispositionIntents = useRef<Record<string, Disposition>>({});
  const undoIntents = useRef<Record<string, S["HistoricalUndoBody"]>>({});
  const taxonomy = useCategoryTaxonomy();
  const groups = useQuery({
    ...localErrorPolicy.query,
    queryKey: [
      "historical-groups",
      offset,
      queryText,
      stateFilter,
      dispositionFilter,
      sort,
    ],
    queryFn: async ({ signal }) => {
      const { data, error: requestError } = await client.GET(
        "/api/historical-reclassifications/groups",
        {
          ...localErrorPolicy.request,
          signal,
          params: {
            query: {
              offset,
              limit: 25,
              q: queryText || undefined,
              conflictsOnly: stateFilter === "Conflict" || undefined,
              migratedBroadOnly: stateFilter === "Broad" || undefined,
              unresolvedOnly: stateFilter === "Unresolved" || undefined,
              unassignedOnly: stateFilter === "Unassigned" || undefined,
              dispositionFilter,
              sort,
              impactCurrency: "USD",
            },
          },
        },
      );
      if (requestError || !data)
        throw requestError ?? new Error("Groups unavailable");
      return data;
    },
  });
  const operations = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["historical-operations"],
    queryFn: async ({ signal }) => {
      const { data, error: requestError } = await client.GET(
        "/api/historical-reclassifications",
        {
          ...localErrorPolicy.request,
          signal,
          params: { query: { offset: 0, limit: 20 } },
        },
      );
      if (requestError || !data)
        throw requestError ?? new Error("Operations unavailable");
      return data;
    },
  });
  const operation = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["historical-operation", operationId],
    enabled: !!operationId,
    queryFn: async ({ signal }) => {
      const { data, error: requestError } = await client.GET(
        "/api/historical-reclassifications/{id}",
        {
          ...localErrorPolicy.request,
          signal,
          params: { path: { id: operationId! } },
        },
      );
      if (requestError || !data)
        throw requestError ?? new Error("Operation unavailable");
      return data;
    },
  });
  const summary = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["historical-operation-summary", operationId],
    enabled: !!operationId,
    queryFn: async ({ signal }) => {
      const { data, error: requestError } = await client.GET(
        "/api/historical-reclassifications/{id}/summary",
        {
          ...localErrorPolicy.request,
          signal,
          params: { path: { id: operationId! } },
        },
      );
      if (requestError || !data)
        throw requestError ?? new Error("Captured choices unavailable");
      return data;
    },
  });
  const items = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["historical-operation-items", operationId, itemOffset],
    enabled: !!operationId,
    queryFn: async ({ signal }) => {
      const { data, error: requestError } = await client.GET(
        "/api/historical-reclassifications/{id}/items",
        {
          ...localErrorPolicy.request,
          signal,
          params: {
            path: { id: operationId! },
            query: { offset: itemOffset, limit: 100 },
          },
        },
      );
      if (requestError || !data)
        throw requestError ?? new Error("Items unavailable");
      return data;
    },
  });
  const visible = groups.data?.items ?? [];
  const selectedRows = visible.filter((row) => selected[groupId(row.group)]);
  const selectedVisible = visible.filter((row) => selected[groupId(row.group)]);
  const allVisibleSelected =
    visible.length > 0 && selectedVisible.length === visible.length;
  const revision = taxonomy.data;
  const captured = summary.data;
  const paths = revision ? categoryPaths(revision.nodes) : [];
  const choices = useMemo(
    () =>
      selectedRows.map((row) => ({
        group: row.group,
        targetCategoryId: (targets[groupId(row.group)] ?? bulkTarget)
          ?.categoryId,
      })),
    [selectedRows, targets, bulkTarget],
  );

  function chooseOperation(id: string | null) {
    setParams(id ? { operation: id } : {});
    setItemOffset(0);
    setError("");
  }
  function changePage(nextOffset: number) {
    setSelected({});
    setTargets({});
    setBulkTarget(undefined);
    setOffset(nextOffset);
  }
  function toggle(row: Group) {
    if (previewIntent) return;
    const id = groupId(row.group);
    setSelected((current) => {
      const next = { ...current };
      if (next[id]) delete next[id];
      else if (Object.keys(next).length < 25) next[id] = row;
      return next;
    });
    setPreviewIntent(undefined);
  }
  async function setDisposition(row: Group, value: Disposition["disposition"]) {
    const key = groupId(row.group);
    const intent = dispositionIntents.current[key] ?? {
      group: row.group,
      disposition: value,
      expectedVersion: row.dispositionVersion,
      operationId: crypto.randomUUID(),
    };
    // Preserve this exact request after a timeout; changing the action requires a fresh read.
    dispositionIntents.current[key] = intent;
    setBusy(key);
    setError("");
    try {
      const { data, error: requestError } = await client.PUT(
        "/api/historical-reclassifications/groups/disposition",
        {
          ...localErrorPolicy.request,
          body: intent,
        },
      );
      if (requestError || !data)
        throw requestError ?? new Error("Disposition unavailable");
      delete dispositionIntents.current[key];
      setSelected((current) => {
        const next = { ...current };
        delete next[key];
        return next;
      });
      await groups.refetch();
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy("");
    }
  }
  async function preview() {
    if (
      !revision ||
      !reason.trim() ||
      !choices.length ||
      choices.some((choice) => choice.targetCategoryId === undefined)
    )
      return;
    const intent = previewIntent ?? {
      operationId: crypto.randomUUID(),
      taxonomyRevisionId: revision.id,
      reason: reason.trim(),
      groups: choices as S["HistoricalGroupChoiceBody"][],
    };
    setPreviewIntent(intent);
    setBusy("preview");
    setError("");
    try {
      const { data, error: requestError } = await client.POST(
        "/api/historical-reclassifications",
        {
          ...localErrorPolicy.request,
          body: intent,
        },
      );
      if (requestError || !data)
        throw requestError ?? new Error("Preview unavailable");
      setPreviewIntent(undefined);
      chooseOperation(intent.operationId);
      await cache.invalidateQueries({ queryKey: ["historical-operations"] });
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy("");
    }
  }
  async function advance(action: "apply" | "cancel" | "undo") {
    if (!operationId) return;
    setBusy(action);
    setError("");
    try {
      let progress: Progress | undefined;
      let requestError: unknown;
      if (action === "apply") {
        const result = await client.POST(
          "/api/historical-reclassifications/{id}/apply",
          {
            ...localErrorPolicy.request,
            params: { path: { id: operationId } },
            body: { limit: 100 },
          },
        );
        progress = result.data;
        requestError = result.error;
      } else if (action === "cancel") {
        const result = await client.POST(
          "/api/historical-reclassifications/{id}/cancel",
          {
            ...localErrorPolicy.request,
            params: { path: { id: operationId } },
          },
        );
        progress = result.data;
        requestError = result.error;
      } else {
        if (!revision || !undoReason.trim()) return;
        const intent = undoIntents.current[operationId] ?? {
          taxonomyRevisionId: revision.id,
          reason: undoReason.trim(),
          limit: 100,
        };
        undoIntents.current[operationId] = intent;
        const result = await client.POST(
          "/api/historical-reclassifications/{id}/undo",
          {
            ...localErrorPolicy.request,
            params: { path: { id: operationId } },
            body: intent,
          },
        );
        progress = result.data;
        requestError = result.error;
      }
      if (requestError || !progress)
        throw requestError ?? new Error("Progress unavailable");
      cache.setQueryData(["historical-operation", operationId], progress);
      await Promise.all([
        cache.invalidateQueries({ queryKey: ["historical-operations"] }),
        cache.invalidateQueries({
          queryKey: ["historical-operation-items", operationId],
        }),
      ]);
    } catch (cause) {
      setError(`${message(cause)} Refresh progress before retrying.`);
    } finally {
      setBusy("");
    }
  }

  return (
    <div className="mx-auto max-w-5xl space-y-6 pb-12">
      <PageHead title="Clean up history" />
      <p className="text-sm text-muted-foreground">
        Review exact purchase groups and choose where their saved assignments
        should go. Changes happen only when you apply a captured preview.
      </p>
      <Link className="text-sm text-primary underline" to="/categories">
        Back to categories
      </Link>
      {error && (
        <div
          role="alert"
          className="rounded-md border border-destructive p-3 text-sm"
        >
          {error}{" "}
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              void groups.refetch();
              void operation.refetch();
            }}
          >
            Refresh
          </Button>
        </div>
      )}
      {operationId && (
        <section
          aria-label="Current operation"
          className="space-y-3 rounded-lg border p-4"
        >
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 className="text-lg font-semibold">Captured operation</h2>
            <Button variant="outline" onClick={() => chooseOperation(null)}>
              Back to groups
            </Button>
          </div>
          <p className="break-all text-xs text-muted-foreground">
            Operation ID: {operationId}
          </p>
          {operation.isPending && <p role="status">Loading operation…</p>}
          {operation.isError && (
            <p role="alert">
              {message(operation.error)}{" "}
              <Button
                variant="outline"
                onClick={() => void operation.refetch()}
              >
                Retry
              </Button>
            </p>
          )}
          {summary.isPending && <p role="status">Loading captured choices…</p>}
          {summary.isError && (
            <p role="alert">
              {message(summary.error)}{" "}
              <Button variant="outline" onClick={() => void summary.refetch()}>
                Retry captured choices
              </Button>
            </p>
          )}
          {operation.data && (
            <>
              <p className="font-medium">{operation.data.state}</p>
              <p className="text-sm">
                {operation.data.applied} applied · {operation.data.conflicted}{" "}
                conflicted · {operation.data.skipped} skipped ·{" "}
                {operation.data.pending} pending of {operation.data.total}
              </p>
              <p className="text-sm">
                {operation.data.undone} undone · {operation.data.undoConflicted}{" "}
                undo conflicts · {operation.data.undoSkipped} undo skipped
              </p>
              {captured && (
                <div className="rounded-md border p-3">
                  <h3 className="text-sm font-medium">
                    Captured choices ({captured.groups.length} groups)
                  </h3>
                  <p className="mt-2 text-sm">Reason: {captured.reason}</p>
                  <ul className="mt-2 space-y-1 text-sm">
                    {captured.groups.map((choice) => (
                      <li key={groupId(choice.group)} className="break-words">
                        {choice.group.store} · {choice.group.rawDescription} ·{" "}
                        {choice.group.itemCode ?? "No code"} →{" "}
                        {choice.targetCategoryId === null
                          ? "Unclassified"
                          : `${choice.targetCategoryId} (captured ID; ${
                              paths.find(
                                (entry) =>
                                  entry.node.id === choice.targetCategoryId,
                              )?.label ?? "current label unavailable"
                            }${
                              paths.some(
                                (entry) =>
                                  entry.node.id === choice.targetCategoryId,
                              )
                                ? ", current label"
                                : ""
                            })`}
                      </li>
                    ))}
                  </ul>
                </div>
              )}
              {items.isPending && <p role="status">Loading preview items…</p>}
              {items.isError && (
                <p role="alert">
                  {message(items.error)}{" "}
                  <Button
                    variant="outline"
                    onClick={() => void items.refetch()}
                  >
                    Retry items
                  </Button>
                </p>
              )}
              {items.data && (
                <details open={operation.data.state === "preview"}>
                  <summary className="cursor-pointer text-sm">
                    Review item evidence ({items.data.items.length} shown on
                    this page)
                  </summary>
                  <ul className="mt-2 max-h-72 space-y-2 overflow-auto text-sm">
                    {items.data.items.map(
                      ({ item, state, reason: itemReason }) => (
                        <li
                          key={item.itemId}
                          className="rounded border p-2 break-words"
                        >
                          {item.group.store} · {item.group.rawDescription} ·{" "}
                          {item.group.itemCode ?? "No code"}
                          <br />
                          {state}
                          {itemReason ? ` · ${itemReason}` : ""}
                        </li>
                      ),
                    )}
                  </ul>
                  <div className="mt-2 flex gap-2">
                    <Button
                      variant="outline"
                      size="sm"
                      disabled={itemOffset === 0}
                      onClick={() =>
                        setItemOffset(Math.max(0, Number(itemOffset) - 100))
                      }
                    >
                      Previous items
                    </Button>
                    <Button
                      variant="outline"
                      size="sm"
                      disabled={items.data.nextOffset === null}
                      onClick={() => setItemOffset(items.data!.nextOffset!)}
                    >
                      Next items
                    </Button>
                  </div>
                </details>
              )}
              <div className="flex flex-wrap gap-2">
                {(operation.data.state === "preview" ||
                  operation.data.state === "applying") && (
                  <>
                    <Button
                      disabled={
                        !!busy ||
                        !captured ||
                        Number(operation.data.pending) === 0
                      }
                      onClick={() => void advance("apply")}
                    >
                      Apply next 100
                    </Button>
                    <Button
                      variant="outline"
                      disabled={!!busy}
                      onClick={() => void advance("cancel")}
                    >
                      Cancel remaining
                    </Button>
                  </>
                )}
                {(operation.data.state === "completed" ||
                  operation.data.state === "cancelled" ||
                  operation.data.state === "undoing") && (
                  <div className="flex flex-wrap items-end gap-2">
                    <div>
                      <Label htmlFor="undo-reason">Undo reason</Label>
                      <Input
                        id="undo-reason"
                        value={undoReason}
                        onChange={(e) => setUndoReason(e.target.value)}
                        disabled={!!undoIntents.current[operationId]}
                      />
                    </div>
                    <Button
                      variant="outline"
                      disabled={
                        !!busy ||
                        !revision ||
                        (!undoReason.trim() &&
                          !undoIntents.current[operationId])
                      }
                      onClick={() => void advance("undo")}
                    >
                      Undo next 100 conditionally
                    </Button>
                  </div>
                )}
                <Button
                  variant="ghost"
                  disabled={!!busy}
                  onClick={() => {
                    void operation.refetch();
                    void items.refetch();
                  }}
                >
                  Refresh progress
                </Button>
              </div>
            </>
          )}
        </section>
      )}
      {!operationId && (
        <section aria-label="Purchase groups" className="space-y-4">
          <div className="flex flex-wrap items-end gap-3">
            <form
              className="flex min-w-52 flex-1 gap-2"
              onSubmit={(event) => {
                event.preventDefault();
                changePage(0);
                setQueryText(search);
              }}
            >
              <div className="flex-1">
                <Label htmlFor="group-search">Search groups</Label>
                <Input
                  id="group-search"
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                />
              </div>
              <Button type="submit" variant="outline" className="self-end">
                Search
              </Button>
            </form>
            <label className="text-sm">
              State
              <select
                aria-label="State"
                className="block h-9 rounded-md border bg-background px-2"
                value={stateFilter}
                onChange={(event) => {
                  changePage(0);
                  setStateFilter(event.target.value);
                }}
              >
                {["All", "Conflict", "Broad", "Unresolved", "Unassigned"].map(
                  (value) => (
                    <option key={value}>{value}</option>
                  ),
                )}
              </select>
            </label>
            <label className="text-sm">
              Disposition
              <select
                aria-label="Disposition"
                className="block h-9 rounded-md border bg-background px-2"
                value={dispositionFilter}
                onChange={(event) => {
                  changePage(0);
                  setDispositionFilter(event.target.value as Filter);
                }}
              >
                {(["visible", "all", "later", "leaveAsIs"] as Filter[]).map(
                  (value) => (
                    <option key={value} value={value}>
                      {
                        {
                          visible: "Visible",
                          all: "All",
                          later: "Later",
                          leaveAsIs: "Leave as is",
                        }[value]
                      }
                    </option>
                  ),
                )}
              </select>
            </label>
            <label className="text-sm">
              Order groups
              <select
                aria-label="Order groups"
                className="block h-9 max-w-full rounded-md border bg-background px-2"
                value={sort}
                onChange={(event) => {
                  changePage(0);
                  setSort(event.target.value as typeof sort);
                }}
              >
                <option value="mostPurchases">Most purchases</option>
                <option value="largestAmount">Largest amount (USD)</option>
              </select>
            </label>
          </div>
          {groups.isPending && <p role="status">Loading purchase groups…</p>}
          {groups.isError && (
            <p role="alert">
              {message(groups.error)}{" "}
              <Button variant="outline" onClick={() => void groups.refetch()}>
                Retry groups
              </Button>
            </p>
          )}
          {groups.data && (
            <>
              <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
                <p>
                  {groups.data.totalGroups} groups · showing{" "}
                  {visible.length ? offset + 1 : 0}–{offset + visible.length}
                </p>
                <label className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    checked={allVisibleSelected}
                    disabled={!!previewIntent}
                    onChange={() =>
                      visible.forEach((row) => {
                        if (
                          Boolean(selected[groupId(row.group)]) ===
                          allVisibleSelected
                        )
                          toggle(row);
                      })
                    }
                  />{" "}
                  Select visible page
                </label>
              </div>
              {visible.length === 0 && <p>No groups match these filters.</p>}
              <ul className="space-y-3">
                {visible.map((row) => {
                  const id = groupId(row.group);
                  const pendingDisposition = dispositionIntents.current[id];
                  return (
                    <li key={id} className="rounded-lg border p-4">
                      <div className="flex items-start gap-3">
                        <input
                          type="checkbox"
                          aria-label={`Select ${row.group.rawDescription} at ${row.group.store}`}
                          checked={!!selected[id]}
                          disabled={
                            !!previewIntent ||
                            (!selected[id] && selectedRows.length >= 25)
                          }
                          onChange={() => toggle(row)}
                          className="mt-1"
                        />
                        <div className="min-w-0 flex-1">
                          <h3 className="font-medium break-words">
                            {row.group.rawDescription}
                          </h3>
                          <p className="text-sm text-muted-foreground break-words">
                            {row.group.store} ·{" "}
                            {row.group.itemCode ?? "No code"} ·{" "}
                            {row.purchaseCount} purchases
                          </p>
                          <p className="text-sm text-muted-foreground">
                            USD impact:{" "}
                            {new Intl.NumberFormat("en-US", {
                              style: "currency",
                              currency: row.impactCurrency,
                            }).format(row.impactAmount)}
                          </p>
                          <p className="mt-1 text-xs text-muted-foreground">
                            {[
                              row.conflict && "Conflict",
                              row.migratedBroad && "Broad",
                              row.unresolved && "Unresolved",
                              row.unassigned && "Unassigned",
                            ]
                              .filter(Boolean)
                              .join(" · ") || "Other"}
                          </p>
                          <p className="mt-1 text-xs">
                            Current:{" "}
                            {row.currentCategories
                              .map(
                                (category) =>
                                  `${category.path ?? "Unclassified"} (${category.purchaseCount})`,
                              )
                              .join(", ") || "Unclassified"}
                          </p>
                          <div className="mt-2 flex flex-wrap gap-2">
                            {pendingDisposition ? (
                              <Button
                                size="sm"
                                variant="outline"
                                disabled={!!busy}
                                onClick={() =>
                                  void setDisposition(
                                    row,
                                    pendingDisposition.disposition,
                                  )
                                }
                              >
                                Retry{" "}
                                {pendingDisposition.disposition === "later"
                                  ? "Later"
                                  : pendingDisposition.disposition ===
                                      "leaveAsIs"
                                    ? "Leave as is"
                                    : "reopen"}
                              </Button>
                            ) : row.disposition ? (
                              <Button
                                size="sm"
                                variant="outline"
                                disabled={!!busy}
                                onClick={() => void setDisposition(row, null)}
                              >
                                Reopen
                              </Button>
                            ) : (
                              <>
                                <Button
                                  size="sm"
                                  variant="outline"
                                  disabled={!!busy}
                                  onClick={() =>
                                    void setDisposition(row, "later")
                                  }
                                >
                                  Later
                                </Button>
                                <Button
                                  size="sm"
                                  variant="outline"
                                  disabled={!!busy}
                                  onClick={() =>
                                    void setDisposition(row, "leaveAsIs")
                                  }
                                >
                                  Leave as is
                                </Button>
                              </>
                            )}
                          </div>
                        </div>
                      </div>
                    </li>
                  );
                })}
              </ul>
              <div className="flex gap-2">
                <Button
                  variant="outline"
                  disabled={offset === 0}
                  onClick={() => changePage(Math.max(0, offset - 25))}
                >
                  Previous
                </Button>
                <Button
                  variant="outline"
                  disabled={groups.data.nextOffset === null}
                  onClick={() => changePage(groups.data!.nextOffset!)}
                >
                  Next
                </Button>
              </div>
            </>
          )}
          {selectedRows.length > 0 && (
            <div className="space-y-3 rounded-lg border p-4">
              <h2 className="font-semibold">
                Selected groups ({selectedRows.length}/25)
              </h2>
              <p className="text-sm text-muted-foreground">
                Only selected groups on this page are included. A bulk category
                fills groups without an individual choice.
              </p>
              <div>
                <Label>Bulk destination</Label>
                <CategoryPathPicker
                  aria-label="Bulk destination"
                  revision={revision}
                  value={bulkTarget}
                  disabled={!!previewIntent}
                  onValueChange={(choice) => {
                    setBulkTarget(choice);
                  }}
                  loading={taxonomy.isPending}
                  error={taxonomy.isError}
                />
              </div>
              <ul className="space-y-3">
                {selectedRows.map((row) => {
                  const id = groupId(row.group);
                  return (
                    <li
                      key={id}
                      className="flex flex-col gap-2 border-t pt-3 sm:flex-row sm:items-center"
                    >
                      <span className="min-w-0 flex-1 break-words text-sm">
                        {row.group.rawDescription} · {row.group.store}
                      </span>
                      <div className="min-w-48 flex-1">
                        <CategoryPathPicker
                          aria-label={`Destination for ${row.group.rawDescription}`}
                          revision={revision}
                          value={targets[id]}
                          disabled={!!previewIntent}
                          onValueChange={(choice) => {
                            setTargets((current) => ({
                              ...current,
                              [id]: choice,
                            }));
                          }}
                          loading={taxonomy.isPending}
                          error={taxonomy.isError}
                        />
                      </div>
                      <Button
                        size="sm"
                        variant="ghost"
                        disabled={!!previewIntent}
                        onClick={() => toggle(row)}
                      >
                        Remove
                      </Button>
                    </li>
                  );
                })}
              </ul>
              <div>
                <Label htmlFor="cleanup-reason">
                  Reason for reclassification
                </Label>
                <Input
                  id="cleanup-reason"
                  value={reason}
                  disabled={!!previewIntent}
                  onChange={(event) => setReason(event.target.value)}
                />
              </div>
              <Button
                disabled={
                  !!busy ||
                  !revision ||
                  !reason.trim() ||
                  choices.some(
                    (choice) => choice.targetCategoryId === undefined,
                  )
                }
                onClick={() => void preview()}
              >
                {previewIntent
                  ? "Retry server preview"
                  : "Capture server preview"}
              </Button>
              {previewIntent && (
                <p className="text-xs">
                  Retry uses the same operation ID and choices:{" "}
                  {previewIntent.operationId}
                </p>
              )}
            </div>
          )}
        </section>
      )}
      <section className="space-y-2">
        <h2 className="text-lg font-semibold">Recent operations</h2>
        {operations.isPending && <p role="status">Loading operations…</p>}
        {operations.isError && (
          <p role="alert">
            {message(operations.error)}{" "}
            <Button variant="outline" onClick={() => void operations.refetch()}>
              Retry operations
            </Button>
          </p>
        )}
        {operations.data?.items.length === 0 && (
          <p className="text-sm">No operations yet.</p>
        )}
        <ul className="space-y-2">
          {operations.data?.items.map((entry) => (
            <li key={entry.operationId}>
              <button
                className="w-full rounded-md border p-3 text-left hover:bg-muted"
                onClick={() => chooseOperation(entry.operationId)}
              >
                <span className="font-medium">{entry.state}</span>
                <span className="ml-2 text-sm">
                  {entry.progress.applied}/{entry.progress.total} applied ·{" "}
                  {entry.progress.pending} pending
                </span>
                <span className="block break-all text-xs text-muted-foreground">
                  {entry.operationId}
                </span>
              </button>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
