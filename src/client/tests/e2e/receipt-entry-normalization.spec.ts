import { taxonomyFixture } from "../../src/test/taxonomy-fixture";
import { expect, test } from "@playwright/test";
import {
  ADMIN_FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";

test("creates a product and purchase in one receipt save, preserving the draft after a rejected save", async ({
  page,
}) => {
  const bodies: unknown[] = [];
  await installApiMocks(page, {
    freezeClock: false,
    user: ADMIN_FIXTURE_USER,
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
      "**/api/receipts/locations*": (route) =>
        route.fulfill({ json: { locations: ["Market"] } }),
      "**/api/accounts**": (route) =>
        route.fulfill({
          json: {
            data: [{ id: "account-a", name: "Checking", isActive: true }],
            total: 1,
          },
        }),
      "**/api/accounts/account-a/cards": (route) =>
        route.fulfill({
          json: [
            {
              id: "card-a",
              name: "Debit",
              cardCode: "1234",
              accountId: "account-a",
              isActive: true,
            },
          ],
        }),
      "**/api/taxonomy": (route) => route.fulfill({ json: taxonomyFixture }),
      "**/api/item-templates/similar*": (route) => route.fulfill({ json: [] }),
      "**/api/receipt-items/suggestions*": (route) =>
        route.fulfill({ json: [] }),
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
      "**/api/trips?*": (route) =>
        route.fulfill({
          json: {
            receipt: {
              receipt: {
                id: "created-receipt",
                location: "Market",
                date: "2026-10-23",
                taxAmount: 0,
              },
              subtotal: 5,
              adjustmentTotal: 0,
              expectedTotal: 5,
              adjustments: [],
              items: [
                {
                  id: "saved-line",
                  description: "MILK",
                  receiptItemCode: "",
                  quantity: 1,
                  unitPrice: 5,
                  category: "Food",
                  normalizationReviewState: "verified",
                  normalizedDescriptionName: "Whole milk 1 gallon",
                  normalizationRevision: "22222222-2222-4222-8222-222222222222",
                },
              ],
            },
            transactions: [],
          },
        }),
      "**/api/receipts/complete": (route) => {
        bodies.push(route.request().postDataJSON());
        return bodies.length === 1
          ? route.fulfill({
              status: 503,
              contentType: "application/problem+json",
              body: JSON.stringify({
                status: 503,
                detail: "Temporary save failure.",
              }),
            })
          : route.fulfill({
              json: {
                receipt: { id: "created-receipt" },
                items: [{ normalizationReviewState: "verified" }],
                transactions: [],
                adjustments: [],
              },
            });
      },
    },
  });
  await signInAs(page, ADMIN_FIXTURE_USER);
  await page.goto("/receipts/new");
  await page.getByRole("combobox").first().click();
  await page.getByRole("option", { name: "Market", exact: true }).click();
  await page.getByPlaceholder("MM/DD/YYYY").first().fill("10/23/2026");
  await page.getByPlaceholder("Item description").fill("MILK");
  await page.getByRole("combobox", { name: /^Category/ }).click();
  await page.getByRole("option", { name: "Food / Dairy", exact: true }).click();
  await page.getByLabel("Unit Price", { exact: false }).fill("5");
  await page
    .getByRole("button", { name: "Choose product", exact: true })
    .click();
  await expect(page.getByRole("dialog", { name: "Choose a product" })).toBeVisible();
  await page.getByRole("checkbox", { name: /Remember this exact description/ }).check();
  await page.getByLabel("New product name").fill("Whole milk 1 gallon");
  await page.getByRole("button", { name: "New product and use it" }).click();
  await expect(page.getByRole("dialog", { name: "Choose a product" })).toHaveCount(0);
  await expect(page.getByPlaceholder("Item description")).toHaveValue("MILK");
  await page.getByRole("button", { name: "Add Item", exact: true }).click();
  await page.getByRole("combobox", { name: /^Account/ }).click();
  await page.getByRole("option", { name: "Checking", exact: true }).click();
  await page.getByRole("combobox", { name: /^Card/ }).click();
  await page.getByRole("option", { name: /Debit/ }).click();
  await page.getByRole("textbox", { name: /^Amount/ }).fill("5");
  await page.getByPlaceholder("MM/DD/YYYY").last().fill("10/23/2026");
  await page.getByRole("button", { name: "Add", exact: true }).click();
  expect(bodies).toHaveLength(0);
  await page
    .getByRole("button", { name: "Submit Receipt", exact: true })
    .first()
    .click();
  await expect(page.getByRole("alert")).toContainText(
    "Temporary save failure.",
  );
  expect(bodies).toHaveLength(1);
  expect(bodies[0]).toMatchObject({
    clientRequestId: expect.stringMatching(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    ),
    receipt: { location: "Market" },
    items: [
      {
        description: "MILK",
        quantity: 1,
        unitPrice: 5,
        classification: {
          categoryId: taxonomyFixture.nodes[1].id,
          taxonomyRevisionId: taxonomyFixture.id,
          expectedAssignmentId: null,
          operationId: expect.any(String),
        },
        normalizationIntent: {
          kind: "confirmNew",
          newName: "Whole milk 1 gallon",
          sourceFingerprint: "milk-source",
          rememberSource: true,
        },
      },
    ],
  });
  await expect(
    page.getByText("Whole milk 1 gallon", { exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Submit Receipt", exact: true })
    .first()
    .click();
  await expect.poll(() => bodies.length).toBe(2);
  expect(bodies[1]).toEqual(bodies[0]);
  await expect(page).toHaveURL(/\/receipts\/created-receipt$/);
  await expect(
    page.getByRole("cell", {
      name: "MILK Verified normalized as Whole milk 1 gallon",
      exact: true,
    }),
  ).toBeVisible();
});

// Real browser/router/forms with synthetic HTTP; persistence is separately proved by PostgreSQL tests.
for (const width of [1280, 375]) {
  test(`receipt-first product choices are keyboard usable without overflow at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 900 });
    let writes = 0;
    await installApiMocks(page, {
      freezeClock: false,
      user: ADMIN_FIXTURE_USER,
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
        "**/api/receipt-items/similar*": (route) => route.fulfill({ json: [] }),
        "**/api/receipt-items/suggestions*": (route) =>
          route.fulfill({ json: [] }),
        "**/api/item-templates/similar*": (route) =>
          route.fulfill({ json: [] }),
        "**/api/receipts/locations*": (route) =>
          route.fulfill({ json: { locations: ["Market"] } }),
        "**/api/receipt-items/normalization-preview": (route) =>
          route.fulfill({
            json: {
              sourceFingerprint: "milk-source",
              sourceAmbiguous: false,
              excluded: false,
              candidates: [
                {
                  id: "11111111-1111-4111-8111-111111111111",
                  revision: "22222222-2222-4222-8222-222222222222",
                  name: "Whole milk 1 gallon",
                },
              ],
              total: 1,
              offset: 0,
              limit: 10,
            },
          }),
        "**/api/receipts/complete": (route) => {
          writes++;
          return route.fulfill({ json: {} });
        },
      },
    });
    await signInAs(page, ADMIN_FIXTURE_USER);
    await page.goto("/receipts/new");
    await page.getByRole("combobox").first().click();
    await page.getByRole("option", { name: "Market", exact: true }).click();
    await page.getByPlaceholder("Item description").fill("MILK");
    const choose = page.getByRole("button", {
      name: "Choose product",
      exact: true,
    });
    await expect(choose).toBeVisible();
    await expect(page.getByRole("dialog", { name: "Choose a product" })).toHaveCount(0);
    await expect(page.getByRole("region", { name: "Suggested products" })).toHaveCount(0);
    await expect(page.getByLabel("New product name")).toHaveCount(0);
    const addItem = page.getByRole("button", { name: "Add Item", exact: true, includeHidden: true });
    await choose.focus();
    await expect(choose).toBeFocused();
    // Focusing the trigger may scroll the mobile form. Compare after focus so
    // this assertion measures dialog layout stability rather than page scrolling.
    const positionBeforeDialog = await addItem.boundingBox();
    expect(positionBeforeDialog).not.toBeNull();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Choose a product" });
    await expect(dialog).toBeVisible();
    await expect.poll(() => dialog.evaluate((element) => element.contains(document.activeElement))).toBe(true);
    await page.keyboard.press("Shift+Tab");
    await expect.poll(() => dialog.evaluate((element) => element.contains(document.activeElement))).toBe(true);
    expect(await addItem.boundingBox()).toEqual(positionBeforeDialog);
    await page.getByLabel("Search all product names").fill("Whole milk");
    await expect(
      page.getByRole("button", { name: "Use Whole milk 1 gallon" }),
    ).toBeVisible();
    await page.getByLabel("New product name").fill("Organic whole milk");
    await page.getByLabel("New product name").press("Enter");
    expect(writes).toBe(0);
    await expect
      .poll(() =>
        page.evaluate(() =>
          Array.from(document.querySelectorAll("*"), (element) => {
            const box = element.getBoundingClientRect();
            return {
              tag: element.tagName,
              className: element.className,
              text: element.textContent?.slice(0, 60),
              left: box.left,
              right: box.right,
              width: box.width,
            };
          }).filter((box) => box.right > innerWidth + 1 && box.width > 0),
        ),
      )
      .toEqual([]);
    await page.getByRole("button", { name: "New product and use it" }).focus();
    await page.keyboard.press("Enter");
    await expect(page.getByText(/Organic whole milk/)).toBeVisible();
    await expect(page.getByRole("dialog", { name: "Choose a product" })).toHaveCount(0);
    await expect(page.getByPlaceholder("Item description")).toHaveValue("MILK");
    const changeProduct = page.getByRole("button", { name: "Change product", exact: true });
    await expect(changeProduct).toBeFocused();
    await page.keyboard.press("Enter");
    await page.getByLabel("New product name").fill("Discard this replacement");
    await page.getByRole("checkbox", { name: /Remember this exact description/ }).check();
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog", { name: "Choose a product" })).toHaveCount(0);
    await expect(changeProduct).toBeFocused();
    await expect(page.getByText(/Selected for this purchase: Organic whole milk/)).toBeVisible();
    await expect(page.getByPlaceholder("Item description")).toHaveValue("MILK");
    await page.keyboard.press("Enter");
    await expect(page.getByLabel("New product name")).toHaveValue("");
    await expect(page.getByRole("checkbox", { name: /Remember this exact description/ })).not.toBeChecked();
    await page.getByRole("button", { name: "Close", exact: true }).click();
    await expect(page.getByText(/Selected for this purchase: Organic whole milk/)).toBeVisible();
    expect(writes).toBe(0);
    const overflow = await page.evaluate(() =>
      Array.from(document.querySelectorAll("*"), (element) => ({
        tag: element.tagName,
        className: element.className,
        text: element.textContent?.slice(0, 60),
        left: element.getBoundingClientRect().left,
        right: element.getBoundingClientRect().right,
        width: element.getBoundingClientRect().width,
      })).filter((box) => box.right > innerWidth + 1 && box.width > 0),
    );
    expect(overflow, JSON.stringify(overflow.slice(-20))).toEqual([]);
    await page.getByPlaceholder("Item description").fill("BREAD");
    await expect(page.getByText(/Organic whole milk/)).toHaveCount(0);
    await expect(
      page.getByRole("button", { name: "Choose product", exact: true }),
    ).toBeVisible();
  });
}

for (const selection of ["click", "keyboard"] as const) {
  test(`selects an inline product by ${selection} and submits the unchanged receipt description`, async ({ page }) => {
    const bodies: unknown[] = [];
    const providerRequests: string[] = [];
    page.on("request", (request) => {
      const url = new URL(request.url());
      if (/chatgpt|openai|typesafe|jev/i.test(url.hostname) ||
        (url.pathname.startsWith("/api/") && /chatgpt|openai|typesafe|jev/i.test(url.pathname))) {
        providerRequests.push(request.url());
      }
    });
    const product = {
      id: "11111111-1111-4111-8111-111111111111",
      revision: "22222222-2222-4222-8222-222222222222",
      canonicalName: "Whole milk 1 gallon", displayLabel: null, channels: ["lexical"],
    };
    const raw = "MILK  1gal #42";
    await installApiMocks(page, {
      freezeClock: false, user: ADMIN_FIXTURE_USER,
      overrides: {
        "**/api/metadata/enums": (route) => route.fulfill({ json: { adjustmentTypes: [], authEventTypes: [], auditActions: [], entityTypes: [] } }),
        "**/api/receipts/locations*": (route) => route.fulfill({ json: { locations: ["Market"] } }),
        "**/api/taxonomy": (route) => route.fulfill({ json: taxonomyFixture }),
        "**/api/accounts**": (route) => route.fulfill({ json: { data: [{ id: "account-a", name: "Checking", isActive: true }], total: 1 } }),
        "**/api/accounts/account-a/cards": (route) => route.fulfill({ json: [{ id: "card-a", name: "Debit", cardCode: "1234", accountId: "account-a", isActive: true }] }),
        "**/api/item-templates/similar*": (route) => route.fulfill({ json: [{ name: product.canonicalName, similarity: 0.9, semanticSimilarity: null, combinedScore: 0.9, source: "history" }] }),
        "**/api/receipt-items/suggestions*": (route) => route.fulfill({ json: [] }),
        "**/api/receipt-items/normalization-candidates*": (route) => route.fulfill({ json: [product] }),
        "**/api/receipt-items/normalization-preview": (route) => route.fulfill({ json: { sourceFingerprint: "inline-source-from-server", sourceAmbiguous: false, excluded: false, candidates: [], total: 0, offset: 0, limit: 10 } }),
        "**/api/receipts/complete": (route) => {
          bodies.push(route.request().postDataJSON());
          return route.fulfill({ status: 503, contentType: "application/problem+json", json: { status: 503, detail: "Synthetic save rejection retains the draft." } });
        },
      },
    });
    await signInAs(page, ADMIN_FIXTURE_USER);
    await page.goto("/receipts/new");
    await page.getByRole("combobox").first().click();
    await page.getByRole("option", { name: "Market", exact: true }).click();
    await page.getByPlaceholder("MM/DD/YYYY").first().fill("10/23/2026");
    const description = page.getByPlaceholder("Item description");
    await description.fill(raw);
    const option = page.getByRole("option", { name: /Whole milk 1 gallon/ });
    await expect(option).toContainText("Product");
    await expect(option).toHaveCount(1);
    await expect(page.getByText(/Selected for this purchase:/)).toHaveCount(0);
    if (selection === "click") await option.click();
    else {
      await description.press("ArrowDown");
      await description.press("ArrowUp");
      await description.press("Enter");
    }
    await expect(description).toHaveValue(raw);
    await expect(page.getByRole("dialog", { name: "Choose a product" })).toHaveCount(0);
    await expect(page.getByText(/Selected for this purchase:/)).toContainText(product.canonicalName);
    await page.getByRole("combobox", { name: /^Category/ }).click();
    await page.getByRole("option", { name: "Food / Dairy", exact: true }).click();
    await page.getByLabel("Unit Price", { exact: false }).fill("5");
    await page.getByRole("button", { name: "Add Item", exact: true }).click();
    await page.getByRole("combobox", { name: /^Account/ }).click();
    await page.getByRole("option", { name: "Checking", exact: true }).click();
    await page.getByRole("combobox", { name: /^Card/ }).click();
    await page.getByRole("option", { name: /Debit/ }).click();
    await page.getByRole("textbox", { name: /^Amount/ }).fill("5");
    await page.getByPlaceholder("MM/DD/YYYY").last().fill("10/23/2026");
    await page.getByRole("button", { name: "Add", exact: true }).click();
    expect(bodies).toHaveLength(0);
    await page.getByRole("button", { name: "Submit Receipt", exact: true }).first().click();
    await expect.poll(() => bodies.length).toBe(1);
    expect(bodies[0]).toMatchObject({
      receipt: { location: "Market" },
      items: [{ description: raw, normalizationIntent: {
        kind: "confirmExisting", targetId: product.id,
        expectedTargetRevision: product.revision,
        sourceFingerprint: "inline-source-from-server", rememberSource: false,
      } }],
    });
    expect(providerRequests).toEqual([]);
  });
}

