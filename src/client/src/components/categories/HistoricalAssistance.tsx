import { useRef, useState } from "react";
import { Link } from "react-router";
import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";
import type { components } from "@/generated/api";
import { localErrorPolicy } from "@/lib/request-error-policy";
import {
  extractErrorMessage,
  parseProblemDetails,
} from "@/lib/problem-details";
import { Button } from "@/components/ui/button";
import { assistanceFailureMessage } from "@/lib/assistance-failure";
import { useAuth } from "@/hooks/useAuth";

type Group = components["schemas"]["HistoricalGroupSummary"];
type Key = components["schemas"]["HistoricalGroupKey"];
type Progress = components["schemas"]["CategoryAssistanceHistoricalProgress"];

const requestState = {
  pending: "Processing",
  ready: "Ready to review",
  partial: "Finished with some results unavailable",
  failed: "Unable to complete",
};

function summarizeStates(states: string[]) {
  const counts = new Map<string, number>();
  for (const state of states) {
    const label =
      state === "suggested"
        ? "ready to review"
        : state === "abstained"
          ? "without a suggestion"
          : state === "accepted"
            ? "accepted"
            : state === "applied"
              ? "applied automatically"
              : state === "dismissed"
                ? "dismissed"
                : state === "stale"
                  ? "out of date"
                  : state === "failed"
                    ? "failed"
                    : state === "indeterminate"
                      ? "with an unknown outcome"
                      : ["waiting", "queued", "dispatching"].includes(state)
                        ? "in progress"
                        : "with status unavailable";
    counts.set(label, (counts.get(label) ?? 0) + 1);
  }
  return Array.from(counts, ([label, count]) => `${count} ${label}`).join(
    " · ",
  );
}

function failureSummary(progress: Progress) {
  return [
    ...new Set(
      progress.groups.flatMap((group) =>
        group.failureReasons
          .filter((reason) => reason != null)
          .map(assistanceFailureMessage),
      ),
    ),
  ];
}
type Intent = {
  operationId: string;
  taxonomyRevisionId: string;
  groups: Key[];
};
const intentKey = (actor: string) =>
  `receipts:historical-assistance-intent:${actor}`;
type StoredIntent = { intent?: Intent; error?: string };
type RecoveredIntent = { progress: Progress; storageError?: string };
function readIntent(actor: string): StoredIntent {
  try {
    const value = sessionStorage.getItem(intentKey(actor));
    if (value === null) return {};
    const parsed: unknown = JSON.parse(value);
    if (typeof parsed !== "object" || parsed === null)
      throw new Error("Invalid saved request");
    const candidate = parsed as Partial<Intent>;
    if (
      typeof candidate.operationId !== "string" ||
      typeof candidate.taxonomyRevisionId !== "string" ||
      !Array.isArray(candidate.groups) ||
      candidate.groups.length < 1 ||
      candidate.groups.length > 25 ||
      candidate.groups.some(
        (group) =>
          typeof group?.store !== "string" ||
          typeof group?.rawDescription !== "string" ||
          !(group.itemCode === null || typeof group.itemCode === "string"),
      )
    )
      throw new Error("Invalid saved request");
    return { intent: candidate as Intent };
  } catch {
    return {
      error:
        "Saved request recovery could not be read. New requests are paused to avoid duplicating earlier work. Check browser storage access or restore the saved request data, then retry reading it.",
    };
  }
}

function clearIntent(actor: string, operationId: string) {
  const stored = readIntent(actor);
  if (stored.error) throw new Error(stored.error);
  // A response from an older mount must not delete a newer saved request.
  if (stored.intent?.operationId === operationId)
    sessionStorage.removeItem(intentKey(actor));
}

const errorMessage = (error: unknown) =>
  extractErrorMessage(error) ?? "Request failed. Try again.";

export function HistoricalAssistance({
  selected,
  revisionId,
}: {
  selected: Group[];
  revisionId?: string;
}) {
  const { user } = useAuth();
  const actor = user?.userId;
  if (!actor) return null;
  return (
    <HistoricalAssistanceForActor
      key={actor}
      actor={actor}
      selected={selected}
      revisionId={revisionId}
    />
  );
}

function HistoricalAssistanceForActor({
  actor,
  selected,
  revisionId,
}: {
  actor: string;
  selected: Group[];
  revisionId?: string;
}) {
  const [storedIntent, setStoredIntent] = useState<StoredIntent>(() =>
    readIntent(actor),
  );
  const savedIntent = storedIntent.intent;
  function setIntent(intent: Intent | undefined) {
    setStoredIntent({ intent });
  }
  const [activeId, setActiveId] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [errorOperationId, setErrorOperationId] = useState<string>();
  const submitting = useRef(false);
  const status = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["category-assistance", "status", actor],
    refetchInterval: 15_000,
    queryFn: async ({ signal }) => {
      const result = await client.GET("/api/category-assistance/status", {
        ...localErrorPolicy.request,
        signal,
      });
      if (result.error || !result.data)
        throw result.error ?? new Error("Response unavailable");
      return result.data;
    },
  });
  // The passive capability source reads Jev's enabled flag and API key locally:
  // "unavailable" means no configured provider, not a failed network check.
  // Wait for that read before restoring Jev-only request controls.
  const canReviewRequests =
    !status.isPending && status.data?.capability !== "unavailable";
  const available =
    !status.isError &&
    status.data?.capability === "ready" &&
    status.data.consentEnabled === true &&
    status.data.suggestionsAvailable === true;
  const hasLocalOperation = !!(
    savedIntent ||
    storedIntent.error ||
    activeId ||
    error ||
    busy
  );
  const recent = useQuery<
    components["schemas"]["CategoryAssistanceHistoricalPage"]
  >({
    ...localErrorPolicy.query,
    queryKey: ["category-assistance", "historical-requests", actor],
    enabled: (query) =>
      canReviewRequests &&
      (available || hasLocalOperation || !!query.state.data?.items.length),
    refetchInterval: 15_000,
    queryFn: async ({ signal }) => {
      const result = await client.GET(
        "/api/category-assistance/historical-requests",
        {
          ...localErrorPolicy.request,
          signal,
          params: { query: { offset: 0, limit: 25 } },
        },
      );
      if (result.error || !result.data)
        throw result.error ?? new Error("Response unavailable");
      return result.data;
    },
  });
  // Reconcile the saved operation directly: it may have fallen outside the
  // recent page, or the browser may have lost the successful POST response.
  const recovery = useQuery<RecoveredIntent>({
    ...localErrorPolicy.query,
    queryKey: [
      "category-assistance",
      "historical-recovery",
      actor,
      savedIntent?.operationId,
    ],
    enabled: canReviewRequests && !!savedIntent && !busy,
    refetchInterval: (query) => (query.state.data ? false : 5_000),
    queryFn: async ({ signal }) => {
      const result = await client.GET(
        "/api/category-assistance/historical-requests/{operationId}",
        {
          ...localErrorPolicy.request,
          signal,
          params: { path: { operationId: savedIntent!.operationId } },
        },
      );
      if (result.error || !result.data)
        throw result.error ?? new Error("Response unavailable");
      let storageError: string | undefined;
      if (result.data.operationId === savedIntent!.operationId) {
        try {
          clearIntent(actor, result.data.operationId);
        } catch {
          storageError =
            "The earlier request was received, but browser storage could not clear its saved retry. Check browser storage access; returning will check that request again safely.";
        }
      }
      return { progress: result.data, storageError };
    },
  });
  const recovered = recovery.data?.progress;
  const intent =
    recovered?.operationId === savedIntent?.operationId
      ? undefined
      : savedIntent;
  const displayedId = activeId ?? recovered?.operationId;
  const active = useQuery<Progress>({
    ...localErrorPolicy.query,
    queryKey: ["category-assistance", "historical-request", actor, displayedId],
    enabled: canReviewRequests && !!displayedId,
    refetchInterval: (query) =>
      query.state.data?.state === "pending" ? 5_000 : 15_000,
    queryFn: async ({ signal }) => {
      const result = await client.GET(
        "/api/category-assistance/historical-requests/{operationId}",
        {
          ...localErrorPolicy.request,
          signal,
          params: { path: { operationId: displayedId! } },
        },
      );
      if (result.error || !result.data)
        throw result.error ?? new Error("Response unavailable");
      return result.data;
    },
  });
  if (
    !canReviewRequests ||
    (!available &&
      !hasLocalOperation &&
      !recent.data?.items.length &&
      !recovered)
  )
    return null;

  const tooLarge = selected.some((row) => Number(row.purchaseCount) > 100);
  const selectedPurchases = selected.reduce(
    (total, row) => total + Number(row.purchaseCount),
    0,
  );
  const canRequest =
    available &&
    !!revisionId &&
    selected.length > 0 &&
    selected.length <= 25 &&
    !tooLarge &&
    Number.isInteger(status.data?.dailyLimit) &&
    Number.isInteger(status.data?.pendingLimit) &&
    !storedIntent.error &&
    !busy;

  async function request() {
    if (submitting.current || storedIntent.error || (!intent && !canRequest))
      return;
    const body = intent ?? {
      operationId: crypto.randomUUID(),
      taxonomyRevisionId: revisionId!,
      groups: selected.map((row) => row.group),
    };
    if (!intent) {
      try {
        sessionStorage.setItem(intentKey(actor), JSON.stringify(body));
      } catch {
        setErrorOperationId(undefined);
        setError(
          "Request could not be saved for a safe retry. Check browser storage and try again.",
        );
        return;
      }
    }
    submitting.current = true;
    setIntent(body);
    setBusy(true);
    setError("");
    setErrorOperationId(body.operationId);
    try {
      const result = await client.POST(
        "/api/category-assistance/historical-requests",
        {
          ...localErrorPolicy.request,
          body,
        },
      );
      if (result.error || !result.data) {
        const statusCode =
          result.response?.status ?? parseProblemDetails(result.error)?.status;
        // This endpoint's validation/conflict responses reject enrollment.
        // After an unknown outcome, an authorization or proxy rejection of a
        // replay cannot prove that the original submission was not received.
        const rejected =
          statusCode === 400 ||
          statusCode === 409 ||
          (!intent &&
            statusCode &&
            statusCode >= 400 &&
            statusCode < 500 &&
            statusCode !== 408 &&
            statusCode !== 499);
        if (rejected) {
          const message = errorMessage(result.error);
          // This API has no machine-readable capacity code. Recognize only
          // its capacity wording; other conflicts keep their specific advice.
          const rejectionMessage =
            statusCode === 409 &&
            /^Historical request exceeds available capacity(?: of \d+ purchases)?\.?$/i.test(
              message,
            )
              ? `${message} Select fewer groups or review existing suggestions, then request again.`
              : message;
          setIntent(undefined);
          setError(rejectionMessage);
          try {
            clearIntent(actor, body.operationId);
          } catch {
            setError(
              `${rejectionMessage} Browser storage could not clear the rejected request; retry information may reappear when you return.`,
            );
          }
          return;
        }
        throw result.error ?? new Error("Request acknowledgement unavailable");
      }
      const progress = result.data;
      setActiveId(progress.operationId);
      setIntent(undefined);
      try {
        clearIntent(actor, body.operationId);
      } catch {
        setError(
          "The request was received, but browser storage could not be cleared. Its saved retry will be checked again when you return.",
        );
      }
      await recent.refetch();
    } catch (cause) {
      setError(
        `${errorMessage(cause)} Receipt of this request is not confirmed. We are checking automatically; retry safely to resend the original selection.`,
      );
    } finally {
      submitting.current = false;
      setBusy(false);
    }
  }

  return (
    <section
      aria-label="Historical suggestions"
      className="space-y-3 rounded-lg border p-4"
    >
      <h2 className="text-lg font-semibold">Optional historical suggestions</h2>
      <p className="text-sm">
        Suggestions require separate review. Requesting them does not change
        saved classifications.
      </p>
      {status.isError && (
        <div className="space-y-2">
          <p role="alert">
            Could not check whether new suggestions are available.{" "}
            {errorMessage(status.error)} You can still check or safely retry an
            earlier request.
          </p>
          <Button variant="outline" onClick={() => void status.refetch()}>
            Retry checking availability
          </Button>
        </div>
      )}
      {!available && !status.isError && (
        <div className="space-y-2">
          <p role="status">
            {status.isPending
              ? "Checking whether new suggestions are available…"
              : "New suggestions are unavailable. You can still check or safely retry an earlier request."}
          </p>
          {!status.isPending && (
            <Button variant="outline" onClick={() => void status.refetch()}>
              Check availability again
            </Button>
          )}
        </div>
      )}
      {available && (
        <>
          {Number.isInteger(status.data?.dailyLimit) &&
            Number.isInteger(status.data?.pendingLimit) && (
              <p className="text-sm">
                Limits: {status.data?.dailyLimit} suggestions per day and{" "}
                {status.data?.pendingLimit} awaiting review or processing. A
                group may need more than one suggestion.
              </p>
            )}
          {(!Number.isInteger(status.data?.dailyLimit) ||
            !Number.isInteger(status.data?.pendingLimit)) && (
            <p className="text-sm">
              Configured limits unavailable.{" "}
              <Button variant="outline" onClick={() => void status.refetch()}>
                Refresh capability status
              </Button>
            </p>
          )}
          <p className="text-sm">
            This request includes {selected.length} selected visible groups and{" "}
            {selectedPurchases} purchases. Maximum: 25 groups, 100 purchases per
            group, 2,500 purchases total.
          </p>
          {tooLarge && (
            <p role="alert">
              A selected group has more than 100 purchases. Remove it to request
              suggestions.
            </p>
          )}
        </>
      )}
      {storedIntent.error && (
        <div className="space-y-2">
          <p role="alert">{storedIntent.error}</p>
          <Button
            variant="outline"
            onClick={() => setStoredIntent(readIntent(actor))}
          >
            Retry reading saved request
          </Button>
        </div>
      )}
      {recovery.data?.storageError && (
        <p role="alert">{recovery.data.storageError}</p>
      )}
      {intent && (
        <p className="text-sm">
          Checking an earlier request for {intent.groups.length} groups. Retry
          safely resends that original selection, even if your current selection
          has changed. Once receipt is confirmed and assistance is available,
          you can request suggestions for another selection.
        </p>
      )}
      {intent && recovery.isError && (
        <p role="alert">
          {parseProblemDetails(recovery.error)?.status === 404
            ? "The earlier request has not appeared yet. Retry safely to confirm it; a new request could duplicate work."
            : `Could not check the earlier request. ${errorMessage(recovery.error)}`}
        </p>
      )}
      {(available || intent || busy) && (
        <Button
          disabled={busy || (!intent && !canRequest)}
          onClick={() => void request()}
        >
          {busy
            ? "Sending request…"
            : intent
              ? "Retry safely"
              : "Request suggestions"}
        </Button>
      )}
      {error && (!recovered || recovered.operationId !== errorOperationId) && (
        <p role="alert">{error}</p>
      )}
      {displayedId && (
        <div
          className="space-y-2 rounded-md border p-3"
          aria-label="Suggestion request progress"
        >
          <h3 className="font-medium">Request progress</h3>
          {active.isPending && <p role="status">Loading request progress…</p>}
          {active.isError && <p role="alert">{errorMessage(active.error)}</p>}
          {active.data && (
            <>
              <p>
                {requestState[active.data.state]} · {active.data.groupCount}{" "}
                groups · {active.data.itemCount} purchases
              </p>
              <p className="text-sm" role="status">
                {summarizeStates(
                  active.data.groups.flatMap((entry) => entry.states),
                )}
              </p>
              {failureSummary(active.data).map((message) => (
                <p key={message} role="alert">
                  {message}
                </p>
              ))}
              <details>
                <summary className="cursor-pointer text-sm underline">
                  View groups and review suggestions
                </summary>
                <ul className="mt-2 space-y-2">
                  {active.data.groups.map((entry) => (
                    <li
                      key={JSON.stringify(entry.group)}
                      className="break-words rounded border p-2 text-sm"
                    >
                      {entry.group.store} · {entry.group.rawDescription} ·{" "}
                      {entry.group.itemCode ?? "No code"}: {entry.itemCount}{" "}
                      purchases ·{" "}
                      {summarizeStates(entry.states) || "Awaiting progress"}
                      {entry.failureReasons.some(Boolean) && (
                        <ul className="mt-1 list-disc pl-5">
                          {[
                            ...new Set(entry.failureReasons.filter(Boolean)),
                          ].map((reason) => (
                            <li key={reason}>
                              {assistanceFailureMessage(reason)}
                            </li>
                          ))}
                        </ul>
                      )}
                      {[...new Set(entry.attemptIds)].map(
                        (attemptId, index, ids) => (
                          <Link
                            key={attemptId}
                            className="ml-2 break-all underline"
                            to={`/classification?attempt=${encodeURIComponent(attemptId)}`}
                          >
                            Review suggestion
                            {ids.length > 1 ? ` ${index + 1}` : ""}
                          </Link>
                        ),
                      )}
                    </li>
                  ))}
                </ul>
              </details>
            </>
          )}
          <p className="text-xs text-muted-foreground">
            Progress updates automatically.
          </p>
          {active.isError && (
            <Button variant="outline" onClick={() => void active.refetch()}>
              Retry loading progress
            </Button>
          )}
        </div>
      )}
      <div className="space-y-2">
        <h3 className="font-medium">Recent suggestion requests</h3>
        {recent.isPending && <p role="status">Loading suggestion requests…</p>}
        {recent.isError && <p role="alert">{errorMessage(recent.error)}</p>}
        {recent.isError && (
          <Button variant="outline" onClick={() => void recent.refetch()}>
            Retry loading requests
          </Button>
        )}
        <ul className="space-y-1">
          {recent.data?.items.map((entry) => (
            <li key={entry.operationId}>
              <Button
                variant="ghost"
                className="h-auto max-w-full break-all text-left whitespace-normal"
                onClick={() => setActiveId(entry.operationId)}
              >
                {requestState[entry.state]} · {entry.groupCount} groups ·{" "}
                {entry.itemCount} purchases ·{" "}
                {new Date(entry.createdAt).toLocaleString()}
              </Button>
            </li>
          ))}
        </ul>
      </div>
    </section>
  );
}
