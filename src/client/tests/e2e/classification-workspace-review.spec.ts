import { expect, test } from "@playwright/test";
import {
  ADMIN_FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";
import { taxonomyFixture } from "../../src/test/taxonomy-fixture";

const attemptId = "11111111-1111-4111-8111-111111111111";
const itemId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const output = "C:/Users/mggar/Source/receipts-017-artifacts/1058";

for (const width of [1280, 375]) {
  test(`classification review browser ${width}`, async ({ page }) => {
    await page.setViewportSize({ width, height: width === 375 ? 812 : 900 });
    const accepted: unknown[] = [];
    const reviewWrites: Array<{ operationId: string; expectedRevision: string; state: string; excludedItemIds: string[] }> = [];
    let attemptAccepted = false;
    let reviewState = "active";
    let reviewRevision = "00000000-0000-0000-0000-000000000000";
    let excludedItemIds: string[] = [];
    await installApiMocks(page, {
      user: ADMIN_FIXTURE_USER,
      freezeClock: false,
      overrides: {
        "**/api/category-assistance/status": (route) =>
          route.fulfill({
            json: {
              capability: "ready",
              consentEnabled: true,
              suggestionsAvailable: true,
              provider: "jev",
              models: ["jev-1.14.0"],
            },
          }),
        "**/api/category-assistance/suggestions?*": (route) => {
          const filter = new URL(route.request().url()).searchParams.get("review") ?? "active";
          const visible = !attemptAccepted && (filter === "all" || filter === reviewState);
          return route.fulfill({
            json: {
              total: visible ? 1 : 0,
              offset: 0,
              limit: 25,
              items: visible
                ? [
                    {
                      attemptId,
                      source: { location: "Store", description: "Milk" },
                      originalDescription: "Whole Milk",
                      affectedCount: 1,
                      proposedPath: [taxonomyFixture.nodes[0].id],
                      abstentionReason: null,
                      confidence: 0.9,
                      state: "suggested",
                      reviewState,
                      reviewRevision,
                      excludedItemCount: excludedItemIds.length,
                      isCurrent: true,
                      createdAt: "2026-10-25T12:00:00Z",
                      completedAt: "2026-10-25T12:00:01Z",
                    },
                  ] : [],
            },
          });
        },
        "**/api/category-assistance/suggestions/*/review-preference": (route) => {
          if (route.request().method() === "PUT") {
            const body = route.request().postDataJSON() as { operationId: string; expectedRevision: string; state: string; excludedItemIds: string[] };
            reviewWrites.push(body);
            if (body.expectedRevision !== reviewRevision) return route.fulfill({ status: 409, json: { detail: "Revision changed" } });
            reviewState = body.state;
            excludedItemIds = body.excludedItemIds;
            reviewRevision = "22222222-2222-4222-8222-222222222222";
          }
          return route.fulfill({ json: { attemptId, state: reviewState, revision: reviewRevision, excludedItemIds } });
        },
        "**/api/category-assistance/suggestions/*/items?*": (route) =>
          route.fulfill({
            json: {
              total: excludedItemIds.length ? 0 : 1,
              offset: 0,
              limit: 100,
              itemIds: excludedItemIds.length ? [] : [itemId],
              items: excludedItemIds.length ? [] : [
                {
                  id: itemId,
                  receiptId: "receipt-a",
                  date: "2026-10-25",
                  description: "Whole Milk",
                  store: "Store",
                  quantity: 1,
                  totalAmount: 4.5,
                  currency: "USD",
                },
              ],
            },
          }),
        "**/api/category-assistance/suggestions/*/accept": async (route) => {
          accepted.push(route.request().postDataJSON());
          attemptAccepted = true;
          await route.fulfill({
            json: {
              attemptId,
              decision: "accepted",
              affectedItemIds: [itemId],
              categoryId: taxonomyFixture.nodes[0].id,
            },
          });
        },
        "**/api/taxonomy": (route) => route.fulfill({ json: taxonomyFixture }),
      },
    });
    await signInAs(page, ADMIN_FIXTURE_USER);
    await page.goto("/classification", {
      waitUntil: "domcontentloaded",
      timeout: 30_000,
    });
    await expect(
      page.getByRole("heading", { name: "Classification" }),
    ).toBeVisible();
    await expect(page.getByText("Whole Milk")).toBeVisible();
    await page.getByRole("button", { name: "Review details" }).focus();
    await page.keyboard.press("Enter");
    await expect(
      page.getByRole("checkbox", { name: /Select Whole Milk/i }),
    ).toBeVisible();
    await expect(page.getByText(/Current choice: active/)).toBeVisible();
    await page.getByRole("checkbox", { name: /Select Whole Milk/i }).check();
    await page.getByRole("button", { name: "Exclude selected from assistance" }).click();
    await expect(page.getByRole("checkbox", { name: `Excluded item ${itemId}` })).toBeVisible();
    expect(reviewWrites.at(-1)).toMatchObject({ state: "active", excludedItemIds: [itemId] });
    await page.getByRole("checkbox", { name: `Excluded item ${itemId}` }).check();
    await page.getByRole("button", { name: "Include selected excluded items" }).click();
    await expect(page.getByRole("checkbox", { name: /Select Whole Milk/i })).toBeVisible();
    expect(reviewWrites.at(-1)).toMatchObject({ state: "active", excludedItemIds: [] });
    await page.getByRole("button", { name: "Later", exact: true }).last().click();
    await expect(page.getByRole("button", { name: "Review details" })).toHaveCount(0);
    await page.getByRole("navigation", { name: "Review state" }).getByRole("button", { name: "Later" }).click();
    await page.getByRole("button", { name: "Review details" }).click();
    await expect(page.getByText(/Current choice: later/)).toBeVisible();
    await page.getByRole("button", { name: "Reopen" }).click();
    await expect(page.getByRole("button", { name: "Review details" })).toHaveCount(0);
    await page.getByRole("navigation", { name: "Review state" }).getByRole("button", { name: "Active" }).click();
    await page.getByRole("button", { name: "Review details" }).click();
    await expect(page.getByText(/Current choice: active/)).toBeVisible();
    await page.screenshot({
      path: `${output}/workspace-browser-${width}.png`,
      fullPage: true,
    });
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    await page
      .getByRole("checkbox", { name: /Select Whole Milk/i })
      .focus();
    await page.keyboard.press("Space");
    await page.getByRole("button", { name: "Apply to selected" }).focus();
    await page.keyboard.press("Enter");
    await expect.poll(() => accepted.length).toBe(1);
    expect(accepted[0]).toMatchObject({
      itemIds: [itemId],
      categoryId: taxonomyFixture.nodes[0].id,
      expectedTaxonomyRevision: taxonomyFixture.id,
    });
    await expect(
      page.getByRole("region", {
        name: "Completed classifications awaiting future mapping",
      }),
    ).toBeVisible();
    await expect(
      page.getByRole("button", { name: "Review details" }),
    ).toHaveCount(0);
    await page.reload();
    await expect(
      page.getByRole("button", { name: "Remember category…" }),
    ).toBeVisible();
    await expect(page.getByRole("link", { name: "Clean up history" })).toHaveAttribute("href", "/categories/cleanup");
  });
}

test("unknown capability keeps manual browsing and admin settings reachable", async ({
  page,
}) => {
  let suggestionReads = 0;
  await installApiMocks(page, {
    user: ADMIN_FIXTURE_USER,
    freezeClock: false,
    overrides: {
      "**/api/category-assistance/status": (route) =>
        route.fulfill({
          json: {
            capability: "unknown",
            consentEnabled: false,
            suggestionsAvailable: false,
            provider: "jev",
            models: ["unverified-model"],
          },
        }),
      "**/api/category-assistance/suggestions?*": (route) => {
        suggestionReads++;
        return route.fulfill({
          json: { total: 0, offset: 0, limit: 25, items: [] },
        });
      },
      "**/api/category-assistance/settings": (route) =>
        route.fulfill({
          json: {
            enabled: false,
            provider: null,
            model: null,
            dailyLimit: 10,
            pendingLimit: 3,
            revision: "22222222-2222-4222-8222-222222222222",
          },
        }),
    },
  });
  await signInAs(page, ADMIN_FIXTURE_USER);
  await page.goto("/classification", {
    waitUntil: "domcontentloaded",
    timeout: 30_000,
  });
  await expect(page.getByText(/suggestions are unavailable/i)).toBeVisible();
  expect(suggestionReads).toBe(0);
  await expect(
    page.getByRole("link", { name: "Browse categories" }),
  ).toBeVisible();
  await page.getByRole("link", { name: "Assistance settings" }).click();
  await expect(
    page.getByRole("heading", { name: "Category Assistance Settings" }),
  ).toBeVisible();
  await expect(page.getByLabel("Provider", { exact: true })).toHaveCount(0);
  await expect(page.getByLabel("Model", { exact: true })).toHaveCount(0);
  await expect(
    page.getByRole("checkbox", { name: /enable automatic/i }),
  ).toBeDisabled();
  await page.screenshot({
    path: `${output}/workspace-admin-unknown.png`,
    fullPage: true,
  });
});
