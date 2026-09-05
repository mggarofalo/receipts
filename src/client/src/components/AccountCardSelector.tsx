import { useId, type Ref } from "react";
import { Link } from "react-router";
import type {
  AccountCardSelection,
  useAccountCardSelection,
} from "@/hooks/useAccountCardSelection";
import { Combobox } from "@/components/ui/combobox";
import { Label } from "@/components/ui/label";
import { Button } from "@/components/ui/button";

interface InputState {
  ref?: Ref<HTMLButtonElement>;
  onBlur?: () => void;
  error?: string;
}
interface AccountCardSelectorProps {
  selection: ReturnType<typeof useAccountCardSelection>;
  onChange: (value: AccountCardSelection) => void;
  accountInput?: InputState;
  cardInput?: InputState;
  fieldClassName?: string;
}

/** Account drives card selection; independent of a particular form library. */
export function AccountCardSelector({
  selection,
  onChange,
  accountInput,
  cardInput,
  fieldClassName,
}: AccountCardSelectorProps) {
  const id = useId();
  const {
    value,
    accountOptions,
    cardOptions,
    accountsLoading,
    accountsError,
    retryAccounts,
    cardsLoading,
    cardsError,
    retryCards,
    selectionError,
  } = selection;
  const cardError = cardInput?.error ?? selectionError;
  const noCards =
    !!value.accountId &&
    !cardsLoading &&
    !cardsError &&
    cardOptions.length === 0;

  return (
    <>
      <div className={`grid gap-2 ${fieldClassName ?? ""}`}>
        <Label htmlFor={`${id}-account`}>
          Account
          <span aria-hidden="true" className="text-destructive">
            *
          </span>
        </Label>
        <Combobox
          id={`${id}-account`}
          ref={accountInput?.ref}
          onBlur={accountInput?.onBlur}
          options={accountOptions}
          value={value.accountId}
          onValueChange={(accountId) =>
            onChange({
              accountId,
              cardId: accountId === value.accountId ? value.cardId : "",
            })
          }
          placeholder="Select an account..."
          searchPlaceholder="Search accounts..."
          loading={accountsLoading && !value.accountId}
          disabled={accountsLoading || accountsError}
          aria-required="true"
          aria-invalid={!!accountInput?.error}
          aria-describedby={`${id}-account-error`}
        />
        {accountsError && (
          <p role="alert" className="text-sm text-destructive">
            Could not load accounts.{" "}
            <Button
              type="button"
              variant="link"
              onClick={() => void retryAccounts()}
            >
              Retry accounts
            </Button>
          </p>
        )}
        <p
          id={`${id}-account-error`}
          role={accountInput?.error ? "alert" : undefined}
          aria-hidden={!accountInput?.error || undefined}
          className="min-h-5 text-sm font-medium text-destructive"
        >
          {accountInput?.error}
        </p>
      </div>
      <div className={`grid gap-2 ${fieldClassName ?? ""}`}>
        <Label htmlFor={`${id}-card`}>
          Card
          <span aria-hidden="true" className="text-destructive">
            *
          </span>
        </Label>
        <Combobox
          key={value.accountId}
          id={`${id}-card`}
          ref={cardInput?.ref}
          onBlur={cardInput?.onBlur}
          options={cardOptions}
          value={value.cardId}
          onValueChange={(cardId) => {
            // Reject stale options if the account or its available cards changed.
            if (cardOptions.some((card) => card.value === cardId))
              onChange({ accountId: value.accountId, cardId });
          }}
          placeholder={
            value.accountId ? "Select a card..." : "Select an account first"
          }
          searchPlaceholder="Search cards..."
          loading={cardsLoading && !value.cardId}
          disabled={!value.accountId || cardsLoading || cardsError || noCards}
          aria-required="true"
          aria-invalid={!!cardError}
          aria-describedby={`${id}-card-error ${id}-card-status`}
        />
        <div id={`${id}-card-status`} className="text-sm">
          {cardsError && (
            <p role="alert" className="text-destructive">
              Could not load this account’s cards.{" "}
              <Button
                type="button"
                variant="link"
                onClick={() => void retryCards()}
              >
                Retry cards
              </Button>
            </p>
          )}
          {noCards && (
            <p role="status" className="text-muted-foreground">
              This account has no active cards.{" "}
              <Link className="underline" to="/cards" state={{ openNew: true }}>
                Create a card
              </Link>{" "}
              or choose another account.
            </p>
          )}
        </div>
        <p
          id={`${id}-card-error`}
          role={cardError ? "alert" : undefined}
          aria-hidden={!cardError || undefined}
          className="min-h-5 text-sm font-medium text-destructive"
        >
          {cardError}
        </p>
      </div>
    </>
  );
}
