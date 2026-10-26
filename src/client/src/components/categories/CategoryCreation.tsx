import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { extractErrorMessage } from "@/lib/problem-details";
import {
  categoryPaths,
  type CategoryChoice,
  type CategoryRevision,
} from "@/lib/category-paths";
import type { components } from "@/generated/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

type Request = components["schemas"]["TaxonomyChangeRequest"];

/** Kept inside the picker so the receipt form and its unsaved values stay mounted. */
export function CategoryCreation({
  revision,
  suggestedParentId,
  onCreated,
  onCancel,
}: {
  revision: CategoryRevision;
  suggestedParentId: string | null;
  onCreated: (choice: CategoryChoice) => void;
  onCancel: () => void;
}) {
  const cache = useQueryClient();
  const nameRef = useRef<HTMLInputElement>(null);
  useEffect(() => {
    nameRef.current?.focus();
  }, []);
  const [latest, setLatest] = useState(revision);
  const [parentId, setParentId] = useState(suggestedParentId ?? "");
  const [name, setName] = useState("");
  const [request, setRequest] = useState<Request>();
  const [review, setReview] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [refreshError, setRefreshError] = useState(false);
  const rows = useMemo(() => categoryPaths(latest.nodes), [latest.nodes]);
  const parentAvailable =
    !parentId || rows.some((row) => row.node.id === parentId);
  const mutation = useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async (body: Request) => {
      const { data, error, response } = await client.POST(
        "/api/taxonomy/changes",
        {
          ...localErrorPolicy.request,
          body,
        },
      );
      if (error || !data || !response.ok)
        throw {
          status: response.status,
          detail:
            extractErrorMessage(error) ??
            "Category could not be created. Try again.",
        };
      return data;
    },
    onSuccess: async (saved, body) => {
      await cache.cancelQueries({
        queryKey: ["taxonomy", "current"],
        exact: true,
      });
      cache.setQueryData(["taxonomy", "current"], saved);
      const row = categoryPaths(saved.nodes).find(
        (entry) => entry.node.id === body.changes[0].categoryId,
      );
      if (row)
        onCreated({
          categoryId: row.node.id,
          taxonomyRevisionId: saved.id,
          path: row.path,
        });
    },
  });
  const conflict =
    (mutation.error as { status?: number } | null)?.status === 409;

  function updateDraft(update: () => void) {
    update();
    setRequest(undefined);
    setReview(false);
    mutation.reset();
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    event.stopPropagation();
    if (
      mutation.isPending ||
      conflict ||
      !parentAvailable ||
      !name.trim() ||
      name !== name.trim()
    )
      return;
    const body: Request = request ?? {
      expectedRevisionId: latest.id,
      operationId: crypto.randomUUID(),
      reason: "Created during receipt item entry",
      changes: [
        {
          kind: "create",
          categoryId: crypto.randomUUID(),
          parentId: parentId || null,
          name,
        },
      ],
    };
    setRequest(body);
    mutation.mutate(body);
  }

  async function refresh() {
    setRefreshing(true);
    setRefreshError(false);
    try {
      const { data, error } = await client.GET(
        "/api/taxonomy",
        localErrorPolicy.request,
      );
      if (error || !data) throw error ?? new Error("No taxonomy returned");
      await cache.cancelQueries({
        queryKey: ["taxonomy", "current"],
        exact: true,
      });
      cache.setQueryData(["taxonomy", "current"], data);
      setLatest(data);
      setRequest(undefined);
      setReview(true);
      mutation.reset();
    } catch {
      setRefreshError(true);
    } finally {
      setRefreshing(false);
    }
  }

  return (
    <form
      onSubmit={submit}
      className="space-y-3 border-t p-3"
      aria-label="Create category"
    >
      <div className="space-y-1">
        <Label htmlFor="receipt-category-name">New category</Label>
        <Input
          id="receipt-category-name"
          ref={nameRef}
          required
          maxLength={200}
          value={name}
          onChange={(event) => updateDraft(() => setName(event.target.value))}
        />
      </div>
      <div className="space-y-1">
        <Label htmlFor="receipt-category-parent">Parent</Label>
        <select
          id="receipt-category-parent"
          className="h-9 w-full rounded-md border bg-background px-2 text-sm"
          value={parentId}
          onChange={(event) =>
            updateDraft(() => setParentId(event.target.value))
          }
        >
          <option value="">Root</option>
          {parentId && !parentAvailable && (
            <option value={parentId}>Previous parent unavailable</option>
          )}
          {rows.map((row) => (
            <option key={row.node.id} value={row.node.id}>
              {row.label}
            </option>
          ))}
        </select>
      </div>
      {review && (
        <p role="status" className="text-sm">
          Tree refreshed. Review the parent, then save.
        </p>
      )}
      {!parentAvailable && (
        <p role="alert" className="text-sm">
          Choose an active parent.
        </p>
      )}
      {mutation.error && (
        <div role="alert" className="space-y-2 text-sm">
          <p>
            {extractErrorMessage(mutation.error) ??
              "Category could not be created. Try again."}
          </p>
          {conflict && (
            <Button
              type="button"
              variant="outline"
              disabled={refreshing}
              onClick={() => void refresh()}
            >
              {refreshing ? "Refreshing…" : "Refresh tree"}
            </Button>
          )}
        </div>
      )}
      {refreshError && (
        <p role="alert" className="text-sm">
          Refresh failed. Try again.
        </p>
      )}
      <div className="flex justify-end gap-2">
        <Button
          type="button"
          variant="ghost"
          disabled={mutation.isPending}
          onClick={onCancel}
        >
          Cancel
        </Button>
        <Button
          type="submit"
          disabled={
            mutation.isPending ||
            conflict ||
            !parentAvailable ||
            !name.trim() ||
            name !== name.trim()
          }
        >
          {mutation.isPending
            ? "Creating…"
            : request
              ? "Retry creation"
              : "Create and select"}
        </Button>
      </div>
    </form>
  );
}
