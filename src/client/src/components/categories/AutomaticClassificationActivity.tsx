import { useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import { useAuth } from "@/hooks/useAuth";
import { categoryPaths } from "@/lib/category-paths";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { extractErrorMessage } from "@/lib/problem-details";
import { Button } from "@/components/ui/button";

export function AutomaticClassificationActivity() {
  const { user } = useAuth();
  const [open, setOpen] = useState(false);
  const [offset, setOffset] = useState(0);
  const [busy, setBusy] = useState<string>();
  const [error, setError] = useState<string>();
  const intents = useRef<Record<string, string>>({});
  const queryClient = useQueryClient();
  const taxonomy = useCategoryTaxonomy(open);
  const activity = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["historical-automatic-activity", user?.userId, offset],
    enabled: open,
    refetchInterval: open ? 10000 : false,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET(
        "/api/historical-reclassifications/activity",
        {
          ...localErrorPolicy.request,
          params: { query: { offset, limit: 10 } },
          signal,
        },
      );
      if (error || !data) throw error ?? new Error("Activity unavailable");
      return data;
    },
  });
  const paths = categoryPaths(taxonomy.data?.nodes ?? []);
  async function undo(id: string, savedRevision?: string | null) {
    const revision = savedRevision ?? intents.current[id] ?? taxonomy.data?.id;
    if (!revision) return;
    intents.current[id] = revision;
    setBusy(id);
    setError(undefined);
    try {
      // A click completes ordinary batches. A larger batch remains resumable.
      for (let chunk = 0; chunk < 100; chunk++) {
        const { data, error } = await client.POST(
          "/api/historical-reclassifications/{id}/undo",
          {
            ...localErrorPolicy.request,
            params: { path: { id } },
            body: {
              taxonomyRevisionId: revision,
              reason: "User undid automatic classification",
              limit: 100,
            },
          },
        );
        if (error || !data) throw error ?? new Error("Undo unavailable");
        if (data.state === "undone") break;
      }
      await queryClient.invalidateQueries({
        queryKey: ["historical-automatic-activity"],
      });
      await queryClient.invalidateQueries({ queryKey: ["historical-groups"] });
    } catch (cause) {
      setError(extractErrorMessage(cause) || "Could not undo. Try again.");
      void activity.refetch();
    } finally {
      setBusy(undefined);
    }
  }
  return (
    <details
      open={open}
      onToggle={(event) => setOpen(event.currentTarget.open)}
      className="rounded-lg border p-4"
    >
      <summary className="cursor-pointer font-medium">
        Automatic classification activity
      </summary>
      {open && (
        <div className="mt-3 space-y-3">
          <p className="text-sm text-muted-foreground">
            Remembered choices classify matching past purchases automatically.
            Undo restores earlier choices where purchases have not changed.
          </p>
          {activity.isPending && <p role="status">Loading activity…</p>}
          {activity.isError && (
            <p role="alert">
              Could not load activity.{" "}
              <Button variant="ghost" onClick={() => void activity.refetch()}>
                Retry activity
              </Button>
            </p>
          )}
          {error && <p role="alert">{error}</p>}
          {activity.data?.items.length === 0 && (
            <p className="text-sm">No automatic classifications yet.</p>
          )}
          <ul className="space-y-3">
            {activity.data?.items.map((entry) => {
              const progress = {
                applied: Number(entry.progress.applied),
                pending: Number(entry.progress.pending),
                undone: Number(entry.progress.undone),
                undoConflicted: Number(entry.progress.undoConflicted),
                undoSkipped: Number(entry.progress.undoSkipped),
              };
              const remaining =
                progress.applied -
                progress.undone -
                progress.undoConflicted -
                progress.undoSkipped;
              return (
                <li
                  key={entry.operationId}
                  className="flex flex-wrap items-center justify-between gap-2 border-t pt-3"
                >
                  <div>
                    {entry.groups.map((choice, index) => (
                      <p key={index} className="text-sm break-words">
                        {choice.group.rawDescription} at {choice.group.store} ·{" "}
                        {paths.find(
                          (path) => path.node.id === choice.targetCategoryId,
                        )?.label ?? "Remembered category"}
                      </p>
                    ))}
                    <p className="text-xs text-muted-foreground">
                      {new Date(entry.capturedAt).toLocaleDateString()} ·{" "}
                      {progress.applied} classified
                      {progress.pending > 0 ? " · In progress" : ""}
                      {progress.undone > 0
                        ? ` · ${progress.undone} undone`
                        : ""}
                      {progress.undoConflicted + progress.undoSkipped > 0
                        ? ` · ${progress.undoConflicted + progress.undoSkipped} kept because they changed`
                        : ""}
                    </p>
                  </div>
                  {remaining > 0 && (
                    <Button
                      size="sm"
                      variant="outline"
                      disabled={
                        !!busy || !taxonomy.data || progress.pending > 0
                      }
                      onClick={() =>
                        void undo(
                          entry.operationId,
                          entry.undoTaxonomyRevisionId,
                        )
                      }
                    >
                      {busy === entry.operationId
                        ? "Undoing…"
                        : entry.state === "undoing"
                          ? "Continue undo"
                          : "Undo"}
                    </Button>
                  )}
                </li>
              );
            })}
          </ul>
          {activity.data &&
            (offset > 0 || activity.data.nextOffset !== null) && (
              <div className="flex gap-2">
                <Button
                  variant="ghost"
                  disabled={offset === 0 || !!busy}
                  onClick={() => setOffset(Math.max(0, offset - 10))}
                >
                  Newer
                </Button>
                <Button
                  variant="ghost"
                  disabled={activity.data.nextOffset === null || !!busy}
                  onClick={() => setOffset(activity.data!.nextOffset!)}
                >
                  Older
                </Button>
              </div>
            )}
        </div>
      )}
    </details>
  );
}
