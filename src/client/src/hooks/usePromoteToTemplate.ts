import { useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";

export interface PromoteToTemplateInput {
  name: string;
  defaultCategory?: string | null;
  defaultSubcategory?: string | null;
  defaultUnitPrice?: number | null;
  defaultItemCode?: string | null;
}

export interface PromoteToTemplateResult {
  /** false when an existing template with the same name blocked creation */
  created: boolean;
  name: string;
}

/**
 * One-click promotion of a history suggestion or an entered line item to an
 * item template.
 *
 * Frontend-side duplicate guard: before creating, queries
 * `GET /api/item-templates/similar` with the exact name and skips creation
 * when a template-source result matches the name case-insensitively (an
 * informational toast is shown instead). pg_trgm's similarity() lowercases
 * before comparing, so a case-variant name always scores a perfect trigram
 * match — the risk is a *newly created* template's combined score being
 * dragged down by its not-yet-generated embedding (async, up to ~40s lag),
 * letting it rank outside the query's result window. See RECEIPTS-866.
 *
 * On successful creation, only the `itemTemplates` cache is invalidated —
 * deliberately NOT `similarItems`. Invalidating it would force an immediate
 * refetch while the new template still has no embedding, which (for the
 * same reason as the duplicate-guard risk above) can rank the
 * just-promoted item low enough to drop off a small result window
 * entirely — turning "the badge flips to Template" into "the suggestion
 * disappears." Leaving the cache alone lets it refresh naturally once
 * `staleTime` elapses, by which point the embedding has usually landed.
 */
export function usePromoteToTemplate() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (
      input: PromoteToTemplateInput,
    ): Promise<PromoteToTemplateResult> => {
      const name = input.name.trim();

      // The /similar endpoint enforces a 2-character minimum on the query,
      // but template names themselves have no such minimum. Skip the
      // duplicate check for short names rather than let a validation error
      // block creation entirely.
      if (name.length >= 2) {
        // limit: 20 (the API's max) rather than a small page size — this is a
        // duplicate check, not a suggestions list, so it should see as much of
        // the candidate set as the endpoint allows. A recently created
        // template without an embedding yet scores lower here (semantic term
        // coalesces to 0), so a narrow window risks missing it during that
        // window. See RECEIPTS-866 for a DB-level backstop (this frontend
        // check can never be airtight against concurrent creates, e.g. two
        // tabs promoting the same name at once).
        const { data: similar, error: similarError } = await client.GET(
          "/api/item-templates/similar",
          { params: { query: { q: name, limit: 20, threshold: 0.3 } } },
        );
        if (similarError) throw similarError;

        const isDuplicate = (similar ?? []).some(
          (item) =>
            item.source === "template" &&
            item.name.toLowerCase() === name.toLowerCase(),
        );
        if (isDuplicate) return { created: false, name };
      }

      const { error } = await client.POST("/api/item-templates", {
        body: {
          name,
          defaultCategory: input.defaultCategory || null,
          defaultSubcategory: input.defaultSubcategory || null,
          // The backend rejects DefaultUnitPrice <= 0 (must be positive when
          // present); history rows can carry non-positive prices that were
          // never validated on the way in (e.g. backup/legacy imports), so
          // guard here rather than pass them straight through.
          defaultUnitPrice:
            input.defaultUnitPrice && input.defaultUnitPrice > 0
              ? input.defaultUnitPrice
              : null,
          defaultItemCode: input.defaultItemCode || null,
        },
      });
      if (error) throw error;

      return { created: true, name };
    },
    onSuccess: (result) => {
      if (result.created) {
        queryClient.invalidateQueries({ queryKey: ["itemTemplates"] });
        toast.success(`Saved "${result.name}" as a template`);
      } else {
        toast.info(`A template named "${result.name}" already exists`);
      }
    },
  });
}
