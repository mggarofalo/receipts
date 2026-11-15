import { expect, test } from "@playwright/test";
import { installApiMocks, signInAsFixtureUser } from "../fixtures/api-mocks";

for (const width of [1280, 375]) {
  test(`split preview keeps the recorded push and separates views at ${width}px`, async ({
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
          const query = new URL(route.request().url()).search;
          queries.push(query);
          if (query)
            return route.fulfill({
              status: 400,
              json: { detail: "Preview unavailable" },
            });
          return route.fulfill({
            json: {
              categoryView: "asClassified",
              capturedCurrentRevisionId: null,
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

    const view = page.getByRole("combobox", { name: "Expected split view" });
    await expect(view).toHaveValue("asClassified");
    await expect(page.getByText("Recorded category")).toBeVisible();
    expect(queries).toContain("");
    const push = page.getByRole("button", { name: /push to ynab/i });
    await expect(push).toBeVisible();

    await view.selectOption("currentHierarchy");
    await expect(
      page.getByText(/Could not load split comparison/),
    ).toBeVisible();
    await expect(page.getByText("Recorded category")).toHaveCount(0);
    expect(queries).toContain("?categoryView=currentHierarchy");
    await expect(push).toBeVisible();
    const bounds = await view.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.x).toBeGreaterThanOrEqual(0);
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width);

    await view.selectOption("asClassified");
    await expect(page.getByText("Recorded category")).toBeVisible();
  });
}
