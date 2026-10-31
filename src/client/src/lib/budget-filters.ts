const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Envelope and occasion filters as they appear in a report URL. */
export function parseBudgetFilterParams(params: URLSearchParams) {
  const uuid = (value: string | null) =>
    value && UUID.test(value) ? value : undefined;
  return {
    envelopeId: uuid(params.get("envelopeId")),
    occasionId: uuid(params.get("occasionId")),
  };
}
