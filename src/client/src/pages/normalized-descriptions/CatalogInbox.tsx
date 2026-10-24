import { useState } from "react";
import { extractErrorMessage } from "@/lib/problem-details";
import { Link } from "react-router";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { RequestFailure } from "@/components/RequestFailure";
import { Pagination } from "@/components/Pagination";
import { useServerPagination } from "@/hooks/useServerPagination";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { useAiConnection } from "@/hooks/useNormalizationAi";
import {
  useNormalizationInbox,
  useNormalizationInboxActions,
  useNormalizationInboxPreferences,
  type InboxPreferences,
} from "@/hooks/useNormalizationInbox";
import type { CatalogSource } from "@/hooks/useNormalizationCatalog";

export function CatalogInbox({
  onReview,
  onRun,
}: {
  onReview: (source: CatalogSource) => void;
  onRun: (id: string) => void;
}) {
  const [query, setQuery] = useState("");
  const [settingsSession, setSettingsSession] = useState(0);
  const pagination = useServerPagination({ tableId: "normalization-inbox" });
  const search = useDebouncedValue(query, 250);
  const inbox = useNormalizationInbox(
    search,
    pagination.offset,
    pagination.limit,
  );
  const preferences = useNormalizationInboxPreferences();
  const actions = useNormalizationInboxActions();
  return (
    <section aria-label="Saved purchases needing review" className="space-y-3">
      <h2 className="card-title">Saved purchases needing review</h2>
      <p>
        Receipts save immediately. Review these store descriptions to keep
        future search and reports consistent. Suggestions never approve a
        product or change a purchase on their own.
      </p>
      {preferences.data?.isOwner && (
        <AssistanceSettings
          key={`${preferences.data.revision}:${settingsSession}`}
          preferences={preferences.data}
          onReload={() =>
            void preferences.refetch().then((result) => {
              if (result.isSuccess) setSettingsSession((value) => value + 1);
            })
          }
        />
      )}
      {preferences.isError && (
        <RequestFailure
          message="Assistance settings could not be loaded. Manual review is still available."
          retry={() => void preferences.refetch()}
          isRetrying={preferences.isFetching}
        />
      )}
      <Input
        aria-label="Search pending descriptions"
        value={query}
        onChange={(event) => {
          setQuery(event.target.value);
          pagination.resetPage();
        }}
      />
      {inbox.isPending ? (
        <p role="status">Loading pending purchases...</p>
      ) : inbox.isError ? (
        <RequestFailure
          message="Pending purchases could not be loaded."
          retry={() => void inbox.refetch()}
          isRetrying={inbox.isFetching}
        />
      ) : (
        <>
          <p>{inbox.data.total} source groups need review</p>
          {inbox.data.items.map((source) => (
            <article
              key={source.sourceHash}
              className="rounded border p-3 space-y-2"
            >
              <h3 className="font-medium">{source.source.description}</h3>
              <p>
                {source.source.location}
                {source.source.itemCode
                  ? ` · ${source.source.itemCode}`
                  : ""} · {source.purchaseCount} purchases across{" "}
                {source.receiptCount} receipts
              </p>
              <p>
                Waiting since {new Date(source.firstSeen).toLocaleDateString()}{" "}
                · Assistance: {source.assistanceState.replaceAll("_", " ")}
              </p>
              {source.errorDetail && <p>{source.errorDetail}</p>}
              <div className="flex flex-wrap gap-2">
                <Button
                  variant="outline"
                  onClick={() => onReview(source.source)}
                >
                  Review source
                </Button>
                {source.runId && preferences.data?.isOwner && (
                  <Button
                    variant="outline"
                    onClick={() => onRun(source.runId!)}
                  >
                    View suggestions or retry
                  </Button>
                )}
                {!source.runId &&
                  source.attemptRevision &&
                  preferences.data?.enabled &&
                  preferences.data.isOwner &&
                  ["paused", "auth_required"].includes(
                    source.assistanceState,
                  ) && (
                    <Button
                      variant="outline"
                      disabled={actions.retryAdmission.isPending}
                      onClick={() =>
                        actions.retryAdmission.mutate({
                          sourceHash: source.sourceHash,
                          expectedRevision: source.attemptRevision!,
                        })
                      }
                    >
                      Retry assistance
                    </Button>
                  )}
              </div>
              <ul className="flex flex-wrap gap-3">
                {source.receipts.map((receipt) => (
                  <li key={receipt.itemId}>
                    <Link
                      className="underline"
                      to={`/receipts/${receipt.receiptId}`}
                    >
                      Receipt {receipt.date}
                    </Link>
                  </li>
                ))}
              </ul>
            </article>
          ))}
          {!inbox.data.items.length && (
            <p>No pending purchases in this view.</p>
          )}
          <Pagination
            currentPage={pagination.currentPage}
            totalItems={inbox.data.total}
            pageSize={pagination.pageSize}
            totalPages={pagination.totalPages(inbox.data.total)}
            onPageChange={(page) => pagination.setPage(page, inbox.data.total)}
            onPageSizeChange={pagination.setPageSize}
            disabled={inbox.isFetching}
          />
        </>
      )}
      {actions.retryAdmission.error && (
        <p role="alert">
          {extractErrorMessage(actions.retryAdmission.error) ??
            "Assistance could not be retried."}
        </p>
      )}
    </section>
  );
}

function AssistanceSettings({
  preferences,
  onReload,
}: {
  preferences: InboxPreferences;
  onReload: () => void;
}) {
  const connection = useAiConnection();
  const actions = useNormalizationInboxActions();
  const [enabled, setEnabled] = useState(preferences.enabled);
  const [model, setModel] = useState(preferences.model ?? "");
  const [limit, setLimit] = useState(preferences.dailyLimit);
  return (
    <details className="rounded border p-3">
      <summary>
        Post-save ChatGPT suggestions: {preferences.enabled ? "enabled" : "off"}
      </summary>
      {actions.savePreferences.isError && (
        <Button type="button" variant="outline" onClick={onReload}>
          Reload saved settings and discard these edits
        </Button>
      )}
      <form
        className="space-y-2 pt-3"
        onSubmit={(event) => {
          event.preventDefault();
          actions.savePreferences.mutate({
            enabled,
            model: model || null,
            dailyLimit: limit,
            expectedRevision: preferences.revision,
          });
        }}
      >
        <p>
          Opt in to send pending store descriptions and catalog candidates to
          your ChatGPT connection after saving. At most one automatic request
          per exact source; uncertain requests require an explicit retry. All
          suggestions need your review. The daily limit bounds new source
          requests, not token usage.
        </p>
        <label className="flex gap-2">
          <input
            type="checkbox"
            checked={enabled}
            onChange={(event) => setEnabled(event.target.checked)}
          />
          Enable post-save suggestions
        </label>
        <label className="block">
          Model
          <select
            aria-label="Post-save model"
            value={model}
            onChange={(event) => setModel(event.target.value)}
          >
            <option value="">Choose model</option>
            {[
              ...new Set([
                ...(connection.data?.models ?? []),
                ...(model ? [model] : []),
              ]),
            ].map((value) => (
              <option key={value} value={value}>
                {value}
              </option>
            ))}
          </select>
        </label>
        <label className="block" htmlFor="normalization-assistance-daily-limit">
          New source requests per UTC day
          <Input
            id="normalization-assistance-daily-limit"
            type="number"
            min={1}
            max={100}
            value={limit}
            onChange={(event) => setLimit(Number(event.target.value))}
          />
        </label>
        <Button
          type="submit"
          disabled={actions.savePreferences.isPending || (enabled && !model)}
        >
          Save assistance settings
        </Button>
        {actions.savePreferences.error && (
          <p role="alert">
            {extractErrorMessage(actions.savePreferences.error) ??
              "Assistance settings could not be saved."}
          </p>
        )}
        {actions.savePreferences.isSuccess && (
          <p role="status">Assistance settings saved.</p>
        )}
      </form>
    </details>
  );
}
