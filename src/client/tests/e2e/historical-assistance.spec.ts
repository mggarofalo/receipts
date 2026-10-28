import { expect, test, type Page } from "@playwright/test";
import { installApiMocks, signInAs } from "../fixtures/api-mocks";

const pollingEvidence = new WeakMap<Page, Array<Record<string, unknown>>>();
test.beforeEach(async ({ page }) => {
  const evidence: Array<Record<string, unknown>> = [];
  pollingEvidence.set(page, evidence);
  page.on("response", async (response) => {
    if (!response.url().includes("category-assistance")) return;
    evidence.push({
      at: new Date().toISOString(),
      method: response.request().method(),
      url: response.url(),
      status: response.status(),
      body: await response.json().catch(() => null),
    });
  });
  page.on("requestfailed", (request) =>
    evidence.push({
      at: new Date().toISOString(),
      failed: request.url(),
      reason: request.failure(),
    }),
  );
  page.on("framenavigated", (frame) => {
    if (frame === page.mainFrame())
      evidence.push({ at: new Date().toISOString(), navigation: frame.url() });
  });
});
test.afterEach(async ({ page }, testInfo) => {
  const browser = await page
    .evaluate(() => ({
      visibility: document.visibilityState,
      focused: document.hasFocus(),
      statuses: [...document.querySelectorAll('[role="status"]')].map(
        (element) => element.textContent,
      ),
    }))
    .catch(() => null);
  await testInfo.attach("historical-polling-evidence", {
    body: JSON.stringify(
      { browser, events: pollingEvidence.get(page) },
      null,
      2,
    ),
    contentType: "application/json",
  });
});

const group = {
  group: { store: "Market", rawDescription: "MILK 2% GAL", itemCode: null },
  purchaseCount: 56,
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
const attemptId = "bbbbbbbb-bbbb-4bbb-bbbb-bbbbbbbbbbbb";
const report = (operationId: string, pending = false) => ({
  operationId,
  createdAt: "2026-10-26T00:00:00Z",
  groupCount: 1,
  itemCount: 56,
  state: pending ? "pending" : "failed",
  groups: [
    {
      group: group.group,
      itemCount: 56,
      attemptIds: pending ? [] : [attemptId, attemptId],
      states: Array(56).fill(pending ? "queued" : "failed"),
      failureReasons: pending ? [] : Array(56).fill("PayloadTooLarge"),
    },
  ],
});
for (const width of [375, 1280]) {
  test(`historical unknown receipt replays identical payload after reload (${width}px)`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 900 });
    let posts = 0;
    let original: Record<string, unknown>;
    let capability = "ready";
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
              consentEnabled: true,
              suggestionsAvailable: true,
              dailyLimit: 20,
              pendingLimit: 5,
            },
          }),
        "**/api/category-assistance/historical-requests?*": (route) =>
          route.fulfill({ json: { total: 0, items: [] } }),
        "**/api/category-assistance/historical-requests/*": (route) =>
          posts < 2
            ? route.fulfill({
                status: 404,
                json: { status: 404, detail: "Not found" },
              })
            : route.fulfill({ json: report(original.operationId as string) }),
        "**/api/category-assistance/historical-requests": (route) => {
          posts++;
          const body = route.request().postDataJSON();
          if (posts === 1) {
            original = body;
            return route.fulfill({
              status: 503,
              json: { detail: "Connection lost" },
            });
          }
          expect(body).toEqual(original);
          return route.fulfill({ json: report(body.operationId) });
        },
      },
    });
    await signInAs(page);
    await page.goto("/categories/cleanup");
    await page
      .getByRole("checkbox", { name: "Select MILK 2% GAL at Market" })
      .check();
    await page.getByRole("button", { name: "Request suggestions" }).click();
    await expect(
      page.getByText(/Receipt of this request is not confirmed/),
    ).toBeVisible();
    await page.reload();
    await page.getByRole("button", { name: "Retry safely" }).click();
    await expect(
      page.getByRole("status").filter({ hasText: "56 failed" }),
    ).toBeVisible();
    await page.getByText("View groups and review suggestions").click();
    await expect(
      page.getByRole("link", { name: "Review suggestion", exact: true }),
    ).toHaveAttribute("href", `/classification?attempt=${attemptId}`);
    expect(posts).toBe(2);
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth),
    ).toBeLessThanOrEqual(width);
    capability = "absent";
    await page.reload();
    await expect(
      page.getByRole("region", { name: "Historical suggestions" }),
    ).toHaveCount(0);
    await expect(page.getByText("MILK 2% GAL", { exact: true })).toBeVisible();
  });

  test(`409 releases selection and progress polls compactly (${width}px)`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 900 });
    const groups = [
      group,
      ...Array.from({ length: 21 }, (_, i) => ({
        ...group,
        purchaseCount: 1,
        group: { ...group.group, rawDescription: `ITEM ${i}` },
      })),
    ];
    const bodies: Array<{ operationId: string; groups: unknown[] }> = [];
    let details = 0;
    let recents = 0;
    await installApiMocks(page, {
      freezeClock: false,
      overrides: {
        "**/api/historical-reclassifications/groups?*": (route) =>
          route.fulfill({
            json: { items: groups, totalGroups: 22, nextOffset: null },
          }),
        "**/api/historical-reclassifications?*": (route) =>
          route.fulfill({ json: { items: [], nextOffset: null } }),
        "**/api/category-assistance/status": (route) =>
          route.fulfill({
            json: {
              capability: "ready",
              consentEnabled: true,
              suggestionsAvailable: true,
              dailyLimit: 20,
              pendingLimit: 5,
            },
          }),
        "**/api/category-assistance/historical-requests?*": (route) => {
          recents++;
          return route.fulfill({ json: { total: 0, items: [] } });
        },
        "**/api/category-assistance/historical-requests/*": (route) => {
          details++;
          return route.fulfill({
            json: report(bodies[1].operationId, details === 1),
          });
        },
        "**/api/category-assistance/historical-requests": (route) => {
          bodies.push(route.request().postDataJSON());
          return bodies.length === 1
            ? route.fulfill({
                status: 409,
                json: {
                  status: 409,
                  detail:
                    "Historical request exceeds available capacity of 20 purchases.",
                },
              })
            : route.fulfill({ json: report(bodies[1].operationId, true) });
        },
      },
    });
    await signInAs(page);
    await page.goto("/categories/cleanup");
    await page.getByLabel("Select visible page").check();
    await page.getByRole("button", { name: "Request suggestions" }).click();
    await expect(
      page.getByRole("alert").filter({ hasText: /20 purchases/ }),
    ).toBeVisible();
    await expect(
      page.getByRole("button", { name: "Retry safely" }),
    ).toHaveCount(0);
    await page.getByLabel("Select visible page").uncheck();
    await page
      .getByRole("checkbox", { name: "Select MILK 2% GAL at Market" })
      .check();
    await page.getByRole("button", { name: "Request suggestions" }).click();
    await expect(
      page.getByRole("status").filter({ hasText: "56 in progress" }),
    ).toBeVisible();
    expect(bodies[0].groups).toHaveLength(22);
    expect(bodies[1].groups).toHaveLength(1);
    expect(bodies[1].operationId).not.toBe(bodies[0].operationId);
    await expect(
      page.getByRole("status").filter({ hasText: "56 failed" }),
    ).toBeVisible({ timeout: 10_000 });
    const progress = page.getByLabel("Suggestion request progress");
    await expect(progress.getByRole("alert")).toHaveCount(1);
    await expect(progress.getByRole("status")).toHaveText("56 failed");
    await page.getByText("View groups and review suggestions").click();
    await expect(
      progress.getByRole("link", { name: "Review suggestion", exact: true }),
    ).toHaveCount(1);
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth),
    ).toBeLessThanOrEqual(width);
    await page.screenshot({
      path: `../../receipts-017-artifacts/1074/historical-${width}.png`,
      fullPage: true,
    });
    const recentBefore = recents;
    await expect
      .poll(() => recents, { timeout: 17_000 })
      .toBeGreaterThan(recentBefore);
    expect(bodies).toHaveLength(2);
  });
}
