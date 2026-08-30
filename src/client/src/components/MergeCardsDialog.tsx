import { useMemo, useRef, useState } from "react";
import { toast } from "sonner";
import client from "@/lib/api-client";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { AlertCircle } from "lucide-react";
import {
  useMergeCards,
  useMergeCardsPreview,
  isMergeCardsConflict,
  type MergeCardsConflict,
  type YnabMappingConflict,
} from "@/hooks/useCards";
import { useAllAccounts, useAccountsCards, useCreateAccount } from "@/hooks/useAccounts";

export interface SelectedCardSummary {
  id: string;
  name: string;
  cardCode: string;
  /** The card's current account. Used to spot a merge that would change nothing. */
  accountId: string;
}

interface MergeCardsDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  selectedCards: SelectedCardSummary[];
  onMergeComplete?: () => void;
  /**
   * Adds cards to the caller's selection. Used by the "include the other N" action
   * when a source account is only partly selected — the selection lives on the
   * /cards page, so completing it has to go back through the caller.
   */
  onIncludeCards?: (cardIds: string[]) => void;
}

type TargetMode = "existing" | "new";

export function MergeCardsDialog({
  open,
  onOpenChange,
  selectedCards,
  onMergeComplete,
  onIncludeCards,
}: MergeCardsDialogProps) {
  const [targetMode, setTargetMode] = useState<TargetMode>("existing");
  const [targetAccountId, setTargetAccountId] = useState<string>("");
  const [newAccountName, setNewAccountName] = useState<string>("");
  const [conflict, setConflict] = useState<MergeCardsConflict | null>(null);
  const [winnerAccountId, setWinnerAccountId] = useState<string | null>(null);
  // Cards pulled in by "include the other N" that the /cards page cannot supply,
  // because they sit on a page it is not showing. The caller is told about them
  // too, but it can only reflect the ones it happens to hold — so the dialog owns
  // the authoritative set it will submit (RECEIPTS-888).
  const [includedCardIds, setIncludedCardIds] = useState<string[]>([]);
  // Tracks an account the dialog created as the merge target. If the user closes
  // the dialog before the merge lands, the account is deleted to avoid leaking
  // empty accounts into the DB.
  const pendingCreatedAccountIdRef = useRef<string | null>(null);
  // The name that account was actually created with. Without this an edit made
  // after a failed merge cannot be told apart from a plain retry, and the merge
  // silently lands on an account still carrying the original name.
  const pendingCreatedAccountNameRef = useRef<string | null>(null);

  const { data: accountsData } = useAllAccounts(true);
  const createAccount = useCreateAccount();
  const mergeCards = useMergeCards();

  /**
   * Deletes an account this dialog created but did not end up merging into.
   *
   * Deliberately not awaited by its callers — closing the dialog should not
   * wait on the network — but it must never be fire-and-forget either: if the
   * delete fails (offline, 403, a race with the merge) the empty account is
   * leaked permanently, and saying nothing leaves the user unable to even know
   * to clean it up. Resolves rather than rejects so no caller needs a .catch.
   */
  async function discardCreatedAccount(accountId: string) {
    try {
      const { error } = await client.DELETE("/api/accounts/{id}", {
        params: { path: { id: accountId } },
      });
      if (error) throw error;
    } catch {
      toast.error(
        "Couldn't remove the empty account created for this merge. You may need to delete it manually.",
      );
    }
  }

  function handleOpenChange(next: boolean) {
    if (!next) {
      const leaked = pendingCreatedAccountIdRef.current;
      if (leaked) {
        pendingCreatedAccountIdRef.current = null;
        pendingCreatedAccountNameRef.current = null;
        void discardCreatedAccount(leaked);
      }
      setTargetMode("existing");
      setTargetAccountId("");
      setNewAccountName("");
      setConflict(null);
      setWinnerAccountId(null);
      setIncludedCardIds([]);
    }
    onOpenChange(next);
  }

  const accounts = accountsData ?? [];
  // Keyed on accountsData, not the `?? []` above: that fallback builds a fresh array
  // every render, so memoising on it would rebuild the map each time.
  const accountNamesById = useMemo(
    () => new Map((accountsData ?? []).map((a) => [a.id, a.name])),
    [accountsData],
  );

  // Every account the selection would empty out. The target is excluded because its
  // own cards are staying put — only accounts the merge will delete matter here.
  // Derived from the caller's selection alone: a card pulled in by "include the
  // other N" always belongs to an account that is already in this set, so feeding
  // the effective selection back in here would be circular for no gain.
  const sourceAccountIds = useMemo(() => {
    const ids = new Set<string>();
    for (const card of selectedCards) {
      if (card.accountId && card.accountId !== targetAccountId) ids.add(card.accountId);
    }
    return [...ids].sort();
  }, [selectedCards, targetAccountId]);

  // The full card set of each of those accounts — not just what happens to be on the
  // current page of /cards, which is the reason this could not be checked before.
  const { cardsByAccountId, isLoading: sourceCardsLoading } =
    useAccountsCards(sourceAccountIds);

  /**
   * What this dialog will actually submit: the caller's selection plus anything the
   * user added with "include the other N".
   *
   * Those extras cannot come from the caller. It derives its selection from the page
   * of cards it is displaying, and the whole point of the affordance is to reach cards
   * that page is not showing — so the dialog resolves them from the account card lists
   * it already fetched.
   */
  const effectiveCards = useMemo(() => {
    if (includedCardIds.length === 0) return selectedCards;

    const byId = new Map(selectedCards.map((c) => [c.id, c]));
    const wanted = new Set(includedCardIds);
    for (const cards of cardsByAccountId.values()) {
      for (const card of cards) {
        if (wanted.has(card.id) && !byId.has(card.id)) byId.set(card.id, card);
      }
    }
    return [...byId.values()];
  }, [selectedCards, includedCardIds, cardsByAccountId]);

  // A merge whose cards all already sit on the chosen target changes nothing. The
  // server handles that idempotently and now says so, but telling the user before
  // they commit is better than telling them after (RECEIPTS-893). "New account" mode
  // is exempt: an account that does not exist yet cannot already hold the cards.
  const wouldChangeNothing =
    targetMode === "existing" &&
    targetAccountId !== "" &&
    effectiveCards.length > 0 &&
    effectiveCards.every((c) => c.accountId === targetAccountId);

  /**
   * Source accounts the selection would only partly merge.
   *
   * The server refuses these: a merge that left cards behind on an account it is about
   * to delete would orphan them and reassign their unrelated transactions. That rule is
   * right, but it used to arrive only after submitting, naming no cards and offering no
   * way to fix it — and the sibling cards might not even be on screen to select
   * (RECEIPTS-888).
   */
  const incompleteAccounts = useMemo(() => {
    const chosenIds = new Set(effectiveCards.map((c) => c.id));
    return sourceAccountIds
      .map((accountId) => {
        const all = cardsByAccountId.get(accountId);
        if (!all) return null;
        const missing = all.filter((c) => !chosenIds.has(c.id));
        if (missing.length === 0) return null;
        return {
          accountId,
          accountName: accountNamesById.get(accountId) ?? "this account",
          missing,
        };
      })
      .filter((a): a is NonNullable<typeof a> => a !== null);
  }, [sourceAccountIds, cardsByAccountId, effectiveCards, accountNamesById]);

  const hasIncompleteSelection = incompleteAccounts.length > 0;
  const missingCardIds = useMemo(
    () => incompleteAccounts.flatMap((a) => a.missing.map((c) => c.id)),
    [incompleteAccounts],
  );

  /**
   * What the merge would actually do, fetched before the user commits.
   *
   * Only asked for once the selection is whole and would change something — a preview of
   * a merge the server would reject describes nothing.
   *
   * In "New account" mode the target is null, meaning "an account that does not exist
   * yet". That is what lets the account be created *after* the selection is known to be
   * valid rather than before (RECEIPTS-902); a hypothetical target holds no cards, which
   * is exactly what a fresh account would be.
   */
  const targetIsReady =
    targetMode === "existing" ? targetAccountId !== "" : newAccountName.trim().length > 0;
  const previewInput =
    targetIsReady &&
    effectiveCards.length > 0 &&
    !hasIncompleteSelection &&
    !wouldChangeNothing &&
    !sourceCardsLoading
      ? {
          targetAccountId: targetMode === "existing" ? targetAccountId : null,
          sourceCardIds: effectiveCards.map((c) => c.id),
          ynabMappingWinnerAccountId: winnerAccountId,
        }
      : null;
  const { data: preview, isFetching: previewLoading } = useMergeCardsPreview(previewInput);

  function includeMissingCards() {
    setIncludedCardIds((prev) => [...new Set([...prev, ...missingCardIds])]);
    // Tell the page too, so any of these it *is* showing tick their checkbox and the
    // "Merge (n)" count stays truthful. It cannot hold the ones it is not showing,
    // which is why the dialog keeps its own copy above.
    onIncludeCards?.(missingCardIds);
  }

  const isSubmitDisabled =
    // One card is enough (RECEIPTS-887). What actually has to hold is that the merge
    // would move something, and `wouldChangeNothing` below is the check for that.
    effectiveCards.length < 1 ||
    (targetMode === "existing" && !targetAccountId) ||
    (targetMode === "new" && newAccountName.trim().length === 0) ||
    mergeCards.isPending ||
    createAccount.isPending ||
    wouldChangeNothing ||
    // Submitting would earn a 400 the user cannot act on. Hold it until the
    // selection is whole, or until they include the rest with one click.
    hasIncompleteSelection ||
    // Until those card lists land the completeness check has nothing to go on, and
    // an unchecked submit is exactly the blind 400 this is meant to prevent.
    sourceCardsLoading ||
    // Same reasoning one step later: while the impact is still being computed the
    // user would be confirming something they have not been shown (RECEIPTS-889).
    previewLoading ||
    // In "New account" mode submit is what *creates* the account, so it must not be
    // reachable until the preview has confirmed the merge would be accepted. A merge
    // rejected after creation strands an empty account nobody can find (RECEIPTS-902).
    (targetMode === "new" && !preview) ||
    (conflict !== null && !winnerAccountId);

  /**
   * Resolves the target account id for "New account" mode, creating the account
   * on first submit and reusing it on retries.
   *
   * Called only after the preview has accepted the selection (RECEIPTS-902), so the
   * merge that follows fails for far fewer reasons than it used to — every rejection
   * the server can predict has already happened, against a hypothetical target, with
   * nothing created. What remains is the unavoidable gap: the merge needs a real
   * account id, so the account must exist a moment before the merge runs. The cleanup
   * machinery below still covers a crash inside that gap.
   *
   * Reuse is only safe while the name still matches what we created. A retry
   * after the user corrects the name must apply that correction, or the merge
   * lands on an account carrying the original name with nothing to indicate it
   * (RECEIPTS-894). Renaming rather than re-creating keeps it to one request and
   * cannot leak a second empty account.
   */
  async function resolveNewAccountTarget(name: string): Promise<string | null> {
    const pendingId = pendingCreatedAccountIdRef.current;

    if (!pendingId) {
      const created = await createAccount.mutateAsync({ name, isActive: true });
      if (!created?.id) return null;
      pendingCreatedAccountIdRef.current = created.id;
      pendingCreatedAccountNameRef.current = name;
      return created.id;
    }

    if (pendingCreatedAccountNameRef.current !== name) {
      const { error } = await client.PUT("/api/accounts/{id}", {
        params: { path: { id: pendingId } },
        body: { id: pendingId, name, isActive: true },
      });
      if (error) throw error;
      pendingCreatedAccountNameRef.current = name;
    }

    return pendingId;
  }

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();

    let resolvedTargetId = targetAccountId;

    // The conflict-resolution retry deliberately does NOT come through here:
    // the conflict handler below switches targetMode to "existing" and pins
    // targetAccountId to the created account, so resolvedTargetId already
    // carries it. Keying reuse off the ref instead is what made an ordinary
    // failed retry indistinguishable from conflict resolution.
    if (targetMode === "new") {
      try {
        const created = await resolveNewAccountTarget(newAccountName.trim());
        if (!created) return;
        resolvedTargetId = created;
      } catch {
        // Surfaced by the global error handler.
        return;
      }
    }

    try {
      await mergeCards.mutateAsync({
        targetAccountId: resolvedTargetId,
        sourceCardIds: effectiveCards.map((c) => c.id),
        ynabMappingWinnerAccountId: winnerAccountId,
      });
      // The created account is only legitimately owned if the merge actually
      // landed on it. If the user created one and then switched to an existing
      // target, clearing the ref unconditionally would leak it.
      const createdId = pendingCreatedAccountIdRef.current;
      pendingCreatedAccountIdRef.current = null;
      pendingCreatedAccountNameRef.current = null;
      if (createdId && createdId !== resolvedTargetId) {
        void discardCreatedAccount(createdId);
      }
      handleOpenChange(false);
      onMergeComplete?.();
    } catch (err) {
      if (isMergeCardsConflict(err)) {
        setConflict(err);
        if (targetMode === "new") {
          setTargetAccountId(resolvedTargetId);
          setTargetMode("existing");
        }
        return;
      }
      // non-conflict errors are surfaced via toast in the hook
    }
  }

  return (
    <Dialog open={open} onOpenChange={handleOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Merge cards into account</DialogTitle>
          <DialogDescription>
            Repoints the selected cards and their transactions to the target
            account. Any source accounts left without cards will be removed.
          </DialogDescription>
        </DialogHeader>

        <form onSubmit={handleSubmit} className="space-y-4">
          <div className="space-y-1">
            <div className="text-sm font-medium">
              Merging {effectiveCards.length} card
              {effectiveCards.length === 1 ? "" : "s"}
            </div>
            <ul className="rounded-md border bg-muted/30 p-2 text-sm max-h-32 overflow-y-auto">
              {effectiveCards.map((card) => (
                <li key={card.id} className="py-0.5">
                  <span className="font-mono text-xs">{card.cardCode}</span>{" "}
                  <span>{card.name}</span>
                  {/* Which account a card belongs to is the whole basis of the
                      all-or-nothing rule below, and the dialog used not to show it
                      at all — leaving the rejection unintelligible (RECEIPTS-888). */}
                  {accountNamesById.has(card.accountId) && (
                    <span className="text-muted-foreground">
                      {" — "}
                      {accountNamesById.get(card.accountId)}
                    </span>
                  )}
                </li>
              ))}
            </ul>
          </div>

          {hasIncompleteSelection && (
            <Alert>
              <AlertCircle className="h-4 w-4" />
              <AlertDescription>
                <div className="font-medium mb-2">
                  {incompleteAccounts.length === 1
                    ? "One account would be left with cards behind"
                    : `${incompleteAccounts.length} accounts would be left with cards behind`}
                </div>
                <p className="text-sm mb-2">
                  A source account is emptied and removed by the merge, so every one of
                  its cards has to come along — otherwise the ones left behind would be
                  orphaned and their transactions reassigned. These are not selected yet:
                </p>
                <ul className="mb-2 space-y-1 text-sm">
                  {incompleteAccounts.map((account) => (
                    <li key={account.accountId}>
                      <span className="font-medium">{account.accountName}</span>
                      {": "}
                      {account.missing.map((c) => `${c.cardCode} ${c.name}`).join(", ")}
                    </li>
                  ))}
                </ul>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={includeMissingCards}
                >
                  Include the other {missingCardIds.length} card
                  {missingCardIds.length === 1 ? "" : "s"}
                </Button>
              </AlertDescription>
            </Alert>
          )}

          <fieldset className="space-y-2" disabled={conflict !== null}>
            <legend className="text-sm font-medium">Target account</legend>
            <div className="flex items-center gap-4 text-sm">
              <label className="flex items-center gap-2">
                <input
                  type="radio"
                  name="target-mode"
                  value="existing"
                  checked={targetMode === "existing"}
                  onChange={() => setTargetMode("existing")}
                />
                <span>Existing account</span>
              </label>
              <label className="flex items-center gap-2">
                <input
                  type="radio"
                  name="target-mode"
                  value="new"
                  checked={targetMode === "new"}
                  onChange={() => setTargetMode("new")}
                />
                <span>New account</span>
              </label>
            </div>

            {targetMode === "existing" ? (
              <div className="space-y-1">
                <Label htmlFor="target-account">Select account</Label>
                <Select value={targetAccountId} onValueChange={setTargetAccountId}>
                  <SelectTrigger id="target-account" aria-label="Target account">
                    <SelectValue placeholder="Choose an account" />
                  </SelectTrigger>
                  <SelectContent>
                    {accounts.map((a) => (
                      <SelectItem key={a.id} value={a.id}>
                        {a.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                {wouldChangeNothing && (
                  <p role="status" className="text-sm text-muted-foreground">
                    {effectiveCards.length === 1
                      ? "This card already belongs to this account"
                      : "Every selected card already belongs to this account"}{" "}
                    — there is nothing to merge. Choose a different target.
                  </p>
                )}
              </div>
            ) : (
              <div className="space-y-1">
                <Label htmlFor="new-account-name">New account name</Label>
                <Input
                  id="new-account-name"
                  value={newAccountName}
                  onChange={(e) => setNewAccountName(e.target.value)}
                  placeholder="e.g. Apple Card"
                />
              </div>
            )}
          </fieldset>

          {/* RECEIPTS-889. Merging deletes the emptied source accounts and repoints
              every one of their transactions, trashed ones included, with no undo.
              The only warning used to be the line of prose in the dialog header. */}
          {previewLoading && (
            <p role="status" className="text-sm text-muted-foreground">
              Working out what this merge would change…
            </p>
          )}

          {!previewLoading && preview && !preview.conflicts && (
            <Alert>
              <AlertCircle className="h-4 w-4" />
              <AlertDescription>
                <div className="font-medium mb-2">This merge cannot be undone</div>
                <ul className="space-y-1 text-sm">
                  <li>
                    {preview.cardsToMove} card{preview.cardsToMove === 1 ? "" : "s"} moved
                  </li>
                  <li>
                    {preview.transactionsToRepoint} transaction
                    {preview.transactionsToRepoint === 1 ? "" : "s"} repointed
                  </li>
                  {preview.trashedTransactionsToRepoint > 0 && (
                    <li>
                      {preview.trashedTransactionsToRepoint} trashed transaction
                      {preview.trashedTransactionsToRepoint === 1 ? "" : "s"} repointed —
                      these are only visible in the recycle bin, and they move too
                    </li>
                  )}
                  {preview.accountsToRemove.length > 0 && (
                    <li>
                      <span className="font-medium">Deleted permanently:</span>{" "}
                      {preview.accountsToRemove.map((a) => a.name).join(", ")}
                    </li>
                  )}
                  {preview.survivingYnabMapping && (
                    <li>
                      YNAB mapping kept:{" "}
                      <span className="font-mono text-xs">
                        {preview.survivingYnabMapping.ynabAccountName}
                      </span>{" "}
                      (from {preview.survivingYnabMapping.fromAccountName})
                    </li>
                  )}
                </ul>
              </AlertDescription>
            </Alert>
          )}

          {conflict && (
            <Alert variant="destructive">
              <AlertCircle className="h-4 w-4" />
              <AlertDescription>
                <div className="font-medium mb-2">YNAB mapping conflict</div>
                <p className="text-sm mb-2">{conflict.message}</p>
                <fieldset className="space-y-1">
                  <legend className="text-sm font-medium mb-1">
                    Keep which mapping?
                  </legend>
                  {conflict.conflicts.map((c: YnabMappingConflict) => (
                    <label key={c.accountId} className="flex items-start gap-2 text-sm">
                      <input
                        type="radio"
                        name="mapping-winner"
                        value={c.accountId}
                        checked={winnerAccountId === c.accountId}
                        onChange={() => setWinnerAccountId(c.accountId)}
                        className="mt-1"
                      />
                      <span>
                        <span className="font-medium">{c.accountName || "(target)"}</span>
                        {" → "}
                        <span className="font-mono text-xs">{c.ynabAccountName}</span>
                      </span>
                    </label>
                  ))}
                </fieldset>
              </AlertDescription>
            </Alert>
          )}

          <DialogFooter>
            <Button
              type="button"
              variant="outline"
              onClick={() => handleOpenChange(false)}
              disabled={mergeCards.isPending}
            >
              Cancel
            </Button>
            <Button type="submit" disabled={isSubmitDisabled}>
              {mergeCards.isPending
                ? "Merging..."
                : conflict
                  ? "Resubmit with resolution"
                  : "Merge"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
