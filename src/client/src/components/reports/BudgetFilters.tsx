import { useEnvelopes, useOccasions } from "@/hooks/useEnvelopes";

/**
 * Narrows a report to one envelope or occasion. Archived ones stay listed,
 * because their purchases are still history.
 */
export function BudgetFilters({
  envelopeId,
  occasionId,
  showEnvelope = true,
  showOccasion = true,
  onChange,
}: {
  envelopeId?: string;
  occasionId?: string;
  showEnvelope?: boolean;
  showOccasion?: boolean;
  onChange: (patch: {
    envelopeId?: string | null;
    occasionId?: string | null;
  }) => void;
}) {
  const { envelopes } = useEnvelopes();
  const { occasions } = useOccasions();
  return (
    <>
      {showEnvelope && envelopes.length > 0 && (
        <label className="text-sm">
          Envelope{" "}
          <select
            aria-label="Envelope filter"
            className="rounded border bg-background p-1"
            value={envelopeId ?? ""}
            onChange={(event) =>
              onChange({ envelopeId: event.target.value || null })
            }
          >
            <option value="">All envelopes</option>
            {envelopes.map((envelope) => (
              <option key={envelope.id} value={envelope.id}>
                {envelope.isActive
                  ? envelope.name
                  : `${envelope.name} (archived)`}
              </option>
            ))}
          </select>
        </label>
      )}
      {showOccasion && occasions.length > 0 && (
        <label className="text-sm">
          Occasion{" "}
          <select
            aria-label="Occasion filter"
            className="rounded border bg-background p-1"
            value={occasionId ?? ""}
            onChange={(event) =>
              onChange({ occasionId: event.target.value || null })
            }
          >
            <option value="">All occasions</option>
            {occasions.map((occasion) => (
              <option key={occasion.id} value={occasion.id}>
                {occasion.isActive
                  ? occasion.name
                  : `${occasion.name} (archived)`}
              </option>
            ))}
          </select>
        </label>
      )}
    </>
  );
}
