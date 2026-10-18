import { RequestFailure } from "@/components/RequestFailure";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { useItemTemplates } from "@/hooks/useItemTemplates";
import { useLinkTemplateMutation } from "@/hooks/useNormalizedDescriptionActions";
import { useMemo, useState } from "react";
import { itemsPhrase } from "./action-copy";
import type { ItemTemplate, NormalizedDescription } from "./types";

interface LinkTemplateDialogProps {
  source: NormalizedDescription;
  onClose: () => void;
}

/**
 * One page of the template list. 100 is the documented ceiling on the shared `limit` parameter, and
 * the page is a window on a server-side search rather than the whole searchable set — so a template
 * past it is still reachable by typing, which is the part that matters.
 */
const TEMPLATE_PICKER_PAGE_SIZE = 100;
/**
 * "This row is an item I already have a template for" (RECEIPTS-930).
 *
 * The consequence is stated per selection rather than in general, because it genuinely differs:
 * choosing a template whose name already matches this row's matched text only records the link,
 * while choosing any other consolidates this row into that template's entry and deletes it. The
 * dialog predicts which by the same rule the server uses — exact, case-insensitive, on the matched
 * text — so the warning is specific instead of hedged. The server is still the authority; the
 * toast reports what actually happened.
 */
export function LinkTemplateDialog({
  source,
  onClose,
}: LinkTemplateDialogProps) {
  const link = useLinkTemplateMutation();
  const [templateId, setTemplateId] = useState<string | undefined>();
  const [search, setSearch] = useState("");
  const debouncedSearch = useDebouncedValue(search);

  // Searched on the server, like the merge dialog since RECEIPTS-879. Filtering the loaded page in
  // the browser can only ever find what was loaded, so a template past the page size read as "no
  // such template exists" while the notice below told you to refine a search that could not reach
  // it — the same failure RECEIPTS-878 removed from merge, rebuilt.
  const { data, total, isLoading, isError, isSuccess, isFetching, refetch } =
    useItemTemplates(0, TEMPLATE_PICKER_PAGE_SIZE, "name", "asc", {
      enabled: true,
      q: debouncedSearch,
    });

  const templates = useMemo(
    () => (data as ItemTemplate[] | undefined) ?? [],
    [data],
  );

  const selected = useMemo(
    () => templates.find((t) => t.id === templateId),
    [templates, templateId],
  );

  const isDebouncing = search.trim() !== debouncedSearch.trim();
  const canChooseTemplate =
    isSuccess && !isFetching && !isDebouncing && !link.isPending;
  const canConfirm = canChooseTemplate && selected !== undefined;

  // The server resolves a template's entry by exact, case-insensitive match on the matched text.
  // Same comparison here, so the dialog can name the actual outcome.
  const willConsolidate =
    selected != null &&
    selected.name.trim().toLowerCase() !==
      source.canonicalName.trim().toLowerCase();

  const sourceCount = source.linkedItemCount ?? 0;

  function handleConfirm() {
    if (!templateId || !canConfirm) return;
    link.mutate(
      { id: source.id, itemTemplateId: templateId },
      { onSuccess: () => onClose() },
    );
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Link to an Item Template</DialogTitle>
          <DialogDescription>
            Pick the template whose item this is. Its entry becomes the one that
            survives, because a template is always represented by the entry
            named after it — so this entry is consolidated into that one unless
            it already is that one.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          <div>
            <Label htmlFor="link-template-search">Search templates</Label>
            <Input
              id="link-template-search"
              value={search}
              onChange={(e) => {
                setSearch(e.target.value);
                setTemplateId(undefined);
              }}
              placeholder="Start typing a template name…"
              className="mt-1"
            />
          </div>
          <div className="max-h-64 overflow-y-auto rounded border">
            {isLoading || isDebouncing ? (
              <Skeleton className="h-24 w-full rounded" />
            ) : isError ? (
              <div className="p-4" data-testid="link-template-error">
                <RequestFailure
                  message="Item templates unavailable. Retry to verify the target before linking."
                  retry={() => {
                    if (source && !isDebouncing) void refetch();
                  }}
                  isRetrying={isFetching}
                />
              </div>
            ) : templates.length === 0 ? (
              <p
                className="p-4 text-sm text-muted-foreground"
                data-testid="link-template-empty"
              >
                {debouncedSearch
                  ? "No templates match your search."
                  : "You have no item templates yet. Create one from the Item Templates page first."}
              </p>
            ) : (
              <ul className="divide-y">
                {templates.map((t) => (
                  <li key={t.id}>
                    <label className="flex cursor-pointer items-center gap-2 p-2 text-sm hover:bg-muted/50">
                      <input
                        type="radio"
                        name="link-template-target"
                        value={t.id}
                        checked={templateId === t.id}
                        disabled={!canChooseTemplate}
                        onChange={() => {
                          if (canChooseTemplate) setTemplateId(t.id);
                        }}
                      />
                      <span className="font-medium">{t.name}</span>
                      {/* The defaults are what makes a template a template, and they are what
                          the reviewer is really identifying the item by. */}
                      {t.defaultCategory && (
                        <span className="ml-auto shrink-0 text-xs text-muted-foreground">
                          {t.defaultCategory}
                          {t.defaultSubcategory
                            ? ` · ${t.defaultSubcategory}`
                            : ""}
                        </span>
                      )}
                    </label>
                  </li>
                ))}
              </ul>
            )}
          </div>
          {/* Now truthful advice: the search runs on the server, so narrowing it genuinely reaches
              templates that are not on this page. */}
          {total > templates.length && (
            <p
              className="text-xs text-muted-foreground"
              data-testid="link-template-truncation-notice"
            >
              Showing {templates.length} of {total} matching templates — keep
              typing to narrow it down.
            </p>
          )}
          {selected && canChooseTemplate && (
            <p className="text-sm" data-testid="link-template-consequence">
              {willConsolidate ? (
                <>
                  “{source.displayName}” will be consolidated into the “
                  {selected.name}” entry.{" "}
                  {/* "No live receipt items", not "no receipt items". The count is live-only, but
                      the merge re-points trashed ones too, so a flat "nothing will be re-pointed"
                      would be false for a row whose items are all in the recycle bin — they would
                      come back attached to a different entry with nothing having said so. */}
                  {sourceCount === 0
                    ? "No live receipt items are linked, so nothing you can see will move."
                    : `Its ${itemsPhrase(sourceCount)} will be re-pointed there.`}{" "}
                  Anything of its own sitting in the recycle bin moves with it.{" "}
                  <strong>This entry is then deleted.</strong> Cannot be undone.
                </>
              ) : (
                <>
                  This entry already is the “{selected.name}” entry. Linking
                  only records the connection — nothing is moved and nothing is
                  deleted.
                </>
              )}
            </p>
          )}
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={handleConfirm} disabled={!canConfirm}>
            {link.isPending ? "Linking…" : "Link"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
