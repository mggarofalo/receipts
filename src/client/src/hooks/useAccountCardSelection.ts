import { useCallback, useMemo, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useAllAccounts, useAccountCards } from "@/hooks/useAccounts";
import { useAllCards } from "@/hooks/useCards";
import { accountToOption, cardToOption } from "@/lib/combobox-options";
import type { components } from "@/generated/api";

export interface AccountCardSelection {
  accountId: string;
  cardId: string;
}
type Card = components["schemas"]["CardResponse"];
function membershipError(
  value: AccountCardSelection,
  cards: Card[] | undefined,
) {
  if (!value.accountId || !value.cardId || !cards) return undefined;
  return cards.some(
    (card) => card.id === value.cardId && card.accountId === value.accountId,
  )
    ? undefined
    : "The selected card no longer belongs to this account. Choose a card again.";
}

/** Shared options and membership validation for transaction forms and future filters. */
export function useAccountCardSelection({
  accountId,
  cardId,
}: AccountCardSelection) {
  const queryClient = useQueryClient();
  const [historical] = useState(() => ({ accountId, cardId }));
  // Fetch every page and retain an inactive current account for historical edits.
  const {
    data: accountData,
    isLoading: accountsLoading,
    isError: accountsError,
    refetch: retryAccounts,
  } = useAllAccounts();
  const {
    data: cardData,
    isLoading: cardsLoading,
    isError: cardsError,
    refetch: retryCards,
  } = useAccountCards(accountId || null);
  const allCards = useAllCards(undefined, { enabled: !accountId });
  const availableCards = accountId ? cardData : allCards.data;
  const availableCardsLoading = accountId ? cardsLoading : allCards.isLoading;
  const availableCardsError = accountId ? cardsError : allCards.isError;
  const retryAvailableCards = accountId ? retryCards : allCards.refetch;
  const accountOptions = useMemo(
    () =>
      (accountData ?? [])
        .filter(
          (account) => account.isActive || account.id === historical.accountId,
        )
        .map(accountToOption),
    [accountData, historical.accountId],
  );
  const cardOptions = useMemo(
    () =>
      (availableCards ?? [])
        .filter(
          (card) =>
            (!accountId || card.accountId === accountId) &&
            !!card.accountId &&
            (card.isActive || card.id === historical.cardId) &&
            !!accountData?.some(
              (account) =>
                account.id === card.accountId &&
                (account.isActive || account.id === historical.accountId),
            ),
        )
        .map((card) => ({
          ...cardToOption(card),
          label: card.isActive ? card.name : `${card.name} (inactive)`,
          sublabel: `${card.cardCode} / ${accountData?.find((account) => account.id === card.accountId)?.name ?? card.accountId}`,
          accountId: card.accountId!,
        })),
    [
      availableCards,
      accountData,
      accountId,
      historical.cardId,
      historical.accountId,
    ],
  );
  const selectionError = membershipError({ accountId, cardId }, cardData);
  // Read the latest successful result at submission, including a response that arrived
  // while async form validation was running. Unavailable data cannot disprove a pair.
  const validate = useCallback(
    (value: AccountCardSelection) => {
      const state = queryClient.getQueryState<Card[]>([
        "cards",
        "byAccount",
        value.accountId,
      ]);
      return membershipError(value, state?.data);
    },
    [queryClient],
  );
  return useMemo(
    () => ({
      value: { accountId, cardId },
      accountOptions,
      cardOptions,
      accountsLoading,
      accountsError,
      retryAccounts,
      cardsLoading: availableCardsLoading,
      cardsError: availableCardsError,
      retryCards: retryAvailableCards,
      selectionError,
      validate,
    }),
    [
      accountId,
      cardId,
      accountOptions,
      cardOptions,
      accountsLoading,
      accountsError,
      retryAccounts,
      availableCardsLoading,
      availableCardsError,
      retryAvailableCards,
      selectionError,
      validate,
    ],
  );
}
