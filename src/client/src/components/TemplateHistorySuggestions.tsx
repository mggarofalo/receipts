import { useCallback, useRef, useState, type RefObject } from "react";
import { ChevronDown } from "lucide-react";
import type { components } from "@/generated/api";
import { useTemplateHistoryCandidates } from "@/hooks/useTemplateHistoryCandidates";
import { useCreateItemTemplate } from "@/hooks/useItemTemplates";
import { Button } from "@/components/ui/button";
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Spinner } from "@/components/ui/spinner";
import { formatCurrency, formatShortDate } from "@/lib/format";

type HistoryCandidate =
  components["schemas"]["ItemTemplateHistoryCandidateResponse"];

const COLLAPSED_STORAGE_KEY = "item-templates-history-suggestions-collapsed";
const PAGE_SIZE = 10;
// Matches the API's validated maximum (GetItemTemplateHistoryCandidatesQueryValidator).
// Requesting past it returns 400, which would otherwise collapse the whole section.
const MAX_LIMIT = 500;
const HEADING_ID = "item-template-history-suggestions-heading";
const COUNT_ID = "item-template-history-suggestions-count";

function readCollapsed(): boolean {
  return localStorage.getItem(COLLAPSED_STORAGE_KEY) === "true";
}

interface TemplateHistorySuggestionsProps {
  /**
   * Focus target used when creating the last remaining candidate: the section
   * unmounts itself once the list is empty, so the trigger button that would
   * normally receive focus after a create is gone by the time focus would land.
   */
  fallbackFocusRef?: RefObject<HTMLElement | null>;
}

/**
 * "Suggested from your history": recurring receipt-item descriptions that have no
 * item template yet, each creatable in one click. Renders nothing at all when there
 * are no candidates, so the Item Templates page is unchanged for a clean history.
 */
export function TemplateHistorySuggestions({
  fallbackFocusRef,
}: TemplateHistorySuggestionsProps) {
  const [limit, setLimit] = useState(PAGE_SIZE);
  const [collapsed, setCollapsed] = useState(readCollapsed);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const { data, total, isError, refetch } = useTemplateHistoryCandidates(
    0,
    limit,
  );
  const {
    mutate: createTemplate,
    isPending,
    variables,
  } = useCreateItemTemplate();

  const handleOpenChange = useCallback((open: boolean) => {
    setCollapsed(!open);
    localStorage.setItem(COLLAPSED_STORAGE_KEY, String(!open));
  }, []);

  const handleShowMore = useCallback(() => {
    setLimit((current) => Math.min(current + PAGE_SIZE, MAX_LIMIT));
  }, []);

  const candidates = (data as HistoryCandidate[] | undefined) ?? [];

  const handleCreate = useCallback(
    (candidate: HistoryCandidate) => {
      // The buttons stay focusable while a create is in flight (aria-disabled,
      // not disabled, so focus is never yanked out of the table) — so guard the
      // duplicate submit here instead.
      if (isPending) return;

      // If this is the only remaining candidate, the successful create makes
      // the list empty and the whole section — including the trigger button —
      // unmounts. Focusing the about-to-vanish trigger would just lose focus
      // to the document body, so fall back to a target the page guarantees
      // stays mounted.
      const isLastCandidate = candidates.length === 1;

      createTemplate(
        {
          name: candidate.name,
          description: null,
          defaultCategory: candidate.suggestedCategory ?? null,
          defaultSubcategory: candidate.suggestedSubcategory ?? null,
          // History rows can carry non-positive prices that were never
          // validated on the way in (e.g. backup/legacy imports); the API
          // rejects DefaultUnitPrice <= 0, so a candidate with one would be a
          // one-click dead end that fails every time it's clicked.
          defaultUnitPrice:
            candidate.suggestedUnitPrice && candidate.suggestedUnitPrice > 0
              ? candidate.suggestedUnitPrice
              : null,
          defaultItemCode: candidate.suggestedItemCode ?? null,
        },
        {
          // The created row disappears on refetch, taking the pressed button
          // with it. Park focus on the section trigger so keyboard users are not
          // dumped back at the top of the document.
          onSuccess: () => {
            if (isLastCandidate) {
              fallbackFocusRef?.current?.focus();
            } else {
              triggerRef.current?.focus();
            }
          },
        },
      );
    },
    [createTemplate, isPending, candidates.length, fallbackFocusRef],
  );

  // Nothing to suggest (or nothing loaded yet) — render no section at all rather
  // than an empty shell the user has to look past. A fetch error is NOT the same
  // as "no candidates" and must not collapse to the same silent nothing: without
  // this, any transient error here (or a "Show more" request that ever slipped
  // past the MAX_LIMIT cap) would make the whole section vanish with no way to
  // recover short of a full page reload.
  if (candidates.length === 0 && !isError) {
    return null;
  }

  const pendingName = isPending ? variables?.name : undefined;

  return (
    <section aria-labelledby={HEADING_ID} className="mb-6">
      <Collapsible open={!collapsed} onOpenChange={handleOpenChange}>
        <div className="flex flex-wrap items-center justify-between gap-2 py-2">
          <h2 id={HEADING_ID} className="text-base font-semibold">
            <CollapsibleTrigger asChild>
              <button
                ref={triggerRef}
                type="button"
                aria-describedby={COUNT_ID}
                className="flex items-center gap-2 rounded-md px-1 py-1 outline-none hover:underline focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px]"
              >
                <ChevronDown
                  aria-hidden="true"
                  className={`h-4 w-4 transition-transform ${collapsed ? "-rotate-90" : ""}`}
                />
                Suggested from your history
              </button>
            </CollapsibleTrigger>
          </h2>
          <span id={COUNT_ID} className="text-sm text-muted-foreground">
            {total} recurring {total === 1 ? "item" : "items"} without a
            template
          </span>
        </div>

        <CollapsibleContent>
          {isError ? (
            <div className="rounded-md border border-destructive/50 p-4 text-sm text-muted-foreground">
              Couldn&apos;t load suggestions from your history.{" "}
              <button
                type="button"
                className="underline underline-offset-2 hover:text-foreground"
                onClick={() => refetch()}
              >
                Try again
              </button>
            </div>
          ) : (
            <>
              <div className="rounded-md border">
                <Table aria-labelledby={HEADING_ID}>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Item</TableHead>
                      <TableHead>Occurrences</TableHead>
                      <TableHead>Category</TableHead>
                      <TableHead>Subcategory</TableHead>
                      <TableHead>Unit Price</TableHead>
                      <TableHead>Last purchased</TableHead>
                      <TableHead className="w-40">Actions</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {candidates.map((candidate) => (
                      <TableRow key={candidate.name}>
                        <TableCell>{candidate.name}</TableCell>
                        <TableCell className="text-muted-foreground">
                          seen {candidate.occurrenceCount} times
                        </TableCell>
                        <TableCell className="text-muted-foreground">
                          {candidate.suggestedCategory ?? (
                            <span className="italic">--</span>
                          )}
                        </TableCell>
                        <TableCell className="text-muted-foreground">
                          {candidate.suggestedSubcategory ?? (
                            <span className="italic">--</span>
                          )}
                        </TableCell>
                        <TableCell className="text-muted-foreground">
                          {candidate.suggestedUnitPrice != null ? (
                            formatCurrency(candidate.suggestedUnitPrice)
                          ) : (
                            <span className="italic">--</span>
                          )}
                        </TableCell>
                        <TableCell className="text-muted-foreground">
                          {formatShortDate(candidate.lastPurchasedAt)}
                        </TableCell>
                        <TableCell>
                          <Button
                            variant="outline"
                            size="sm"
                            aria-label={`Create template for ${candidate.name}`}
                            aria-disabled={isPending}
                            aria-busy={pendingName === candidate.name}
                            onClick={() => handleCreate(candidate)}
                          >
                            {pendingName === candidate.name && (
                              <Spinner size="sm" />
                            )}
                            Create template
                          </Button>
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </div>

              {candidates.length < total && limit < MAX_LIMIT && (
                <div className="flex justify-center py-3">
                  <Button variant="ghost" size="sm" onClick={handleShowMore}>
                    Show more suggestions
                  </Button>
                </div>
              )}
            </>
          )}
        </CollapsibleContent>
      </Collapsible>
    </section>
  );
}
