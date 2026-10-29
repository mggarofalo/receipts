import { useState } from "react";
import { Link, useSearchParams } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import type { components } from "@/generated/api";
import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import { usePageTitle } from "@/hooks/usePageTitle";
import { usePermission } from "@/hooks/usePermission";
import { useAuth } from "@/hooks/useAuth";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { extractErrorMessage } from "@/lib/problem-details";
import { categoryPaths, type CategoryChoice } from "@/lib/category-paths";
import { CategoryPathPicker } from "@/components/categories/CategoryPathPicker";
import { RememberCategoryControl } from "@/components/categories/RememberCategory";
import { AutomaticClassificationActivity } from "@/components/categories/AutomaticClassificationActivity";
import { PageHead } from "@/components/primitives";
import { Button } from "@/components/ui/button";

type Suggestion = components["schemas"]["CategoryAssistanceSuggestionRead"];
type Decision = components["schemas"]["CategoryAssistanceDecisionRequest"];
type ReviewPreference =
  components["schemas"]["CategoryAssistanceReviewPreference"];
type ReviewRequest =
  components["schemas"]["CategoryAssistanceReviewPreferenceRequest"];
type ReviewState = ReviewPreference["state"];
type ReviewFilter = ReviewState | "all";
type PendingRemember = {
  attemptId: string;
  source: Suggestion["source"];
  categoryId: string;
  affectedItemCount: number;
  savedAt: string;
};

function storageKey(userId: string) {
  return `receipts:classification:pending-remember:${userId}`;
}

function readPending(userId?: string): PendingRemember[] {
  if (!userId) return [];
  try {
    const parsed: unknown = JSON.parse(
      window.sessionStorage.getItem(storageKey(userId)) ?? "[]",
    );
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(
      (entry): entry is PendingRemember =>
        !!entry &&
        typeof entry === "object" &&
        typeof entry.attemptId === "string" &&
        typeof entry.categoryId === "string" &&
        typeof entry.affectedItemCount === "number" &&
        typeof entry.savedAt === "string" &&
        !!entry.source &&
        typeof entry.source.location === "string" &&
        typeof entry.source.description === "string",
    );
  } catch {
    return [];
  }
}

export default function ClassificationWorkspace() {
  usePageTitle("Classification");
  const { isAdmin } = usePermission();
  const { user } = useAuth();
  const userId = user?.userId;
  const [pendingRemember, setPendingRemember] = useState<{
    userId?: string;
    entries: PendingRemember[];
  }>(() => ({ userId, entries: readPending(userId) }));
  const visiblePending = userId
    ? pendingRemember.userId === userId
      ? pendingRemember.entries
      : readPending(userId)
    : [];
  // A provider can be unavailable while a saved classification still needs a
  // separate, manual mapping decision.
  function addPending(entry: PendingRemember) {
    if (!userId) return;
    const next = [
      ...readPending(userId).filter((row) => row.attemptId !== entry.attemptId),
      entry,
    ];
    try {
      window.sessionStorage.setItem(storageKey(userId), JSON.stringify(next));
    } catch {
      /* Keep the current tab's completion reachable. */
    }
    setPendingRemember({ userId, entries: next });
  }
  function removePending(attempt: string) {
    if (!userId) return;
    const next = readPending(userId).filter((row) => row.attemptId !== attempt);
    try {
      window.sessionStorage.setItem(storageKey(userId), JSON.stringify(next));
    } catch {
      /* Keep UI usable when storage is unavailable. */
    }
    setPendingRemember({ userId, entries: next });
  }
  const [params, setParams] = useSearchParams();
  const offset = Math.max(0, Number(params.get("offset")) || 0);
  const attemptId = params.get("attempt");
  const reviewParam = params.get("review");
  const review: ReviewFilter =
    reviewParam === "later" ||
    reviewParam === "left_as_is" ||
    reviewParam === "all"
      ? reviewParam
      : "active";
  function navigateReview(next: ReviewFilter, nextOffset = 0) {
    setParams(
      next === "active"
        ? { offset: String(nextOffset) }
        : { review: next, offset: String(nextOffset) },
    );
  }
  const status = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["category-assistance", "status"],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET(
        "/api/category-assistance/status",
        {
          ...localErrorPolicy.request,
          signal,
        },
      );
      if (error || !data)
        throw error ?? new Error("Assistance status unavailable");
      return data;
    },
  });
  const available =
    status.data?.capability === "ready" &&
    status.data.suggestionsAvailable === true &&
    status.data.consentEnabled === true;
  const suggestions = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["category-assistance", "suggestions", userId, review, offset],
    enabled: available && !attemptId,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET(
        "/api/category-assistance/suggestions",
        {
          ...localErrorPolicy.request,
          params: { query: { offset, limit: 25, review } },
          signal,
        },
      );
      if (error || !data) throw error ?? new Error("Suggestions unavailable");
      return data;
    },
  });
  const directSuggestion = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["category-assistance", "suggestion", attemptId, userId],
    enabled: available && !!attemptId,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET(
        "/api/category-assistance/suggestions/{attemptId}",
        {
          ...localErrorPolicy.request,
          params: { path: { attemptId: attemptId! } },
          signal,
        },
      );
      if (error || !data) throw error ?? new Error("Suggestion unavailable");
      return data;
    },
  });
  const taxonomy = useCategoryTaxonomy(available);

  return (
    <div className="space-y-6">
      <PageHead title="Classification" />
      <p>
        {available
          ? "Review suggestions or browse categories."
          : "Browse categories and classify receipts."}
      </p>
      <nav
        aria-label="Classification workspace"
        className="flex flex-wrap items-center gap-3 text-sm"
      >
        {available && (
          <span aria-current="page" className="font-semibold">
            Suggestions
          </span>
        )}
        <Link className="underline" to="/categories">
          Browse categories
        </Link>
        <Link className="underline" to="/categories/cleanup">
          Clean up history
        </Link>
        {isAdmin() && (
          <Link className="underline" to="/admin/category-assistance">
            Assistance settings
          </Link>
        )}
      </nav>
      <AutomaticClassificationActivity />
      {visiblePending.length > 0 && (
        <section
          aria-label="Completed classifications awaiting future mapping"
          className="space-y-3"
        >
          <h2 className="font-semibold">Completed classifications</h2>
          {visiblePending.map((entry) => (
            <article
              key={entry.attemptId}
              className="rounded-lg border p-4 space-y-2"
            >
              <p>
                {entry.affectedItemCount} item(s) classified on{" "}
                {new Date(entry.savedAt).toLocaleString()}.
              </p>
              <p>
                Saved category: {entry.categoryId}. This records the completed
                decision; receipt categories may have changed since then.
              </p>
              <p>
                Exact source: {entry.source.description} at{" "}
                {entry.source.location}
                {entry.source.itemCode ? ` · ${entry.source.itemCode}` : ""}.
              </p>
              <p>
                Optionally remember this category for future exact matches. This
                is a separate manual mapping action.
              </p>
              <RememberCategoryControl
                source={entry.source}
                preferredCategoryId={entry.categoryId}
                confirmDifferentMapping
                onSaved={() => removePending(entry.attemptId)}
              />
              <Button
                variant="outline"
                onClick={() => removePending(entry.attemptId)}
              >
                Skip remembering
              </Button>
            </article>
          ))}
        </section>
      )}
      {status.isPending && <p role="status">Checking category assistance…</p>}
      {status.isError && (
        <div role="alert" className="space-y-2">
          <p>
            Category assistance status could not be loaded. Manual categories
            remain available.
          </p>
          <Button variant="outline" onClick={() => void status.refetch()}>
            Retry
          </Button>
        </div>
      )}
      {status.data &&
        status.data.capability !== "unavailable" &&
        !available && (
          <p role="status">
            Category suggestions are unavailable. You can still browse
            categories and classify receipts manually.
          </p>
        )}
      {available && (
        <>
          {!attemptId && (
            <nav aria-label="Review state" className="flex flex-wrap gap-2">
              {(
                [
                  ["active", "Active"],
                  ["later", "Later"],
                  ["left_as_is", "Left as is"],
                  ["all", "All"],
                ] as const
              ).map(([value, label]) => (
                <Button
                  key={value}
                  variant={review === value ? "default" : "outline"}
                  aria-current={review === value ? "page" : undefined}
                  onClick={() => navigateReview(value)}
                >
                  {label}
                </Button>
              ))}
            </nav>
          )}
          {attemptId && (
            <Link className="underline" to="/classification">
              All suggestions
            </Link>
          )}
          {(attemptId ? directSuggestion : suggestions).isPending && (
            <p role="status">Loading suggestions…</p>
          )}
          {(attemptId ? directSuggestion : suggestions).isError && (
            <div role="alert" className="space-y-2">
              <p>
                {extractErrorMessage(
                  attemptId ? directSuggestion.error : suggestions.error,
                ) ?? "Could not load suggestions."}
              </p>
              <Button
                variant="outline"
                onClick={() =>
                  void (attemptId ? directSuggestion : suggestions).refetch()
                }
              >
                Retry
              </Button>
            </div>
          )}
          {!attemptId && suggestions.data?.items.length === 0 && (
            <p>
              No suggestions in{" "}
              {review === "left_as_is" ? "Left as is" : review}.
            </p>
          )}
          <div className="space-y-3">
            {(attemptId
              ? directSuggestion.data
                ? [directSuggestion.data]
                : []
              : (suggestions.data?.items ?? [])
            ).map((item) => (
              <SuggestionCard
                key={`${userId}:${item.attemptId}`}
                item={item}
                userId={userId}
                taxonomy={taxonomy}
                initiallyOpen={!!attemptId}
                direct={!!attemptId}
                onAccepted={addPending}
              />
            ))}
          </div>
          {!attemptId && suggestions.data && suggestions.data.total > 25 && (
            <div className="flex gap-2">
              <Button
                variant="outline"
                disabled={offset === 0}
                onClick={() => navigateReview(review, Math.max(0, offset - 25))}
              >
                Previous
              </Button>
              <Button
                variant="outline"
                disabled={offset + 25 >= suggestions.data.total}
                onClick={() => navigateReview(review, offset + 25)}
              >
                Next
              </Button>
            </div>
          )}
        </>
      )}
    </div>
  );
}

function SuggestionCard({
  item,
  userId,
  taxonomy,
  initiallyOpen,
  direct,
  onAccepted,
}: {
  item: Suggestion;
  userId?: string;
  taxonomy: ReturnType<typeof useCategoryTaxonomy>;
  initiallyOpen: boolean;
  direct: boolean;
  onAccepted: (entry: PendingRemember) => void;
}) {
  const cache = useQueryClient();
  const [open, setOpen] = useState(initiallyOpen);
  const [offset, setOffset] = useState(0);
  const [selected, setSelected] = useState<string[]>([]);
  const [excludedSelected, setExcludedSelected] = useState<string[]>([]);
  const [choice, setChoice] = useState<CategoryChoice>();
  const [pending, setPending] = useState<{
    kind: "accept" | "dismiss";
    body: Decision | { operationId: string };
  }>();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string>();
  const [stale, setStale] = useState(false);
  const [appliedCategoryId, setAppliedCategoryId] = useState<string>();
  const [reviewPending, setReviewPending] = useState<ReviewRequest>();
  const [reviewBusy, setReviewBusy] = useState(false);
  const [reviewStale, setReviewStale] = useState(false);
  const [reviewMessage, setReviewMessage] = useState<string>();
  const preference = useQuery({
    ...localErrorPolicy.query,
    queryKey: [
      "category-assistance",
      "review-preference",
      item.attemptId,
      userId,
    ],
    enabled: open,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET(
        "/api/category-assistance/suggestions/{attemptId}/review-preference",
        {
          ...localErrorPolicy.request,
          params: { path: { attemptId: item.attemptId } },
          signal,
        },
      );
      if (error || !data)
        throw error ?? new Error("Review preference unavailable");
      return data;
    },
  });
  const eligible = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["category-assistance", "items", item.attemptId, userId, offset],
    enabled: open && item.isCurrent,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET(
        "/api/category-assistance/suggestions/{attemptId}/items",
        {
          ...localErrorPolicy.request,
          params: {
            path: { attemptId: item.attemptId },
            query: { offset, limit: 100 },
          },
          signal,
        },
      );
      if (error || !data)
        throw error ?? new Error("Eligible items unavailable");
      return data;
    },
  });
  const proposed =
    taxonomy.data && item.proposedPath.length > 0
      ? categoryPaths(taxonomy.data.nodes).find(
          (row) =>
            row.node.id === item.proposedPath.at(-1) &&
            row.path.map((node) => node.id).join("/") ===
              item.proposedPath.join("/"),
        )
      : undefined;
  const selectedChoice =
    choice ??
    (proposed && taxonomy.data
      ? {
          categoryId: proposed.node.id,
          taxonomyRevisionId: taxonomy.data.id,
          path: proposed.path,
        }
      : undefined);
  const canAccept =
    item.isCurrent &&
    !stale &&
    !reviewStale &&
    !reviewPending &&
    !reviewBusy &&
    preference.isSuccess &&
    selected.length > 0 &&
    selected.length <= 100 &&
    (pending?.kind === "accept" ||
      selected.every((id) =>
        eligible.data?.items.some((receiptItem) => receiptItem.id === id),
      )) &&
    !!selectedChoice?.categoryId &&
    selectedChoice.taxonomyRevisionId === taxonomy.data?.id;

  async function refreshReview() {
    setBusy(true);
    setMessage(undefined);
    try {
      await cache.refetchQueries(
        {
          queryKey: [
            "category-assistance",
            direct ? "suggestion" : "suggestions",
          ],
        },
        { throwOnError: true },
      );
      await eligible.refetch({ throwOnError: true });
      await preference.refetch({ throwOnError: true });
      await taxonomy.refetch({ throwOnError: true });
      setPending(undefined);
      setSelected([]);
      setExcludedSelected([]);
      setChoice(undefined);
      setStale(false);
      setReviewStale(false);
      setReviewPending(undefined);
    } catch (error) {
      setMessage(extractErrorMessage(error) ?? "Could not refresh. Try again.");
    } finally {
      setBusy(false);
    }
  }

  async function saveReview(next?: {
    state: ReviewState;
    excludedItemIds: string[];
  }) {
    if (
      reviewBusy ||
      !preference.data ||
      reviewStale ||
      (!next && !reviewPending)
    )
      return;
    const request = reviewPending ?? {
      operationId: crypto.randomUUID(),
      expectedRevision: preference.data.revision,
      state: next!.state,
      excludedItemIds: next!.excludedItemIds,
    };
    setReviewPending(request);
    setReviewBusy(true);
    setReviewMessage(undefined);
    try {
      const result = await client.PUT(
        "/api/category-assistance/suggestions/{attemptId}/review-preference",
        {
          ...localErrorPolicy.request,
          params: { path: { attemptId: item.attemptId } },
          body: request,
        },
      );
      if (result.error || !result.data) {
        if (result.response.status === 409) setReviewStale(true);
        throw result.error ?? new Error("Review preference was not saved");
      }
      // A replay may return an older response. Read the actual current state.
      await preference.refetch({ throwOnError: true });
      await cache.invalidateQueries({
        queryKey: ["category-assistance", "suggestions"],
      });
      await cache.invalidateQueries({
        queryKey: ["category-assistance", "suggestion", item.attemptId],
      });
      await cache.invalidateQueries({
        queryKey: ["category-assistance", "items", item.attemptId],
      });
      setSelected([]);
      setExcludedSelected([]);
      setReviewPending(undefined);
      setReviewMessage("Review preference saved.");
    } catch (error) {
      setReviewMessage(
        extractErrorMessage(error) ??
          "Could not confirm review preference. Retry uses the same operation.",
      );
    } finally {
      setReviewBusy(false);
    }
  }

  async function decide(kind: "accept" | "dismiss") {
    if (busy || (kind === "accept" && !canAccept)) return;
    const request =
      pending?.kind === kind
        ? pending
        : {
            kind,
            body:
              kind === "dismiss"
                ? { operationId: crypto.randomUUID() }
                : {
                    operationId: crypto.randomUUID(),
                    expectedTaxonomyRevision: taxonomy.data!.id,
                    itemIds: selected,
                    categoryId: selectedChoice!.categoryId,
                  },
          };
    setPending(request);
    setBusy(true);
    setMessage(undefined);
    try {
      const result =
        kind === "accept"
          ? await client.POST(
              "/api/category-assistance/suggestions/{attemptId}/accept",
              {
                ...localErrorPolicy.request,
                params: { path: { attemptId: item.attemptId } },
                body: request.body as Decision,
              },
            )
          : await client.POST(
              "/api/category-assistance/suggestions/{attemptId}/dismiss",
              {
                ...localErrorPolicy.request,
                params: { path: { attemptId: item.attemptId } },
                body: request.body as { operationId: string },
              },
            );
      if (result.error || !result.data) {
        if (result.response.status === 409) setStale(true);
        throw result.error ?? new Error("Decision was not saved");
      }
      setPending(undefined);
      if (kind === "accept" && result.data.categoryId) {
        setAppliedCategoryId(result.data.categoryId);
        onAccepted({
          attemptId: item.attemptId,
          source: item.source,
          categoryId: result.data.categoryId,
          affectedItemCount: result.data.affectedItemIds.length,
          savedAt: new Date().toISOString(),
        });
      }
      setMessage(
        kind === "accept"
          ? `${result.data.affectedItemIds.length} item(s) classified.`
          : "Suggestion dismissed.",
      );
      if (kind === "dismiss") {
        await cache.invalidateQueries({
          queryKey: ["category-assistance", "suggestions"],
        });
      }
      if (kind === "accept") {
        await cache.invalidateQueries({
          queryKey: ["category-assistance", "suggestions"],
        });
        await cache.invalidateQueries({
          queryKey: ["category-assistance", "suggestion", item.attemptId],
        });
      }
      await cache.invalidateQueries({
        queryKey: ["category-assistance", "items", item.attemptId],
      });
    } catch (error) {
      setMessage(
        extractErrorMessage(error) ??
          "Decision could not be saved. Retry uses the same operation.",
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <article className="rounded-lg border p-4 space-y-3 break-words">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="font-semibold">
            {item.originalDescription ?? item.source.description}
          </h2>
          <p className="text-sm text-muted-foreground">
            {item.source.location}
            {item.source.itemCode ? ` · ${item.source.itemCode}` : ""} ·{" "}
            {item.affectedCount} affected
          </p>
          <Link
            className="text-sm underline"
            to={`/classification?attempt=${encodeURIComponent(item.attemptId)}`}
          >
            Link to this suggestion
          </Link>
        </div>
        <span className="text-sm">{item.isCurrent ? item.state : "Stale"}</span>
      </div>
      <p className="text-sm">
        Review:{" "}
        {item.reviewState === "left_as_is"
          ? "Left as is"
          : (item.reviewState ?? "Active")}
        {item.excludedItemCount ? ` · ${item.excludedItemCount} excluded` : ""}
      </p>
      <p>
        {proposed?.label ??
          (item.abstentionReason
            ? `No proposed category: ${item.abstentionReason}`
            : "Proposed category unavailable in current tree")}
      </p>
      <Button
        variant="outline"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
      >
        {open ? "Hide details" : "Review details"}
      </Button>
      {open && (
        <div className="space-y-3 border-t pt-3">
          <section aria-label="Review preference" className="space-y-2">
            <h3 className="font-medium">Review preference</h3>
            {preference.isPending && (
              <p role="status">Loading review preference…</p>
            )}
            {preference.isError && (
              <p role="alert">
                {extractErrorMessage(preference.error) ??
                  "Could not load review preference."}{" "}
                <Button
                  variant="outline"
                  onClick={() => void preference.refetch()}
                >
                  Retry preference
                </Button>
              </p>
            )}
            {preference.data && (
              <>
                <p>
                  Current choice:{" "}
                  {preference.data.state === "left_as_is"
                    ? "Left as is"
                    : preference.data.state}
                  . {preference.data.excludedItemIds.length} excluded item(s).
                </p>
                {reviewStale && (
                  <p role="alert">
                    Review preference changed. Refresh before saving again.{" "}
                    <Button
                      variant="outline"
                      onClick={() => void refreshReview()}
                    >
                      Refresh suggestions
                    </Button>
                  </p>
                )}
                {reviewPending && !reviewStale && (
                  <Button
                    variant="outline"
                    disabled={reviewBusy}
                    onClick={() => void saveReview()}
                  >
                    Retry preference save
                  </Button>
                )}
                <div className="flex flex-wrap gap-2">
                  <Button
                    variant="outline"
                    disabled={
                      reviewBusy ||
                      !!reviewPending ||
                      reviewStale ||
                      preference.data.state === "later"
                    }
                    onClick={() =>
                      void saveReview({
                        state: "later",
                        excludedItemIds: preference.data.excludedItemIds,
                      })
                    }
                  >
                    Later
                  </Button>
                  <Button
                    variant="outline"
                    disabled={
                      reviewBusy ||
                      !!reviewPending ||
                      reviewStale ||
                      preference.data.state === "left_as_is"
                    }
                    onClick={() =>
                      void saveReview({
                        state: "left_as_is",
                        excludedItemIds: preference.data.excludedItemIds,
                      })
                    }
                  >
                    Leave as is
                  </Button>
                  <Button
                    variant="outline"
                    disabled={
                      reviewBusy ||
                      !!reviewPending ||
                      reviewStale ||
                      preference.data.state === "active"
                    }
                    onClick={() =>
                      void saveReview({
                        state: "active",
                        excludedItemIds: preference.data.excludedItemIds,
                      })
                    }
                  >
                    Reopen
                  </Button>
                </div>
                {selected.length > 0 && (
                  <Button
                    variant="outline"
                    disabled={
                      reviewBusy ||
                      !!reviewPending ||
                      reviewStale ||
                      preference.data.excludedItemIds.length +
                        selected.filter(
                          (id) => !preference.data.excludedItemIds.includes(id),
                        ).length >
                        1000
                    }
                    onClick={() =>
                      void saveReview({
                        state: preference.data.state,
                        excludedItemIds: [
                          ...new Set([
                            ...preference.data.excludedItemIds,
                            ...selected,
                          ]),
                        ],
                      })
                    }
                  >
                    Exclude selected from assistance
                  </Button>
                )}
                {preference.data.excludedItemIds.length > 0 && (
                  <Button
                    variant="outline"
                    disabled={
                      reviewBusy ||
                      !!reviewPending ||
                      reviewStale ||
                      excludedSelected.length === 0
                    }
                    onClick={() =>
                      void saveReview({
                        state: preference.data.state,
                        excludedItemIds: preference.data.excludedItemIds.filter(
                          (id) => !excludedSelected.includes(id),
                        ),
                      })
                    }
                  >
                    Include selected excluded items
                  </Button>
                )}
                {preference.data.excludedItemIds.length > 0 && (
                  <ul className="text-sm">
                    {preference.data.excludedItemIds.map((id) => (
                      <li key={id}>
                        <label>
                          <input
                            type="checkbox"
                            checked={excludedSelected.includes(id)}
                            disabled={reviewBusy || !!reviewPending}
                            onChange={(event) =>
                              setExcludedSelected(
                                event.target.checked
                                  ? [...excludedSelected, id]
                                  : excludedSelected.filter(
                                      (value) => value !== id,
                                    ),
                              )
                            }
                          />{" "}
                          Excluded item {id}
                        </label>
                      </li>
                    ))}
                  </ul>
                )}
                {preference.data.excludedItemIds.length >= 1000 && (
                  <p role="status">
                    The 1000 item exclusion limit has been reached.
                  </p>
                )}
              </>
            )}
            {reviewMessage && (
              <p role={reviewPending ? "alert" : "status"}>{reviewMessage}</p>
            )}
          </section>
          <p className="text-sm">
            Source: {item.source.description} at {item.source.location}.{" "}
            {item.probabilityScope === "local_choice"
              ? "Final choice confidence"
              : "Confidence"}
            :{" "}
            {item.confidence == null
              ? "not provided"
              : `${Math.round(item.confidence * 100)}%`}
            . Suggested {new Date(item.createdAt).toLocaleString()}.
          </p>
          {!item.isCurrent && (
            <p role="alert">
              This suggestion is stale. Refresh the list for current
              suggestions.
            </p>
          )}
          {stale && (
            <p role="alert">
              The suggestion or selected items changed. Refresh before reviewing
              again.{" "}
              <Button
                variant="outline"
                disabled={busy}
                onClick={() => void refreshReview()}
              >
                Refresh suggestions
              </Button>
            </p>
          )}
          {item.isCurrent && (
            <>
              <div>
                <p className="text-sm font-medium">Eligible receipt items</p>
                {eligible.isPending && (
                  <p role="status">Loading eligible items…</p>
                )}
                {eligible.isError && (
                  <div role="alert">
                    Could not load eligible items.{" "}
                    <Button
                      variant="outline"
                      onClick={() => void eligible.refetch()}
                    >
                      Retry
                    </Button>
                  </div>
                )}
                {eligible.data?.items.map((receiptItem) => (
                  <div
                    key={receiptItem.id}
                    className="flex items-start gap-2 py-2 text-sm border-b"
                  >
                    <input
                      type="checkbox"
                      id={`classification-item-${receiptItem.id}`}
                      aria-label={`Select ${receiptItem.description}`}
                      checked={selected.includes(receiptItem.id)}
                      disabled={
                        busy ||
                        !!pending ||
                        !!appliedCategoryId ||
                        (!selected.includes(receiptItem.id) &&
                          selected.length >= 100)
                      }
                      onChange={(event) => {
                        setPending(undefined);
                        setSelected(
                          event.target.checked
                            ? [...selected, receiptItem.id]
                            : selected.filter(
                                (value) => value !== receiptItem.id,
                              ),
                        );
                      }}
                    />
                    <span>
                      <span className="font-medium">
                        {receiptItem.description}
                      </span>
                      <span className="block text-muted-foreground">
                        {receiptItem.date} · {receiptItem.store} ·{" "}
                        {new Intl.NumberFormat(undefined, {
                          style: "currency",
                          currency: receiptItem.currency,
                        }).format(receiptItem.totalAmount)}
                      </span>
                      <Link
                        className="underline"
                        to={`/receipts/${receiptItem.receiptId}`}
                      >
                        View receipt
                      </Link>
                    </span>
                  </div>
                ))}
                {eligible.data && (
                  <p className="text-xs text-muted-foreground">
                    {selected.length} selected on this page. Changing pages
                    clears the selection.
                  </p>
                )}
                {eligible.data?.total === 0 && <p>No eligible items remain.</p>}
                {eligible.data && eligible.data.total > 100 && (
                  <div className="flex gap-2">
                    <Button
                      variant="outline"
                      disabled={offset === 0 || busy || !!pending}
                      onClick={() => {
                        setSelected([]);
                        setOffset(Math.max(0, offset - 100));
                      }}
                    >
                      Previous items
                    </Button>
                    <Button
                      variant="outline"
                      disabled={
                        offset + 100 >= eligible.data.total || busy || !!pending
                      }
                      onClick={() => {
                        setSelected([]);
                        setOffset(offset + 100);
                      }}
                    >
                      Next items
                    </Button>
                  </div>
                )}
              </div>
              <div className="max-w-md space-y-1">
                <p className="text-sm font-medium">
                  Apply category (choose a different path if needed)
                </p>
                <CategoryPathPicker
                  revision={taxonomy.data}
                  value={selectedChoice}
                  onValueChange={(next) => {
                    setChoice(next);
                    setPending(undefined);
                  }}
                  loading={taxonomy.isPending}
                  error={taxonomy.isError}
                  disabled={busy || !!pending}
                />
              </div>
              {taxonomy.isError && (
                <p role="alert">
                  Categories unavailable.{" "}
                  <Button
                    variant="outline"
                    onClick={() => void taxonomy.refetch()}
                  >
                    Retry
                  </Button>
                </p>
              )}
              <p className="text-xs text-muted-foreground">
                Only checked eligible items change. This does not verify product
                identity.
              </p>
              <div className="flex flex-wrap gap-2">
                <Button
                  disabled={!canAccept || busy || !!appliedCategoryId}
                  onClick={() => void decide("accept")}
                >
                  Apply to selected
                </Button>
                <Button
                  variant="outline"
                  disabled={busy || stale || !!appliedCategoryId}
                  onClick={() => void decide("dismiss")}
                >
                  Dismiss suggestion
                </Button>
                <Button variant="ghost" onClick={() => setOpen(false)}>
                  Close
                </Button>
              </div>
            </>
          )}
          {message && (
            <p role={stale || pending ? "alert" : "status"}>{message}</p>
          )}
        </div>
      )}
    </article>
  );
}
