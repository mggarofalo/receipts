import { test, expect, type Page, type Route } from "@playwright/test";
import {
  installApiMocks,
  signInAs,
  ADMIN_FIXTURE_USER,
  FIXTURE_USER,
  type FixtureUser,
} from "../fixtures/api-mocks";

// Behavioural coverage for the card-merge flow (/cards → "Merge (n)" → dialog).
//
// Merge is the app's only destructive bulk operation: it repoints cards and
// their transactions onto a target account and then DELETES the emptied source
// accounts. So the thing that matters most is not the happy path — it's that a
// merge which did NOT happen is never reported as one.
//
// The failure modes below are all real responses from POST /api/cards/merge:
//
//   403  RequireAdmin rejects a non-admin. Empty body.
//   404  Target account or a source card no longer exists (stale list). Empty body.
//   400  `TypedResults.BadRequest("...")` — a bare JSON *string*, not
//        ProblemDetails. Raised for partial source-account merges, unknown
//        YNAB winner, and fewer than two cards.
//   409  YNAB mapping conflict; the dialog resolves this inline.
//
// Empty-body and bare-string bodies are exactly the two shapes openapi-fetch
// does not surface as a ProblemDetails object, so they are the two that can slip
// past a naive `if (error)` check or a global handler that keys off `status`.

const ACCOUNTS = [
  { id: "acc-target", name: "Target Account", isActive: true },
  { id: "acc-source", name: "Source Account", isActive: true },
];

const CARDS = [
  { id: "card-1", cardCode: "1111", name: "Primary Visa", isActive: true, accountId: "acc-target" },
  { id: "card-2", cardCode: "2222", name: "Reissued Visa", isActive: true, accountId: "acc-source" },
];

const json = (body: unknown, status = 200) => ({
  status,
  contentType: "application/json",
  body: JSON.stringify(body),
});

/**
 * Boot /cards as an admin with two selectable cards, and stub the merge
 * endpoint with whatever the test needs.
 *
 * `mergeHandler` receives the merge POST. Returning a response with NO body at
 * all is deliberate in several tests — that is what ASP.NET sends for a bare
 * 403/404, and reproducing it faithfully is the whole point.
 */
async function gotoCards(
  page: Page,
  mergeHandler: (route: Route) => unknown,
  user: FixtureUser = ADMIN_FIXTURE_USER,
) {
  await installApiMocks(page, {
    user,
    // Real timers: we assert on toasts, which sonner mounts and dismisses on
    // setTimeout. A frozen clock makes that behaviour unrepresentative.
    freezeClock: false,
    overrides: {
      // `?*` keeps this disjoint from the merge route (which has no query string).
      "**/api/cards?*": (route) =>
        route.fulfill(json({ data: CARDS, total: CARDS.length, offset: 0, limit: 50 })),
      "**/api/accounts?*": (route) =>
        route.fulfill(json({ data: ACCOUNTS, total: ACCOUNTS.length, offset: 0, limit: 500 })),
      "**/api/cards/merge": mergeHandler,
    },
  });
  await signInAs(page, user);
  await page.goto("/cards");
  await expect(page.getByRole("heading", { name: "Cards" })).toBeVisible();
}

/** Select both cards, open the dialog, choose the target account, submit. */
async function submitMerge(page: Page) {
  await page.getByLabel("Select Primary Visa").check();
  await page.getByLabel("Select Reissued Visa").check();

  await page.getByLabel("Merge selected cards into an account").click();
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByText("Merge cards into account")).toBeVisible();

  await dialog.getByLabel("Target account").click();
  await page.getByRole("option", { name: "Target Account" }).click();
  await dialog.getByRole("button", { name: "Merge", exact: true }).click();

  return dialog;
}

test.describe("card merge — failed merges must not report success", () => {
  test("a 403 from RequireAdmin is surfaced as an error, not 'Cards merged'", async ({ page }) => {
    // ASP.NET's authorization failure: status only, zero-length body.
    await gotoCards(page, (route) => route.fulfill({ status: 403 }));

    const dialog = await submitMerge(page);

    await expect(page.getByText("You do not have permission to perform this action.")).toBeVisible();
    await expect(page.getByText("Cards merged")).toBeHidden();
    await expect(dialog).toBeVisible("a rejected merge must leave the dialog open to retry");
  });

  test("a 404 for a stale card or account is surfaced as an error", async ({ page }) => {
    await gotoCards(page, (route) => route.fulfill({ status: 404 }));

    const dialog = await submitMerge(page);

    await expect(page.getByText("The requested resource was not found.")).toBeVisible();
    await expect(page.getByText("Cards merged")).toBeHidden();
    await expect(dialog).toBeVisible();
  });

  test("a 400 with a bare-string body shows the server's reason", async ({ page }) => {
    // The most confusing rejection in practice: you selected two cards, but a
    // source account has a third card you did not select, so the merge would
    // orphan it. The server explains exactly that — the user must see it.
    const reason =
      "Source account would be partially merged: all of its cards must be included in the merge, or none.";
    await gotoCards(page, (route) =>
      route.fulfill({
        status: 400,
        contentType: "application/json",
        // `TypedResults.BadRequest(string)` serialises to a bare JSON string.
        body: JSON.stringify(reason),
      }),
    );

    const dialog = await submitMerge(page);

    await expect(page.getByText(reason)).toBeVisible();
    await expect(page.getByText("Cards merged")).toBeHidden();
    await expect(dialog).toBeVisible();
  });
});

test.describe("card merge — successful and recoverable paths", () => {
  test("a 200 merges, closes the dialog, and clears the selection", async ({ page }) => {
    await gotoCards(page, (route) => route.fulfill(json({ success: true })));

    await submitMerge(page);

    await expect(page.getByText("Cards merged")).toBeVisible();
    await expect(page.getByRole("dialog")).toBeHidden();
    // handleMergeComplete resets the selection, so the button falls back to 0.
    await expect(page.getByLabel("Merge selected cards into an account")).toHaveText("Merge (0)");
  });

  test("a 409 YNAB conflict is resolved inline and resubmitted with a winner", async ({ page }) => {
    const mergeBodies: unknown[] = [];
    await gotoCards(page, async (route) => {
      mergeBodies.push(route.request().postDataJSON());
      if (mergeBodies.length === 1) {
        return route.fulfill(
          json(
            {
              message: "Source cards have differing YNAB mappings.",
              conflicts: [
                {
                  accountId: "acc-target",
                  accountName: "Target Account",
                  ynabBudgetId: "b1",
                  ynabAccountId: "y1",
                  ynabAccountName: "YNAB Target",
                },
                {
                  accountId: "acc-source",
                  accountName: "Source Account",
                  ynabBudgetId: "b1",
                  ynabAccountId: "y2",
                  ynabAccountName: "YNAB Source",
                },
              ],
            },
            409,
          ),
        );
      }
      return route.fulfill(json({ success: true }));
    });

    const dialog = await submitMerge(page);

    // A conflict is a prompt, not a failure: no error toast, and the dialog
    // stays open asking which mapping survives.
    await expect(dialog.getByText("YNAB mapping conflict")).toBeVisible();
    const resubmit = dialog.getByRole("button", { name: /resubmit/i });
    await expect(resubmit).toBeDisabled();

    await dialog.getByRole("radio", { name: /Source Account/ }).check();
    await expect(resubmit).toBeEnabled();
    await resubmit.click();

    await expect(page.getByText("Cards merged")).toBeVisible();
    await expect(page.getByRole("dialog")).toBeHidden();

    expect(mergeBodies).toHaveLength(2);
    expect(mergeBodies[0]).toMatchObject({ ynabMappingWinnerAccountId: null });
    expect(mergeBodies[1]).toMatchObject({
      targetAccountId: "acc-target",
      sourceCardIds: ["card-1", "card-2"],
      ynabMappingWinnerAccountId: "acc-source",
    });
  });
});

test.describe("card merge — entry conditions", () => {
  test("the merge button is disabled until at least two cards are selected", async ({ page }) => {
    await gotoCards(page, (route) => route.fulfill(json({ success: true })));

    const mergeButton = page.getByLabel("Merge selected cards into an account");
    await expect(mergeButton).toBeDisabled();

    // One card is not enough — the API rejects fewer than two source cards, so
    // the UI must not offer the action. This is also the reason a single-card
    // account cannot currently be merged into another account at all.
    await page.getByLabel("Select Primary Visa").check();
    await expect(mergeButton).toHaveText("Merge (1)");
    await expect(mergeButton).toBeDisabled();

    await page.getByLabel("Select Reissued Visa").check();
    await expect(mergeButton).toBeEnabled();
  });

  // RECEIPTS-895. The 403 test above proves a rejected merge is reported
  // honestly; this proves a non-admin never gets far enough to be rejected.
  // That matters because the dialog's "New account" mode creates the target
  // account BEFORE submitting, so reaching it and failing leaves a stray
  // empty account behind.
  test("a non-admin is never offered the merge dialog", async ({ page }) => {
    let mergeRequests = 0;
    await gotoCards(
      page,
      (route) => {
        mergeRequests++;
        return route.fulfill({ status: 403 });
      },
      FIXTURE_USER,
    );

    const mergeButton = page.getByLabel("Merge selected cards into an account");

    // Select enough cards that an admin would be able to merge.
    await page.getByLabel("Select Primary Visa").check();
    await page.getByLabel("Select Reissued Visa").check();
    await expect(mergeButton).toHaveText("Merge (2)");

    await expect(mergeButton).toBeDisabled();
    await expect(mergeButton).toHaveAccessibleName(
      "Merge selected cards into an account — requires an administrator account",
    );

    await expect(page.getByRole("dialog")).toBeHidden();
    // The gate is real, not just visual: nothing ever hit the endpoint.
    expect(mergeRequests).toBe(0);
  });
});
