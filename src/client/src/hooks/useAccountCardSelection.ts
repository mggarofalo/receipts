import { useCallback, useMemo } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useAllAccounts, useAccountCards } from "@/hooks/useAccounts";
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
  const accountOptions = useMemo(
    () =>
      (accountData ?? [])
        .filter((account) => account.isActive || account.id === accountId)
        .map(accountToOption),
    [accountData, accountId],
  );
  const cardOptions = useMemo(
    () =>
      (cardData ?? [])
        .filter(
          (card) =>
            card.accountId === accountId &&
            (card.isActive || card.id === cardId),
        )
        .map((card) => ({
          ...cardToOption(card),
          label: card.isActive ? card.name : `${card.name} (inactive)`,
        })),
    [cardData, accountId, cardId],
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
      cardsLoading,
      cardsError,
      retryCards,
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
      cardsLoading,
      cardsError,
      retryCards,
      selectionError,
      validate,
    ],
  );
}
