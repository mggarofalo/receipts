import { useId, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router";
import {
  pendingReviewHref,
  readCatalogSource,
} from "./catalog-review-navigation";
import { PendingIdentityRecovery } from "./PendingIdentityRecovery";
import {
  useCatalogSources,
  useCatalogSuggestions,
  type CatalogSource,
} from "@/hooks/useNormalizationCatalog";
import { useAiConnection } from "@/hooks/useNormalizationAi";
import { useServerPagination } from "@/hooks/useServerPagination";
import { Pagination } from "@/components/Pagination";
import { RequestFailure } from "@/components/RequestFailure";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Checkbox } from "@/components/ui/checkbox";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { extractErrorMessage } from "@/lib/problem-details";

const sourceId = (source: CatalogSource) => JSON.stringify(source);

export function CatalogSourceList({
  onReview,
  onStarted,
}: {
  onReview: (source: CatalogSource) => void;
  onStarted: (id: string) => void;
}) {
  const [params] = useSearchParams();
  const returnedSource = readCatalogSource(params.get("catalogSource"));
  const [query, setQuery] = useState(returnedSource?.description ?? "");
  const legacyControlId = useId();
  const labelControlId = useId();
  const [labelOnly, setLabelOnly] = useState(false);
  const [disposition, setDisposition] = useState(
    returnedSource ? "all" : "needs-review",
  );
  const [selection, setSelection] = useState<CatalogSource[]>([]);
  const [model, setModel] = useState("");
  const [includeLegacy, setIncludeLegacy] = useState(false);
  const intent = useRef<{ fingerprint: string; id: string } | null>(null);
  const pagination = useServerPagination({
    tableId: "normalization-catalog-sources",
  });
  const sources = useCatalogSources(
    query,
    disposition,
    pagination.offset,
    pagination.limit,
  );
  const connection = useAiConnection();
  const start = useCatalogSuggestions((run) => {
    setSelection([]);
    intent.current = null;
    onStarted(run.id);
  });
  const availableModels = connection.data?.models ?? [];
  const models =
    model && !availableModels.includes(model)
      ? [model, ...availableModels]
      : availableModels;
  const canRequest =
    connection.data?.configured &&
    connection.data.isOwner &&
    connection.data.credentialsPresent &&
    !connection.data.busy &&
    availableModels.includes(model);
  const selectionEligible =
    !labelOnly ||
    selection.every((source) => {
      const row = sources.data?.items.find(
        (group) => sourceId(group.source) === sourceId(source),
      );
      return (
        row?.linkedTargetCount === 1 &&
        row.linkedTargets?.[0]?.status === "active"
      );
    });
  const total = sources.data?.total ?? 0;
  return (
    <section className="space-y-4" aria-label="Catalog source descriptions">
      <h2 className="card-title">Source descriptions</h2>
      <p>
        Review distinct store descriptions once and reuse approved mappings on
        new purchases. Unknown legacy assignments need an explicit decision;
        they are not evidence of prior review.
      </p>
      {returnedSource && (
        <p role="status">
          Returned to {returnedSource.description} at {returnedSource.location}
          {returnedSource.itemCode ? ` (${returnedSource.itemCode})` : ""}.
          Linked identity status is refreshed after explicit review.
        </p>
      )}
      <form
        className="flex flex-wrap gap-2"
        onSubmit={(event) => {
          event.preventDefault();
          const form = new FormData(event.currentTarget);
          setQuery(String(form.get("q") ?? "").trim());
          setSelection([]);
          pagination.resetPage();
        }}
      >
        <Input
          name="q"
          defaultValue={query}
          maxLength={200}
          aria-label="Find source descriptions"
          placeholder="Search descriptions"
        />
        <Button type="submit" variant="outline">
          Search descriptions
        </Button>
        <label className="flex items-center gap-2">
          <span>Show</span>
          <select
            aria-label="Source disposition"
            value={disposition}
            onChange={(event) => {
              setDisposition(event.target.value);
              setSelection([]);
              pagination.resetPage();
            }}
          >
            <option value="needs-review">Needs review</option>
            <option value="all">All sources</option>
            <option value="mapped">Mapped</option>
            <option value="protected">Protected</option>
            <option value="excluded">Excluded</option>
          </select>
        </label>
      </form>
      {sources.isPending ? (
        <p role="status">Loading source descriptions...</p>
      ) : sources.isError ? (
        <RequestFailure
          message="Source descriptions could not be loaded."
          retry={() => void sources.refetch()}
          isRetrying={sources.isFetching}
        />
      ) : !sources.data?.items.length ? (
        <p>No descriptions match this view.</p>
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Select</TableHead>
              <TableHead>Description</TableHead>
              <TableHead>Store / code</TableHead>
              <TableHead>Current identities</TableHead>
              <TableHead>Purchases</TableHead>
              <TableHead>Needs review</TableHead>
              <TableHead>Review</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {sources.data.items.map((group) => {
              const id = sourceId(group.source);
              const selected = selection.some((item) => sourceId(item) === id);
              return (
                <TableRow key={id}>
                  <TableCell>
                    <Checkbox
                      aria-label={`Select ${group.source.description} at ${group.source.location}`}
                      checked={selected}
                      disabled={
                        start.isPending ||
                        sources.isFetching ||
                        (!selected && selection.length >= 50) ||
                        (labelOnly &&
                          (group.linkedTargetCount !== 1 ||
                            group.linkedTargets?.[0]?.status !== "active"))
                      }
                      onCheckedChange={(checked) =>
                        setSelection((current) =>
                          checked === true
                            ? [
                                ...current.filter(
                                  (item) => sourceId(item) !== id,
                                ),
                                group.source,
                              ]
                            : current.filter((item) => sourceId(item) !== id),
                        )
                      }
                    />
                  </TableCell>
                  <TableCell>{group.source.description}</TableCell>
                  <TableCell>
                    {group.source.location}
                    <span className="block text-sm">
                      {group.source.itemCode || "No item code"}
                    </span>
                  </TableCell>
                  <TableCell>
                    {(group.linkedTargets ?? []).map((target) => (
                      <div key={target.id}>
                        <span>
                          {target.name} —{" "}
                          {target.status === "pendingReview"
                            ? "Pending review"
                            : target.status === "active"
                              ? "Active"
                              : "Rejected"}
                        </span>
                        {target.status === "pendingReview" && (
                          <Link
                            className="block underline"
                            to={pendingReviewHref(target.id, group.source)}
                          >
                            Review pending identity
                          </Link>
                        )}
                      </div>
                    ))}
                    {!group.linkedTargetCount && (
                      <span>No linked identity</span>
                    )}
                    {(group.linkedTargetCount ?? 0) > 1 && (
                      <p className="text-sm">
                        Multiple identities: review ambiguity before requesting
                        a display name.
                      </p>
                    )}
                    {(group.linkedTargetCount ?? 0) >
                      (group.linkedTargets?.length ?? 0) && (
                      <p>
                        {group.linkedTargetCount! -
                          (group.linkedTargets?.length ?? 0)}{" "}
                        more linked identities
                      </p>
                    )}
                  </TableCell>
                  <TableCell>{group.itemCount}</TableCell>
                  <TableCell>
                    {group.unassignedCount} unassigned; {group.automaticCount}{" "}
                    automatic; {group.legacyUnknownCount} unknown legacy;{" "}
                    {group.protectedCount} protected;{" "}
                    {group.revisableCount ?? 0} revoked mapping
                  </TableCell>
                  <TableCell>
                    <Button
                      variant="outline"
                      onClick={() => onReview(group.source)}
                    >
                      Review
                    </Button>
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      )}
      <Pagination
        currentPage={pagination.currentPage}
        totalItems={total}
        pageSize={pagination.pageSize}
        totalPages={pagination.totalPages(total)}
        onPageChange={(page) => {
          setSelection([]);
          pagination.setPage(page, total);
        }}
        onPageSizeChange={(size) => {
          setSelection([]);
          pagination.setPageSize(size);
        }}
        disabled={sources.isFetching || start.isPending}
      />
      <fieldset
        className="space-y-3 rounded-lg border p-4"
        disabled={start.isPending}
      >
        <legend className="font-medium">Ask ChatGPT for suggestions</legend>
        <p>
          {selection.length} of up to 50 distinct descriptions selected. The
          model receives a bounded shortlist of existing names and
          human-approved examples; it cannot browse your entire catalog or write
          to it.
        </p>
        <p>
          Submitting sends the selected receipt descriptions, candidate names,
          and approved example descriptions to OpenAI using your connected
          ChatGPT plan. Examples indicate whether their store and code scope
          matches, without sending the store or item code. Prices, receipt
          totals, and payment details are not sent. Requests may consume your
          plan allowance, including requests later cancelled or retried.
        </p>
        {connection.isError && (
          <RequestFailure
            message="ChatGPT status could not be loaded."
            retry={() => void connection.refetch()}
            isRetrying={connection.isFetching}
          />
        )}
        {!connection.data?.credentialsPresent && (
          <p>
            Connect ChatGPT in the ChatGPT tab to request suggestions. Manual
            catalog review is available without it.
          </p>
        )}
        {connection.data?.busy && (
          <p role="status">ChatGPT is busy. Existing runs remain available.</p>
        )}
        <label className="flex items-center gap-2" htmlFor={labelControlId}>
          <Checkbox
            id={labelControlId}
            checked={labelOnly}
            onCheckedChange={(value) => {
              setLabelOnly(value === true);
              setSelection([]);
              setDisposition(value === true ? "all" : "needs-review");
              pagination.resetPage();
            }}
          />
          Improve display names only
        </label>
        {labelOnly && (
          <p>
            Select existing product descriptions, including mapped or protected
            sources. Each selected source must have exactly one Active product
            identity. Follow its pending-review link and explicitly approve it
            first if needed. Suggestions cannot reassign purchases. Names
            require a separate global review.
          </p>
        )}
        <label className="block">
          <span>Model</span>{" "}
          <select
            aria-label="Catalog suggestion model"
            value={model}
            onChange={(event) => setModel(event.target.value)}
          >
            <option value="">Choose a model</option>
            {models.map((name) => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-2" htmlFor={legacyControlId}>
          <Checkbox
            id={legacyControlId}
            disabled={labelOnly}
            checked={includeLegacy}
            onCheckedChange={(value) => setIncludeLegacy(value === true)}
          />
          Include unknown legacy assignments when requesting suggestions
        </label>
        <Button
          disabled={
            !canRequest ||
            !selection.length ||
            !selectionEligible ||
            sources.isFetching ||
            sources.isError ||
            start.isPending
          }
          onClick={() => {
            const body = {
              model,
              sources: selection.map((source) => ({
                source,
                includeLegacyUnknown: labelOnly ? false : includeLegacy,
                labelOnly,
              })),
            };
            const fingerprint = JSON.stringify(body);
            if (intent.current?.fingerprint !== fingerprint)
              intent.current = { fingerprint, id: crypto.randomUUID() };
            start.mutate({ ...body, clientRequestId: intent.current.id });
          }}
        >
          {start.isPending
            ? "Submitting..."
            : labelOnly
              ? "Suggest readable display names"
              : "Suggest normalized names"}
        </Button>
        {start.error && (
          <>
            <p role="alert">{extractErrorMessage(start.error)}</p>
            <PendingIdentityRecovery
              error={start.error}
              source={selection.length === 1 ? selection[0] : undefined}
            />
          </>
        )}
      </fieldset>
    </section>
  );
}
