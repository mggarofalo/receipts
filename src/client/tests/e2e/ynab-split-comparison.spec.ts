import { expect, test } from "@playwright/test";
import { installApiMocks, signInAsFixtureUser } from "../fixtures/api-mocks";

for (const width of [1280, 375]) {
  test(`split comparison shows the recorded split without a preview selector at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 900 });
    const queries: string[] = [];
    await installApiMocks(page, {
      ynabConfigured: true,
      freezeClock: false,
      overrides: {
        "**/api/metadata/enums": (route) =>
          route.fulfill({
            json: {
              adjustmentTypes: [],
              authEventTypes: [],
              auditActions: [],
              entityTypes: [],
            },
          }),
        "**/api/trips?*": (route) =>
          route.fulfill({
            json: {
              receipt: {
                receipt: {
                  id: "r1",
                  location: "Market",
                  date: "2026-10-26",
                  taxAmount: 0,
                },
                subtotal: 11,
                adjustmentTotal: 0,
                expectedTotal: 11,
                items: [],
                adjustments: [],
                warnings: [],
              },
              transactions: [],
              warnings: [],
            },
          }),
        "**/api/ynab/connection-status": (route) =>
          route.fulfill({ json: { isConfigured: true, isConnected: true } }),
        "**/api/ynab/receipt-sync-statuses**": (route) =>
          route.fulfill({ json: { data: [] } }),
        "**/api/ynab/settings/budget": (route) =>
          route.fulfill({ json: { selectedBudgetId: "budget" } }),
        "**/api/ynab/receipts/r1/split-comparison**": (route) => {
          queries.push(new URL(route.request().url()).search);
          return route.fulfill({
            json: {
              canComputeExpected: true,
              unmappedCategories: [],
              transactionComparisons: [
                {
                  localTransactionId: "t1",
                  accountName: "Checking",
                  totalMilliunits: -11000,
                  expected: [
                    {
                      ynabCategoryId: "cat",
                      categoryName: "Recorded category",
                      milliunits: -11000,
                    },
                  ],
                  actual: null,
                  actualFetchError: null,
                  matches: null,
                },
              ],
            },
          });
        },
      },
    });
    await signInAsFixtureUser(page);
    await page.goto("/receipts/r1", { waitUntil: "domcontentloaded" });

    await expect(page.getByText("Recorded category")).toBeVisible();
    await expect(
      page.getByRole("combobox", { name: "Expected split view" }),
    ).toHaveCount(0);
    await expect(page.getByText(/current hierarchy/i)).toHaveCount(0);
    expect(queries.every((query) => query === "")).toBe(true);
    await expect(
      page.getByRole("button", { name: /push to ynab/i }),
    ).toBeVisible();
  });
}
