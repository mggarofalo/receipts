import { useRef, useState } from "react";
import { useSearchParams } from "react-router";
import {
  useCatalogCoverage,
  useCatalogOperation,
  useCatalogOperations,
  type CatalogSource,
} from "@/hooks/useNormalizationCatalog";
import {
  useAiConnection,
  useAiRun,
  useAiRuns,
} from "@/hooks/useNormalizationAi";
import { Button } from "@/components/ui/button";
import { RequestFailure } from "@/components/RequestFailure";
import { CatalogSourceList } from "./CatalogSourceList";
import {
  CatalogSourceReview,
  type CatalogSuggestion,
} from "./CatalogSourceReview";
import { CatalogOperationReview } from "./CatalogOperationReview";
import { CatalogAiReview } from "./CatalogAiReview";
import { CatalogInbox } from "./CatalogInbox";
import { useNormalizationInboxPreferences } from "@/hooks/useNormalizationInbox";

export function CatalogTab() {
  const [params, setParams] = useSearchParams();
  const operationId = params.get("catalogOperation");
  const runId = params.get("aiRun");
  const [review, setReview] = useState<{
    source: CatalogSource;
    suggestion?: CatalogSuggestion;
    key: string;
  } | null>(null);
  const activeReview = useRef<string | null>(null);
  const coverage = useCatalogCoverage();
  const operations = useCatalogOperations();
  const operation = useCatalogOperation(operationId);
  const connection = useAiConnection();
  const assistance = useNormalizationInboxPreferences();
  const owner =
    assistance.data?.isOwner === true ||
    (connection.data?.configured === true && connection.data.isOwner);
  const runs = useAiRuns(owner);
  const run = useAiRun(runId, owner);
  const select = (field: "catalogOperation" | "aiRun", id: string | null) => {
    setParams((previous) => {
      const next = new URLSearchParams(previous);
      next.set("tab", "catalog");
      if (id) next.set(field, id);
      else next.delete(field);
      return next;
    });
  };
  const openReview = (
    source: CatalogSource,
    suggestion?: CatalogSuggestion,
  ) => {
    const key = crypto.randomUUID();
    activeReview.current = key;
    setReview({ source, suggestion, key });
  };
  return (
    <div className="space-y-6">
      <CatalogInbox onReview={openReview} onRun={(id) => select("aiRun", id)} />
      <section className="space-y-3" aria-label="Catalog coverage">
        <h2 className="card-title">
          A maintained catalog for search and reports
        </h2>
        <p>
          Bring historical store descriptions under consistent product names,
          then reuse reviewed mappings for new purchases. Recommendations need
          your review; a linked item alone is not proof that its name is
          correct.
        </p>
        {coverage.isPending ? (
          <p role="status">Loading coverage...</p>
        ) : coverage.isError ? (
          <RequestFailure
            message="Catalog coverage could not be loaded."
            retry={() => void coverage.refetch()}
            isRetrying={coverage.isFetching}
          />
        ) : (
          coverage.data && (
            <dl className="grid grid-cols-2 gap-3 md:grid-cols-4">
              {[
                ["All purchases", coverage.data.totalItems],
                ["Unassigned", coverage.data.unassigned],
                ["Unknown legacy", coverage.data.legacyUnknown],
                ["Automatic", coverage.data.automatic],
                ["Reviewed", coverage.data.reviewed],
                ["Approved mapping", coverage.data.mapped],
                ["Protected", coverage.data.protected],
                ["Excluded", coverage.data.excluded],
                ["Revoked mapping needs review", coverage.data.revisable ?? 0],
              ].map(([label, value]) => (
                <div className="rounded border p-3" key={label}>
                  <dt>{label}</dt>
                  <dd className="text-2xl">{value}</dd>
                </div>
              ))}
            </dl>
          )
        )}
      </section>
      {operationId && (
        <section className="space-y-2">
          <div className="flex gap-2">
            <Button
              variant="ghost"
              onClick={() => select("catalogOperation", null)}
            >
              Close operation
            </Button>
            <Button
              variant="outline"
              onClick={() => void operation.refetch()}
              disabled={operation.isFetching}
            >
              Refresh operation
            </Button>
          </div>
          {operation.isPending ? (
            <p role="status">Loading operation...</p>
          ) : operation.isError ? (
            <RequestFailure
              message="Operation could not be loaded."
              retry={() => void operation.refetch()}
              isRetrying={operation.isFetching}
            />
          ) : (
            operation.data && (
              <CatalogOperationReview
                key={operation.data.id}
                operation={operation.data}
              />
            )
          )}
        </section>
      )}
      {review && (
        <section className="space-y-2">
          <Button
            variant="ghost"
            onClick={() => {
              activeReview.current = null;
              setReview(null);
            }}
          >
            Close source review
          </Button>
          <CatalogSourceReview
            key={review.key}
            source={review.source}
            suggestion={review.suggestion}
            onPreview={(created) => {
              if (activeReview.current !== review.key) return;
              activeReview.current = null;
              setReview(null);
              select("catalogOperation", created.id);
            }}
          />
        </section>
      )}
      {runId && owner && (
        <section className="space-y-2">
          <Button variant="ghost" onClick={() => select("aiRun", null)}>
            Close suggestions
          </Button>
          {run.isPending ? (
            <p role="status">Loading suggestions...</p>
          ) : run.isError ? (
            <RequestFailure
              message="Suggestions could not be loaded."
              retry={() => void run.refetch()}
              isRetrying={run.isFetching}
            />
          ) : (
            run.data &&
            (run.data.proposals.length > 0 &&
            run.data.proposals.every((proposal) => !proposal.source) ? (
              <Button
                onClick={() =>
                  setParams((previous) => {
                    const next = new URLSearchParams(previous);
                    next.set("tab", "chatgpt");
                    return next;
                  })
                }
              >
                Open original purchase review
              </Button>
            ) : (
              <CatalogAiReview
                key={run.data.id}
                run={run.data}
                onReview={openReview}
                onRetried={(id) => select("aiRun", id)}
              />
            ))
          )}
        </section>
      )}
      <CatalogSourceList
        key={connection.data?.generation ?? "unconnected"}
        onReview={openReview}
        onStarted={(id) => select("aiRun", id)}
      />
      <section className="space-y-2" aria-label="Historical operation history">
        <h2 className="card-title">Historical operations</h2>
        {operations.isPending ? (
          <p>Loading operations...</p>
        ) : operations.isError ? (
          <RequestFailure
            message="Operation history could not be loaded."
            retry={() => void operations.refetch()}
            isRetrying={operations.isFetching}
          />
        ) : !operations.data?.length ? (
          <p>No historical operations yet.</p>
        ) : (
          <ul className="space-y-2">
            {operations.data.map((item) => (
              <li key={item.id}>
                <Button
                  variant="outline"
                  className="h-auto whitespace-normal text-left"
                  onClick={() => select("catalogOperation", item.id)}
                >
                  {item.source.description} at {item.source.location} ·{" "}
                  {item.state} · {item.applied} applied
                </Button>
              </li>
            ))}
          </ul>
        )}
      </section>
      {owner && (
        <section className="space-y-2" aria-label="Catalog request history">
          <h2 className="card-title">ChatGPT requests</h2>
          {runs.isPending ? (
            <p>Loading requests...</p>
          ) : runs.isError ? (
            <RequestFailure
              message="Request history could not be loaded."
              retry={() => void runs.refetch()}
              isRetrying={runs.isFetching}
            />
          ) : (
            <ul className="space-y-2">
              {runs.data?.map((item) => (
                <li key={item.id}>
                  <Button
                    variant="outline"
                    onClick={() => select("aiRun", item.id)}
                  >
                    {new Date(item.createdAt).toLocaleString()} · {item.model} ·{" "}
                    {item.state}
                  </Button>
                </li>
              ))}
            </ul>
          )}
        </section>
      )}
    </div>
  );
}
