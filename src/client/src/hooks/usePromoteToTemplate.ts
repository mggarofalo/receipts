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
 * informational toast is shown instead). On successful creation the
 * `similarItems` and `itemTemplates` caches are invalidated so a promoted
 * suggestion's badge flips from History to Template on the next fetch.
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
        // the candidate set as the endpoint allows. A newly created template
        // without an embedding yet scores lower here (semantic term coalesces
        // to 0), so a narrow window risks missing a genuine case-variant
        // duplicate entirely. See RECEIPTS-866 for the full fix (a
        // case-insensitive uniqueness backstop on the create path).
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
          defaultUnitPrice: input.defaultUnitPrice || null,
          defaultItemCode: input.defaultItemCode || null,
        },
      });
      if (error) throw error;

      return { created: true, name };
    },
    onSuccess: (result) => {
      if (result.created) {
        queryClient.invalidateQueries({ queryKey: ["itemTemplates"] });
        queryClient.invalidateQueries({ queryKey: ["similarItems"] });
        toast.success(`Saved "${result.name}" as a template`);
      } else {
        toast.info(`A template named "${result.name}" already exists`);
      }
    },
    onError: () => {
      toast.error("Failed to save as template");
    },
  });
}
