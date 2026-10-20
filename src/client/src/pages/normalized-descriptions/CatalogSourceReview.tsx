import { useId, useRef, useState } from "react";
import { Link } from "react-router";
import { PendingIdentityRecovery } from "./PendingIdentityRecovery";
import {
  useCatalogActions,
  useCatalogCandidates,
  useCatalogMappings,
  type CatalogSource,
  type CatalogOperation,
} from "@/hooks/useNormalizationCatalog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Checkbox } from "@/components/ui/checkbox";
import { RequestFailure } from "@/components/RequestFailure";
import { extractErrorMessage } from "@/lib/problem-details";

export type CatalogSuggestion = {
  targetId?: string | null;
  targetRevision?: string | null;
  newCanonicalName?: string | null;
  includeLegacyUnknown?: boolean;
  suggestionRunId?: string;
  suggestionProposalId?: string;
  suggestionRunRevision?: string;
};

export function CatalogSourceReview({
  source,
  suggestion,
  onPreview,
}: {
  source: CatalogSource;
  suggestion?: CatalogSuggestion;
  onPreview: (operation: CatalogOperation) => void;
}) {
  const [lookup, setLookup] = useState(source.description);
  const candidates = useCatalogCandidates({ ...source, description: lookup });
  const controlId = useId();
  const mappings = useCatalogMappings(source);
  const { preview, revoke } = useCatalogActions(onPreview);
  const [targetId, setTargetId] = useState(suggestion?.targetId ?? "");
  const [reviewedTarget, setReviewedTarget] = useState<{
    id: string;
    revision: string;
  } | null>(null);
  const [newName, setNewName] = useState(suggestion?.newCanonicalName ?? "");
  const [exclude, setExclude] = useState(false);
  const [includeLegacy, setIncludeLegacy] = useState(
    suggestion?.includeLegacyUnknown ?? false,
  );
  const [revokeId, setRevokeId] = useState<string | null>(null);
  const intent = useRef<{ fingerprint: string; id: string } | null>(null);
  const selected = candidates.data?.find(
    (candidate) => candidate.id === targetId,
  );
  // Preserve the observed revision until the user explicitly reviews a newer candidate.
  const targetRevision =
    reviewedTarget?.id === targetId
      ? reviewedTarget.revision
      : targetId === suggestion?.targetId
        ? suggestion.targetRevision
        : null;
  const validTarget = !!targetId && !!targetRevision;
  const pending = preview.isPending || revoke.isPending;
  const error = preview.error ?? revoke.error;
  return (
    <section
      className="space-y-4 rounded-lg border p-4"
      aria-label="Review source description"
    >
      <h2 className="card-title">{source.description}</h2>
      <p>
        {source.location} · Item code: {source.itemCode || "not recorded"}
      </p>
      <p className="text-sm">
        This mapping will apply only to this exact description, store, and item
        code. Review product identity, including meaningful brand, size, and
        variant differences. A shared name does not convert package units or
        prices.
      </p>
      <form
        className="flex gap-2"
        onSubmit={(event) => {
          event.preventDefault();
          const query = String(
            new FormData(event.currentTarget).get("lookup") ?? "",
          ).trim();
          setLookup(query || source.description);
          setTargetId("");
          setReviewedTarget(null);
          setNewName("");
        }}
      >
        <Input
          name="lookup"
          aria-label="Find an existing canonical name"
          placeholder="Search existing names if the shortlist misses the right one"
          maxLength={2000}
          disabled={pending || exclude}
        />
        <Button type="submit" variant="outline" disabled={pending || exclude}>
          Find existing name
        </Button>
      </form>
      <p className="text-sm">
        Only Active identities can be selected here.{" "}
        <Link
          className="underline"
          to={`/admin/normalized-descriptions?${new URLSearchParams({ tab: "review", reviewQ: lookup, catalogSource: JSON.stringify(source) })}`}
        >
          Search pending identities for this name
        </Link>{" "}
        if it is awaiting review.
      </p>
      {candidates.isPending ? (
        <p role="status">Finding existing names...</p>
      ) : candidates.isError ? (
        <RequestFailure
          message="Existing names could not be loaded."
          retry={() => void candidates.refetch()}
          isRetrying={candidates.isFetching}
        />
      ) : (
        <fieldset disabled={pending || exclude} className="space-y-2">
          <legend className="font-medium">Existing catalog candidates</legend>
          {!candidates.data?.length && (
            <p>No candidates found. You can propose a new name.</p>
          )}
          {candidates.data?.map((candidate) => (
            <label key={candidate.id} className="block rounded border p-3">
              <input
                type="radio"
                name="catalog-target"
                value={candidate.id}
                checked={targetId === candidate.id}
                onChange={() => {
                  setTargetId(candidate.id);
                  setReviewedTarget({
                    id: candidate.id,
                    revision: candidate.revision,
                  });
                  setNewName("");
                }}
              />{" "}
              {candidate.displayLabel ?? candidate.canonicalName}
              <span className="block text-sm text-muted-foreground">
                Found by: {candidate.channels.join(", ")}
              </span>
              {candidate.approvedExamples.length > 0 && (
                <span className="block text-sm">
                  Previously approved descriptions:{" "}
                  {candidate.approvedExamples.join("; ")}
                </span>
              )}
            </label>
          ))}
        </fieldset>
      )}
      {selected && targetRevision !== selected.revision && (
        <div className="space-y-2">
          <p role="status">
            This product changed after the original selection. Review its
            current details above before continuing.
          </p>
          <Button
            variant="outline"
            disabled={pending || exclude}
            onClick={() =>
              setReviewedTarget({
                id: selected.id,
                revision: selected.revision,
              })
            }
          >
            Use current candidate details
          </Button>
        </div>
      )}
      {targetId && !selected && (
        <p role="status">
          The suggested existing name is bound to its original revision. The
          server will reject the preview if it has changed.
        </p>
      )}
      <label className="block space-y-1" htmlFor={`${controlId}-name`}>
        <span>Or propose a new canonical name</span>
        <Input
          id={`${controlId}-name`}
          value={newName}
          disabled={pending || exclude}
          maxLength={200}
          onChange={(event) => {
            setNewName(event.target.value);
            setTargetId("");
            setReviewedTarget(null);
          }}
        />
      </label>
      <label
        className="flex items-center gap-2"
        htmlFor={`${controlId}-exclude`}
      >
        <Checkbox
          id={`${controlId}-exclude`}
          checked={exclude}
          disabled={pending}
          onCheckedChange={(value) => setExclude(value === true)}
        />
        Exclude this source from normalization
      </label>
      <label
        className="flex items-center gap-2"
        htmlFor={`${controlId}-legacy`}
      >
        <Checkbox
          id={`${controlId}-legacy`}
          checked={includeLegacy}
          disabled={pending}
          onCheckedChange={(value) => setIncludeLegacy(value === true)}
        />
        Include older assignments whose review history is unknown
      </label>
      <p className="text-sm">
        Manual and template assignments remain protected. Preview shows the
        historical purchases eligible for this change before you approve it.
      </p>
      <Button
        disabled={pending || (!exclude && !validTarget && !newName.trim())}
        onClick={() => {
          const body = {
            source,
            includeLegacyUnknown: includeLegacy,
            exclude,
            targetId: !exclude && validTarget ? targetId : null,
            targetRevision: !exclude && validTarget ? targetRevision : null,
            newCanonicalName: !exclude && !targetId ? newName.trim() : null,
            suggestionRunId: suggestion?.suggestionRunId,
            suggestionProposalId: suggestion?.suggestionProposalId,
            suggestionRunRevision: suggestion?.suggestionRunRevision,
          };
          const fingerprint = JSON.stringify(body);
          if (intent.current?.fingerprint !== fingerprint)
            intent.current = { fingerprint, id: crypto.randomUUID() };
          preview.mutate({ ...body, clientRequestId: intent.current.id });
        }}
      >
        {preview.isPending
          ? "Preparing preview..."
          : "Preview historical changes"}
      </Button>
      {error && (
        <div role="alert" className="space-y-2 text-destructive">
          <p>{extractErrorMessage(error)}</p>
          <PendingIdentityRecovery error={error} source={source} />
        </div>
      )}
      <section className="space-y-2" aria-label="Saved source mappings">
        <h3 className="font-medium">Saved mappings</h3>
        {mappings.isPending ? (
          <p>Loading mappings...</p>
        ) : mappings.isError ? (
          <RequestFailure
            message="Saved mappings could not be loaded."
            retry={() => void mappings.refetch()}
            isRetrying={mappings.isFetching}
          />
        ) : !mappings.data?.length ? (
          <p>No approved mapping yet.</p>
        ) : (
          mappings.data.map((mapping) => (
            <div key={mapping.id} className="space-y-2">
              <p>
                {mapping.targetName ?? "Excluded"} · {mapping.state}
              </p>
              {mapping.state === "active" &&
                (revokeId === mapping.id ? (
                  <>
                    <p>
                      Stop reusing this mapping for new purchases? Existing
                      assignments remain; use the operation history to undo
                      eligible historical changes.
                    </p>
                    <Button
                      variant="destructive"
                      disabled={pending}
                      onClick={() =>
                        revoke.mutate(mapping, {
                          onSuccess: () => setRevokeId(null),
                        })
                      }
                    >
                      Confirm revoke
                    </Button>
                    <Button
                      variant="ghost"
                      disabled={pending}
                      onClick={() => setRevokeId(null)}
                    >
                      Keep mapping
                    </Button>
                  </>
                ) : (
                  <Button
                    variant="outline"
                    disabled={pending}
                    onClick={() => setRevokeId(mapping.id)}
                  >
                    Revoke future reuse
                  </Button>
                ))}
            </div>
          ))
        )}
      </section>
    </section>
  );
}
