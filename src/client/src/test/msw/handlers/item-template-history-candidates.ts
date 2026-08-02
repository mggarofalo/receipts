import { http, HttpResponse } from "msw";
import { itemTemplateHistoryCandidates } from "../fixtures/item-template-history-candidates";

// msw matches paths exactly, so the item-templates list handler never swallows this
// sub-path. Keeping the handler in the default set means every integration test that
// renders the Item Templates page has a response for the suggestions section.
export const itemTemplateHistoryCandidateHandlers = [
  http.get("*/api/item-templates/history-candidates", ({ request }) => {
    const url = new URL(request.url);
    const offset = Number(url.searchParams.get("offset") ?? 0);
    const limit = Number(url.searchParams.get("limit") ?? 10);
    const minCount = Number(url.searchParams.get("minCount") ?? 2);

    const matching = itemTemplateHistoryCandidates.filter(
      (candidate) => candidate.occurrenceCount >= minCount,
    );

    return HttpResponse.json({
      data: matching.slice(offset, offset + limit),
      total: matching.length,
      offset,
      limit,
    });
  }),
];
