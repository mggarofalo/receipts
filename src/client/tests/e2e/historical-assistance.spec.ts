import { expect, test } from "@playwright/test";
import { installApiMocks, signInAs } from "../fixtures/api-mocks";

const group = {
  group: { store: "Market", rawDescription: "MILK 2% GAL", itemCode: null },
  purchaseCount: 12,
  impactAmount: 10,
  impactCurrency: "USD",
  conflict: false,
  migratedBroad: true,
  unresolved: false,
  unassigned: false,
  currentCategories: [],
  disposition: null,
  dispositionVersion: 0,
};
const requestId = "aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa";
const attemptId = "bbbbbbbb-bbbb-4bbb-bbbb-bbbbbbbbbbbb";
const progress = {
  operationId: requestId,
  createdAt: "2026-10-26T00:00:00Z",
  groupCount: 1,
  itemCount: 12,
  state: "ready",
  groups: [
    {
      group: group.group,
      itemCount: 12,
      attemptIds: [attemptId],
      states: ["failed", "suggested"],
      failureReasons: ["PayloadTooLarge", null],
    },
  ],
};

for (const width of [375, 1280]) {
  test(`historical assistance survives failure and reload at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 900 });
    let capability = "ready";
    let posts = 0;
    let originalBody: unknown;
    await installApiMocks(page, {
      freezeClock: false,
      overrides: {
        "**/api/historical-reclassifications/groups?*": (route) =>
          route.fulfill({
            json: { items: [group], totalGroups: 1, nextOffset: null },
          }),
        "**/api/historical-reclassifications?*": (route) =>
          route.fulfill({ json: { items: [], nextOffset: null } }),
        "**/api/category-assistance/status": (route) =>
          route.fulfill({
            json: {
              capability,
              consentEnabled: capability === "ready",
              suggestionsAvailable: capability === "ready",
              provider: "test",
              models: ["test"],
              dailyLimit: 20,
              pendingLimit: 5,
            },
          }),
        "**/api/category-assistance/historical-requests?*": (route) =>
          route.fulfill({
            json: {
              total: posts > 1 ? 1 : 0,
              offset: 0,
              limit: 25,
              items: posts > 1 ? [progress] : [],
            },
          }),
        "**/api/category-assistance/historical-requests/*": (route) =>
          route.fulfill({ json: progress }),
        "**/api/category-assistance/historical-requests": (route) => {
          posts++;
          const body = route.request().postDataJSON();
          if (posts === 1) {
            originalBody = body;
            return route.fulfill({
              status: 503,
              json: { detail: "Connection lost" },
            });
          }
          expect(body).toEqual(originalBody);
          return route.fulfill({ json: progress });
        },
      },
    });
    await signInAs(page);
    await page.goto("/categories/cleanup");
    await expect(
      page.getByRole("region", { name: "Historical suggestions" }),
    ).toBeVisible();
    await page
      .getByRole("checkbox", { name: "Select MILK 2% GAL at Market" })
      .check();
    await expect(
      page.getByText(
        /This request includes 1 selected visible groups and 12 purchases/,
      ),
    ).toBeVisible();
    await page.getByRole("button", { name: "Request suggestions" }).click();
    await expect(
      page.getByText(/Check recent requests before retrying/),
    ).toBeVisible();
    await page.reload();
    await expect(
      page.getByRole("button", { name: "Retry request suggestions" }),
    ).toBeVisible();
    await page
      .getByRole("button", { name: "Retry request suggestions" })
      .click();
    await expect(
      page.getByRole("link", { name: `Review suggestion ${attemptId}` }),
    ).toHaveAttribute("href", `/classification?attempt=${attemptId}`);
    await expect(
      page.getByText(/Purchase 1: The category hierarchy is too large/),
    ).toBeVisible();
    await expect(page.getByText(/Purchase 2:/)).toHaveCount(0);
    await page.screenshot({
      path: `C:/Users/mggar/Source/receipts-017-artifacts/1057/historical-failure-${width}.png`,
      fullPage: true,
    });
    await page.reload();
    await page
      .getByRole("button", { name: new RegExp(`ready.*${requestId}`) })
      .click();
    await expect(
      page.getByRole("link", { name: `Review suggestion ${attemptId}` }),
    ).toBeVisible();
    expect(posts).toBe(2);
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth),
    ).toBeLessThanOrEqual(width);

    for (const unavailable of ["unknown", "disabled", "unconfigured"]) {
      capability = unavailable;
      await page.reload();
      await expect(
        page.getByRole("region", { name: "Historical suggestions" }),
      ).toHaveCount(0);
      await expect(page.getByText("MILK 2% GAL")).toBeVisible();
    }
  });
}
