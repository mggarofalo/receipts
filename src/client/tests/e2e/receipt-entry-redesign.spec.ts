import { taxonomyFixture } from "../../src/test/taxonomy-fixture";
import { expect, test } from "@playwright/test";
import {
  ADMIN_FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";

const productId = "11111111-1111-4111-8111-111111111111";
const revision = "22222222-2222-4222-8222-222222222222";
const cards = [
  {
    id: "card-a",
    name: "Debit",
    cardCode: "1234",
    accountId: "account-a",
    isActive: true,
  },
  {
    id: "card-b",
    name: "Debit",
    cardCode: "1234",
    accountId: "account-b",
    isActive: true,
  },
];
const accounts = [
  { id: "account-a", accountCode: "1000", name: "Checking", isActive: true },
  { id: "account-b", accountCode: "2000", name: "Savings", isActive: true },
];

for (const width of [1280, 900, 375, 320]) {
  for (const known of [true, false]) {
    test(`${known ? "inline product" : "unknown item"} saves once with split payments and adjustment at ${width}px`, async ({
      page,
    }, testInfo) => {
      await page.setViewportSize({ width, height: 900 });
      const bodies: Record<string, unknown>[] = [];
      await installApiMocks(page, {
        freezeClock: false,
        user: ADMIN_FIXTURE_USER,
        overrides: {
          "**/api/metadata/enums": (route) =>
            route.fulfill({
              json: {
                adjustmentTypes: [{ value: "discount", label: "Discount" }],
                authEventTypes: [],
                auditActions: [],
                entityTypes: [],
              },
            }),
          "**/api/receipts/locations*": (route) =>
            route.fulfill({ json: { locations: ["Market"] } }),
          "**/api/accounts**": (route) =>
            route.fulfill({
              json: { data: accounts, total: 2, offset: 0, limit: 50 },
            }),
          "**/api/accounts/*/cards": (route) =>
            route.fulfill({
              json: cards.filter((card) =>
                route.request().url().includes(card.accountId),
              ),
            }),
          "**/api/cards**": (route) =>
            route.fulfill({
              json: { data: cards, total: 2, offset: 0, limit: 50 },
            }),
          "**/api/taxonomy": (route) =>
            route.fulfill({ json: taxonomyFixture }),
          "**/api/receipt-items/similar*": (route) =>
            route.fulfill({ json: [] }),
          "**/api/item-templates/similar*": (route) =>
            route.fulfill({ json: [] }),
          "**/api/receipt-items/suggestions*": (route) =>
            route.fulfill({ json: [] }),
          "**/api/receipt-items/normalization-candidates*": (route) =>
            route.fulfill({
              json: known
                ? [
                    {
                      id: productId,
                      revision,
                      canonicalName: "Whole milk 1 gallon",
                      displayLabel: null,
                      channels: ["lexical"],
                      score: 0.95,
                    },
                  ]
                : [],
            }),
          "**/api/receipt-items/normalization-preview": (route) =>
            route.fulfill({
              json: {
                sourceFingerprint: "milk-source",
                sourceAmbiguous: false,
                excluded: false,
                candidates: [],
                total: 0,
                offset: 0,
                limit: 10,
              },
            }),
          "**/api/receipts/complete": (route) => {
            bodies.push(route.request().postDataJSON());
            return route.fulfill({
              status: 503,
              contentType: "application/problem+json",
              body: JSON.stringify({
                status: 503,
                detail: "Draft retained for browser inspection.",
              }),
            });
          },
        },
      });
      await signInAs(page, ADMIN_FIXTURE_USER);
      await page.goto("/receipts/new", { waitUntil: "domcontentloaded" });
      await expect(
        page.getByRole("button", { name: "Submit Receipt", exact: true }),
      ).toHaveCount(1);
      const transactionCard = page.locator('[data-slot="card"]').filter({
        has: page.getByText("Transactions", { exact: true }),
      });
      const itemCard = page.locator('[data-slot="card"]').filter({
        has: page.getByText("Line Items", { exact: true }),
      });
      await expect(transactionCard).toHaveCount(1);
      await expect(itemCard).toHaveCount(1);
      const transactionBounds = await transactionCard.boundingBox();
      const itemBounds = await itemCard.boundingBox();
      expect(transactionBounds).not.toBeNull();
      expect(itemBounds).not.toBeNull();
      expect(
        transactionBounds!.y + transactionBounds!.height,
      ).toBeLessThanOrEqual(itemBounds!.y);
      if (known && (width === 1280 || width === 375))
        await page.screenshot({
          path: testInfo.outputPath(`entry-empty-${width}.png`),
          fullPage: true,
        });
      await page.getByRole("combobox", { name: /^Location/ }).click();
      await page.getByRole("option", { name: "Market", exact: true }).click();
      await page.getByPlaceholder("MM/DD/YYYY").first().fill("10/23/2026");
      await page.getByPlaceholder("Item description").fill("MILK");
      await page.getByRole("combobox", { name: /^Category/ }).click();
      await page.getByPlaceholder("Search category paths…").fill("Food Dairy");
      await page
        .getByRole("option", { name: "Food / Dairy", exact: true })
        .click();
      await expect(page.getByRole("listbox")).toHaveCount(0);
      await page.getByLabel("Unit Price", { exact: false }).fill("5");
      await page.keyboard.press("Escape");
      if (width >= 640) {
        const priceBounds = await page
          .getByLabel("Unit Price", { exact: false })
          .boundingBox();
        const addBounds = await page
          .getByRole("button", { name: "Add Item", exact: true })
          .boundingBox();
        expect(priceBounds).not.toBeNull();
        expect(addBounds).not.toBeNull();
        expect(Math.abs(priceBounds!.y - addBounds!.y)).toBeLessThanOrEqual(1);
        expect(
          Math.abs(priceBounds!.height - addBounds!.height),
        ).toBeLessThanOrEqual(1);
      }
      if (width <= 900) {
        const submit = page.getByRole("button", {
          name: "Submit Receipt",
          exact: true,
        });
        await expect(submit).toBeInViewport();
        const submitBounds = await submit.boundingBox();
        const navBounds = await page
          .getByRole("navigation", { name: "Mobile navigation" })
          .boundingBox();
        expect(submitBounds).not.toBeNull();
        expect(navBounds).not.toBeNull();
        expect(submitBounds!.y + submitBounds!.height).toBeLessThanOrEqual(
          navBounds!.y,
        );
        expect(
          await submit.evaluate((element) => {
            const bounds = element.getBoundingClientRect();
            const top = document.elementFromPoint(
              bounds.left + bounds.width / 2,
              bounds.top + bounds.height / 2,
            );
            return element.contains(top) && bounds.bottom <= innerHeight - 60;
          }),
        ).toBe(true);
      }
      if (known) {
        await expect(
          page.getByRole("region", { name: "Suggested products" }),
        ).toHaveCount(0);
        await page
          .getByRole("button", { name: "Choose product", exact: true })
          .click();
        const productDialog = page.getByRole("dialog", {
          name: "Choose a product",
        });
        await expect(productDialog).toBeVisible();
        const candidate = productDialog.getByRole("button", {
          name: "Choose suggested Whole milk 1 gallon",
          exact: true,
        });
        await expect(candidate).toBeVisible();
        if (width === 1280 || width === 375)
          await page.screenshot({
            path: testInfo.outputPath(`entry-inline-${width}.png`),
            fullPage: true,
          });
        await candidate.click();
        await expect(productDialog).toHaveCount(0);
        await expect(page.getByPlaceholder("Item description")).toHaveValue(
          "MILK",
        );
        await expect(
          page.getByText("Selected for this purchase:", { exact: false }),
        ).toBeVisible();
      } else {
        await expect(
          page.getByText(/Needs review|No matching product names/),
        ).toHaveCount(0);
      }
      expect(bodies).toHaveLength(0);
      await page.getByRole("button", { name: "Add Item", exact: true }).click();
      await page
        .getByRole("button", { name: "Add adjustment", exact: true })
        .click();
      const dialog = page.getByRole("dialog");
      await dialog.getByRole("combobox", { name: /^Type/ }).click();
      await page.getByRole("option", { name: "Discount", exact: true }).click();
      await dialog.getByRole("textbox", { name: /^Amount/ }).fill("-1");
      await dialog
        .getByRole("button", { name: "Add Adjustment", exact: true })
        .click();
      for (const account of ["Checking", "Savings"]) {
        await page.getByRole("combobox", { name: /^Card/ }).click();
        await page.getByPlaceholder("Search cards...").fill("1234");
        await expect(
          page.getByRole("option", { name: /Debit.*1234.*Checking/ }),
        ).toBeVisible();
        await expect(
          page.getByRole("option", { name: /Debit.*1234.*Savings/ }),
        ).toBeVisible();
        await page
          .getByRole("option", { name: new RegExp(`Debit.*1234.*${account}`) })
          .click();
        await expect(
          page.getByRole("combobox", { name: /^Account/ }),
        ).toContainText(account);
        await expect(
          page.getByRole("combobox", { name: /^Card/ }),
        ).toBeFocused();
        await page.keyboard.press("Tab");
        await expect(
          page.getByRole("combobox", { name: /^Account/ }),
        ).toBeFocused();
        await page.getByRole("textbox", { name: /^Amount/ }).fill("2");
        await page.getByRole("button", { name: "Add", exact: true }).click();
      }
      await expect
        .poll(() =>
          page.evaluate(
            () => document.documentElement.scrollWidth <= innerWidth,
          ),
        )
        .toBe(true);
      expect(bodies).toHaveLength(0);
      if (known && (width === 1280 || width === 375))
        await page.screenshot({
          path: testInfo.outputPath(`entry-filled-${width}.png`),
          fullPage: true,
        });
      await page
        .getByRole("button", { name: "Submit Receipt", exact: true })
        .click();
      await expect(page.getByRole("alert")).toContainText(
        "Draft retained for browser inspection.",
      );
      await expect(
        page.getByRole("combobox", { name: /^Location/ }),
      ).toContainText("Market");
      await expect(page.getByText("MILK", { exact: true })).toBeVisible();
      await expect(transactionCard).toContainText("Checking");
      await expect(transactionCard).toContainText("Savings");
      const balance = page.locator(".sticky.bottom-0");
      await expect(balance).toContainText("$4.00");
      await expect(balance).toContainText("Balanced");
      // Retrying the failed save must preserve the same draft, including calculations.
      await page
        .getByRole("button", { name: "Submit Receipt", exact: true })
        .click();
      await expect.poll(() => bodies.length).toBe(2);
      expect(bodies[1]).toEqual(bodies[0]);
      expect(bodies[0]).toMatchObject({
        items: [
          {
            description: "MILK",
            unitPrice: 5,
            classification: {
              categoryId: taxonomyFixture.nodes[1].id,
              taxonomyRevisionId: taxonomyFixture.id,
              expectedAssignmentId: null,
              operationId: expect.any(String),
            },
            ...(known
              ? {
                  normalizationIntent: {
                    kind: "confirmExisting",
                    targetId: productId,
                    expectedTargetRevision: revision,
                    sourceFingerprint: "milk-source",
                  },
                }
              : {}),
          },
        ],
        transactions: [
          { cardId: "card-a", amount: 2 },
          { cardId: "card-b", amount: 2 },
        ],
        adjustments: [{ type: "discount", amount: -1 }],
      });
    });
  }
}
