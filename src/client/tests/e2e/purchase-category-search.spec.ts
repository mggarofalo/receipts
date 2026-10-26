import { expect, test } from "@playwright/test";
import { taxonomyFixture } from "../../src/test/taxonomy-fixture";
import { installApiMocks, signInAsFixtureUser } from "../fixtures/api-mocks";

const categoryId = taxonomyFixture.nodes[1].id;

for (const width of [1280, 375]) {
  test(`category-only purchase history works with keyboard at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 812 });
    const requests: URL[] = [];
    await installApiMocks(page, {
      freezeClock: false,
      overrides: {
        "**/api/search/purchases?*": async (route) => {
          const url = new URL(route.request().url());
          requests.push(url);
          await route.fulfill({
            contentType: "application/json",
            body: JSON.stringify({
              categoryView:
                url.searchParams.get("categoryView") ?? "asClassified",
              assignmentSelection: "currentPointer",
              currentRevisionId: taxonomyFixture.id,
              total: 30,
              offset: Number(url.searchParams.get("offset") ?? 0),
              limit: 25,
              items: [
                {
                  id: "10000000-0000-4000-8000-000000000001",
                  receiptId: "20000000-0000-4000-8000-000000000001",
                  date: "2026-05-01",
                  description: "Whole milk",
                  location: "Market",
                  quantity: 1,
                  unitPrice: 3.5,
                  unitPriceCurrency: "USD",
                  totalAmount: 3.5,
                  totalAmountCurrency: "USD",
                  categoryState: "classified",
                  categoryPath: [
                    {
                      categoryId: taxonomyFixture.nodes[0].id,
                      name: "Food",
                      archived: false,
                    },
                    { categoryId, name: "Dairy", archived: false },
                  ],
                },
              ],
            }),
          });
        },
      },
    });
    await signInAsFixtureUser(page);
    await page.goto(
      `/search?categoryId=${categoryId}&categoryView=currentHierarchy&location=Market`,
      { waitUntil: "domcontentloaded" },
    );
    await expect(
      page.getByRole("table", { name: "Purchase results" }),
    ).toContainText("Food / Dairy");
    await expect
      .poll(() =>
        new URL(page.url()).searchParams.get("capturedCurrentRevisionId"),
      )
      .toBe(taxonomyFixture.id);
    expect(requests.at(-1)?.searchParams.get("q")).toBe("");
    expect(requests.at(-1)?.searchParams.get("categoryId")).toBe(categoryId);

    await page.getByRole("button", { name: "Next page" }).click();
    await expect
      .poll(() => requests.at(-1)?.searchParams.get("offset"))
      .toBe("25");
    expect(requests.at(-1)?.searchParams.get("capturedCurrentRevisionId")).toBe(
      taxonomyFixture.id,
    );

    const picker = page.getByRole("combobox", {
      name: "Category",
      exact: true,
    });
    await picker.focus();
    await page.keyboard.press("Enter");
    await expect(page.getByPlaceholder("Search category paths…")).toBeFocused();
    await page.getByPlaceholder("Search category paths…").fill("Food");
    await expect(
      page.getByRole("option", { name: /Food \/ Dairy/ }),
    ).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(picker).toBeFocused();

    await page.getByLabel("Category view").selectOption("asClassified");
    await expect
      .poll(() => requests.at(-1)?.searchParams.get("categoryView"))
      .toBe("asClassified");
    expect(new URL(page.url()).searchParams.get("offset")).toBeNull();
    expect(
      new URL(page.url()).searchParams.get("capturedCurrentRevisionId"),
    ).toBeNull();
    expect(new URL(page.url()).searchParams.get("location")).toBe("Market");

    await page.getByText("Historical assignment cutoff").click();
    await page
      .getByLabel("Assignments known by (UTC)")
      .fill("2026-05-01T12:30");
    await expect
      .poll(() => requests.at(-1)?.searchParams.get("knowledgeCutoff"))
      .toBe("2026-05-01T12:30:00.000Z");
    expect(new URL(page.url()).searchParams.get("location")).toBe("Market");
    expect(
      await page.evaluate(
        () =>
          document.documentElement.scrollWidth <=
          document.documentElement.clientWidth + 1,
      ),
    ).toBe(true);
  });
}
